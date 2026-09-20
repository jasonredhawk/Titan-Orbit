using System.Reflection;
using TitanOrbit;
using TitanOrbit.Core;
using TitanOrbit.Shared;
using Unity.Entities;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Input;
using Unity.Mathematics;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// MonoBehaviour bridge from Unity's Update loop to ECS input. Captures keyboard/mouse via
    /// PlayerInputHandler and writes into <see cref="ShipPendingInput"/>, which
    /// <see cref="ShipInputApplySystem"/> reads during GhostInputSystemGroup on the client world.
    /// <para>
    /// B-key cycles the bullet bank: walks the same list the Weapons HUD paints, latches
    /// <see cref="ShipInput.SetBulletBank"/> (so fixed-tick NetCode does not miss
    /// <c>WasPressedThisFrame</c>), and relies on <see cref="ShipCycleBulletSystem"/> +
    /// baked <see cref="ShipLoadoutState"/> for the sticky index. The HUD caret is the
    /// only player-facing feedback — no world-space category name.
    /// V-key hold sets <see cref="ShipInput.WantExpelGems"/> so the server dumps cargo
    /// forward of the hull. T-key (when GameManager Cycle All Thruster VFX is on) walks
    /// <see cref="ThrusterVfxBank"/> on live ship proxies only — no ghost / RPC.
    /// During idle theatrical orbit, aim is held (zero <c>AimPlanarDir</c>) so the
    /// mouse does not yaw the hull. Gameplay follow always unprojects the cursor.
    /// </para>
    /// </summary>
    [DefaultExecutionOrder(-10000)]
    public class ShipInputBridge : MonoBehaviour
    {
        /// <summary>Optional floating-name prefab (SimpleFloatingText). T-key thruster debug only. Loaded from Prefabs/Ships in Editor if unset.</summary>
        [SerializeField] GameObject bulletNameTextPrefab;

        PlayerInputHandler _input;
        ThrusterVfxBank _thrusterBank;
        Camera _cachedCamera;

        static GameObject s_ThrusterCycleLabel;
        static int s_LastThrusterCycleFrame = -1;
        static ShipInputBridge s_Active;

        void Awake()
        {
            // SampleScene had a leftover ShipInputBridge plus NceGameRoot — both
            // saw T and spawned overlapping family / prefab labels.
            if (s_Active != null && s_Active != this)
            {
                enabled = false;
                return;
            }

            s_Active = this;
        }

        void OnDestroy()
        {
            if (s_Active == this)
                s_Active = null;
        }

        /// <summary>[UNITY] Resolve input handler + optional T-key thruster label prefab.</summary>
        void Start()
        {
            _input = FindAnyObjectByType<PlayerInputHandler>();
            _thrusterBank = ThrusterVfxBank.LoadDefault();

#if UNITY_EDITOR
            // --- Editor convenience: wire floating text prefab without scene plumbing ---
            if (bulletNameTextPrefab == null)
            {
                bulletNameTextPrefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(
                    "Assets/Prefabs/Ships/BulletNameTextPrefab.prefab");
            }
#endif
        }

        /// <summary>Each frame: latch B if pressed, publish ShipInput, keep thruster debug labels.</summary>
        void Update()
        {
            // --- Per-frame refresh ---
            if (_input == null)
                return;

            // --- B / CycleBullet ---
            // [TITAN-ORBIT] Orbit Menu and turret pads do not cycle hull guns. Prefer
            // SetBulletBank for the next Weapons-HUD row so the caret and the ghost
            // write the same index. CycleBullet increment is only a fallback when we
            // cannot see the local ship yet (join / Instantiates).
            bool cyclePressed = _input.CycleBulletPressed
                && !MoonOrbitClientState.IsOrbitMenuVisible
                && !PlanetaryDefenseTurretClientState.IsControlling;

            if (cyclePressed && !TryRequestNextVisibleWeapon())
                ShipPendingInput.LatchCycleBullet();

            // --- ALT activates the focused loadout pack ---
            // [TITAN-ORBIT] One key for the whole left-side list. UP/DOWN (and clicks)
            // move the caret across rocket levels and mine packs. When the caret sits on
            // MINES, ALT places that mine. Otherwise ALT fires the selected rocket.
            // E does not place mines — it used to, which made the HUD show two hotkeys.
            bool activatePressed = _input.RocketPressed
                && !MoonOrbitClientState.IsOrbitMenuVisible
                && !PlanetaryDefenseTurretClientState.IsControlling;
            bool rocketPressed = activatePressed && !MineSlotSelection.HudFocused;
            bool minePressed = activatePressed && MineSlotSelection.HudFocused;

            if (rocketPressed)
                ShipPendingInput.LatchFireRocket();
            if (minePressed)
                ShipPendingInput.LatchPlaceMine();

            if (TitanOrbitDebugFlags.CycleAllThrusterVfx
                && _input.CycleThrusterVfxPressed
                && !MoonOrbitClientState.IsOrbitMenuVisible)
            {
                TryCycleThrusterVfx();
            }

            bool setBankPressed = ShipPendingInput.SetBulletBankLatched
                && !MoonOrbitClientState.IsOrbitMenuVisible
                && !PlanetaryDefenseTurretClientState.IsControlling;

            ShipPendingInput.Set(
                BuildInput(rocketPressed, minePressed, setBankPressed),
                localHostMode: false);
        }

        /// <summary>
        /// Converts PlayerInputHandler state into a ShipInput struct for ECS consumption.
        /// Aim direction is computed from mouse world position relative to local ship.
        /// </summary>
        /// <param name="setBankPressedThisFrame">True when B or a Weapons tile latched a bank.</param>
        ShipInput BuildInput(
            bool rocketPressedThisFrame,
            bool minePressedThisFrame,
            bool setBankPressedThisFrame)
        {
            // --- Build data ---
            // Cache Camera.main — looking it up every frame was part of a ~4ms Update (Profiler 41220).
            if (_cachedCamera == null)
                _cachedCamera = UnityEngine.Camera.main;
            var cam = _cachedCamera;
            float2 aimDir = float2.zero;
            float aimDistance = 0f;
            // [HYBRID] Prefer presentation pose (already synced) before ECS ship queries.
            // [TITAN-ORBIT] TryGet… — MPPM Player 2 often has NaN mouse until that Game view is focused;
            // leaving aimDir at zero keeps current facing (ShipPhysicsDriveLogic.AimWorldPoint).
            // [TITAN-ORBIT] Turret control aims from the pad pose (hull is stowed/hidden).
            bool turretControl = PlanetaryDefenseTurretClientState.IsControlling;

            // [TITAN-ORBIT] Theatrical: do not unproject. A still-or-moving cursor plus
            // the orbiting camera would slide the world aim point and yaw the hull.
            // Zero AimPlanarDir → motor keeps current facing. Gameplay always aims.
            bool freezeAimForTheatrical = CameraFollowEcs.Instance != null
                && CameraFollowEcs.Instance.IsTheatricalEngaged;
            if (!freezeAimForTheatrical &&
                cam != null &&
                _input.TryGetMouseWorldPosition(cam, out Vector3 aimWorld))
            {
                Vector3 shipPos = Vector3.zero;
                if (turretControl && PlanetaryDefenseTurretClientState.HasPadWorldPosition)
                    shipPos = PlanetaryDefenseTurretClientState.PadWorldPosition;
                else if (ShipDisplayPose.HasLocalPose)
                    shipPos = ShipDisplayPose.LocalPosition;
                else if (!EcsGameBridge.TryGetLocalShipPosition(out shipPos))
                    shipPos = Vector3.zero;
                Vector3 toAim = aimWorld - shipPos;
                toAim.y = 0f;
                if (toAim.sqrMagnitude > 0.001f)
                {
                    // Unit direction for hull yaw; distance so MEGA barrels can
                    // converge on the cursor instead of firing parallel.
                    aimDistance = toAim.magnitude;
                    Vector3 dir = toAim / aimDistance;
                    aimDir = new float2(dir.x, dir.z);
                }
            }

            // While controlling a turret, RMB thrust is the exit signal (server ejects).
            bool thrust = _input.MoveForwardPressed;

            // [NETCODE] InputEvent.Set() marks fire as pressed this tick (one-shot for ghost input).
            // [TITAN-ORBIT] Orbit menu and the hold-S comms matrix both use LMB on HUD tiles —
            // those clicks must not also shoot.
            var fire = new InputEvent();
            if (_input.ShootPressed
                && !MoonOrbitClientState.IsOrbitMenuVisible
                && !ShipCommsClientState.IsOpen)
                fire.Set();

            // [TITAN-ORBIT] B fallback increment only. The usual path is SetBulletBank
            // from TryRequestNextVisibleWeapon — putting CycleBullet.IsSet on the same
            // tick would step twice (click-next AND increment).
            var cycleBullet = new InputEvent();
            if (!turretControl && ShipPendingInput.CycleBulletLatched)
                cycleBullet.Set();

            var fireRocket = new InputEvent();
            if (!turretControl && (rocketPressedThisFrame || ShipPendingInput.FireRocketLatched))
                fireRocket.Set();

            var placeMine = new InputEvent();
            if (!turretControl && (minePressedThisFrame || ShipPendingInput.PlaceMineLatched))
                placeMine.Set();

            // [TITAN-ORBIT] HUD tile click — one-shot set, not a sticky every-tick index.
            // Sticky SelectedBulletBank would fight B-key increment on the next ticks.
            var setBulletBank = new InputEvent();
            if (!turretControl && (setBankPressedThisFrame || ShipPendingInput.SetBulletBankLatched))
                setBulletBank.Set();

            // [TITAN-ORBIT] Shift alone (not AND thrust). Regular ships: OVERDRIVE
            // latch + burst while thrusting. MEGAs: same bit is heading-lock / unoccupied
            // auto-gun mouse-aim (no speed burst). Clear while stowed so prediction
            // does not fight turret possession.
            bool overdrive = !turretControl && _input.OverdriveHeld;

            // [TITAN-ORBIT] Hold V to dump cargo. Server pulses while this bit stays true.
            bool wantExpelGems = !turretControl
                && !MoonOrbitClientState.IsOrbitMenuVisible
                && _input.ExpelGemsHeld;

            return new ShipInput
            {
                AimPlanarDir = aimDir,
                MovePlanarDir = float2.zero,
                Thrust = thrust,
                Overdrive = overdrive,
                Fire = fire,
                CycleBullet = cycleBullet,
                FireRocket = fireRocket,
                DisableSpaceBrakes = !_input.SpaceBrakesEnabled,
                WantDepositGems = MoonOrbitClientState.WantDepositGems,
                SelectedRocketSlot = RocketSlotSelection.SelectedIndex,
                PlaceMine = placeMine,
                SelectedMineSlot = MineSlotSelection.SelectedIndex,
                AimDistance = aimDistance,
                SetBulletBank = setBulletBank,
                SelectedBulletBank = BulletBankSelection.RequestedBankIndex,
                WantExpelGems = wantExpelGems,
            };
        }

        /// <summary>
        /// Walks the Weapons HUD list and latches that bank as a HUD-style set.
        /// Heal mode (Orbit Menu) eats the press so B does not also increment.
        /// Returns false only when we cannot see the local ship — caller then
        /// falls back to <see cref="ShipPendingInput.LatchCycleBullet"/>.
        /// </summary>
        /// <returns>True when B was handled (cycled, locked, or no-op on a one-row list).</returns>
        bool TryRequestNextVisibleWeapon()
        {
            // --- Local ship on the client world ---
            var world = EcsGameBridge.ClientWorld;
            if (world == null || !world.IsCreated)
                return false;
            if (!EcsGameBridge.TryGetLocalShipEntityOnWorld(world, out Entity ship) ||
                ship == Entity.Null)
                return false;

            // --- Heal lock / current index ---
            // [TITAN-ORBIT] Production heal ignores B. Treat as handled so we do not
            // latch CycleBullet, which the cycle system would also ignore.
            int runtime = 0;
            if (EcsGameBridge.TryGetLocalShipLoadout(out ShipLoadoutState loadout))
            {
                if (loadout.HealingBulletsActive && !TitanOrbitDebugFlags.CycleAllBulletBanks)
                    return true;
                runtime = loadout.RuntimeBulletIndex;
            }

            // --- Next HUD row ---
            // Prefer the optimistic caret so rapid B walks the painted list
            // even before RuntimeBulletIndex catches up.
            int current = BulletBankSelection.ResolveCaretBank(runtime);
            int next = BulletBankOwnership.NextVisibleBank(world.EntityManager, ship, current);
            BulletBankSelection.Request(next);
            return true;
        }

        /// <summary>
        /// Advances <see cref="ThrusterVfxBank.DebugCycleIndex"/> and rebuilds live jets.
        /// Client presentation only — does not write ship input or ghosts.
        /// </summary>
        void TryCycleThrusterVfx()
        {
            if (s_LastThrusterCycleFrame == Time.frameCount)
                return;
            s_LastThrusterCycleFrame = Time.frameCount;

            if (_thrusterBank == null)
                _thrusterBank = ThrusterVfxBank.LoadDefault();
            if (_thrusterBank == null || _thrusterBank.EntryCount < 1)
                return;

            int index = _thrusterBank.CycleDebugIndex();
            var style = LocalPlayerThrusterStyle.Get();
            style.HasCustom = 1;
            style.FollowTeam = 1;
            style.StyleIndex = (byte)index;
            LocalPlayerThrusterStyle.Set(style);
            ShipPropulsionVisualApplier.RebuildAllLive();
            string familyName = _thrusterBank.GetDisplayName(index);
            string thrusterName = _thrusterBank.GetThrusterPrefabDisplayName(index);
            if (string.IsNullOrEmpty(familyName) && string.IsNullOrEmpty(thrusterName))
                return;

            if (bulletNameTextPrefab == null)
            {
                Debug.Log($"[ThrusterVfx] {index}: {familyName} / {thrusterName}");
                return;
            }

            if (!EcsGameBridge.TryGetLocalShipPosition(out Vector3 shipPos))
                return;

            DestroyThrusterCycleLabel();

            Vector3 pos = shipPos + Vector3.up * 5f;
            s_ThrusterCycleLabel = Instantiate(bulletNameTextPrefab, pos, Quaternion.identity);
            s_ThrusterCycleLabel.name = "_ThrusterCycleLabel";
            if (!TryInitializeFloatingText(s_ThrusterCycleLabel, familyName, thrusterName, Color.white, 2.4f))
                TryInitializeFloatingText(s_ThrusterCycleLabel, familyName, Color.white, 2.4f);
        }

        static void DestroyThrusterCycleLabel()
        {
            if (s_ThrusterCycleLabel != null)
            {
                s_ThrusterCycleLabel.SetActive(false);
                Destroy(s_ThrusterCycleLabel);
                s_ThrusterCycleLabel = null;
            }

            // Orphans from a second ShipInputBridge or a deferred Destroy.
            // Hide + rename so Find cannot see the same object again this frame.
            GameObject leftover = GameObject.Find("_ThrusterCycleLabel");
            while (leftover != null)
            {
                leftover.SetActive(false);
                leftover.name = "_ThrusterCycleLabel_Dead";
                Destroy(leftover);
                leftover = GameObject.Find("_ThrusterCycleLabel");
            }
        }

        /// <summary>
        /// Invokes two-line <c>SimpleFloatingText.Initialize(title, subtitle, …)</c> when present.
        /// </summary>
        static bool TryInitializeFloatingText(
            GameObject go,
            string title,
            string subtitle,
            Color color,
            float duration)
        {
            if (go == null)
                return false;

            foreach (MonoBehaviour script in go.GetComponents<MonoBehaviour>())
            {
                if (script == null || script.GetType().Name != "SimpleFloatingText")
                    continue;

                MethodInfo init = script.GetType().GetMethod(
                    "Initialize",
                    BindingFlags.Instance | BindingFlags.Public,
                    binder: null,
                    types: new[] { typeof(string), typeof(string), typeof(Color), typeof(float) },
                    modifiers: null);
                if (init == null)
                    return false;
                init.Invoke(script, new object[] { title, subtitle, color, duration });
                return true;
            }

            return false;
        }

        /// <summary>
        /// Invokes <c>SimpleFloatingText.Initialize</c> when that component is on the prefab.
        /// </summary>
        static void TryInitializeFloatingText(GameObject go, string message, Color color, float duration)
        {
            if (go == null)
                return;

            foreach (MonoBehaviour script in go.GetComponents<MonoBehaviour>())
            {
                if (script == null || script.GetType().Name != "SimpleFloatingText")
                    continue;

                MethodInfo init = script.GetType().GetMethod(
                    "Initialize",
                    BindingFlags.Instance | BindingFlags.Public,
                    binder: null,
                    types: new[] { typeof(string), typeof(Color), typeof(float) },
                    modifiers: null);
                init?.Invoke(script, new object[] { message, color, duration });
                return;
            }
        }
    }
}
