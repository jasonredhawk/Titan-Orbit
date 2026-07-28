using System.Reflection;
using TitanOrbit.Core;
using TitanOrbit.Shared;
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
    /// B-key cycles the bullet bank: latches the press (so fixed-tick NetCode does not miss
    /// <c>WasPressedThisFrame</c>), shows floating category name, and relies on
    /// <see cref="ShipCycleBulletSystem"/> + baked <see cref="ShipLoadoutState"/> for the sticky index.
    /// </para>
    /// </summary>
    [DefaultExecutionOrder(-10000)]
    public class ShipInputBridge : MonoBehaviour
    {
        /// <summary>Optional floating-name prefab (SimpleFloatingText). Loaded from Prefabs/Ships in Editor if unset.</summary>
        [SerializeField] GameObject bulletNameTextPrefab;

        PlayerInputHandler _input;
        BulletVfxBank _bank;
        Camera _cachedCamera;

        /// <summary>
        /// Client-side display index for floating text. Advanced on each B press so the label
        /// always matches the cycle even before the ghost snapshot arrives. Resynced from
        /// <see cref="ShipLoadoutState.RuntimeBulletIndex"/> when not actively cycling.
        /// </summary>
        int _displayBankIndex = -1;

        /// <summary>[UNITY] Resolve input handler + optional bullet-name prefab.</summary>
        void Start()
        {
            _input = FindAnyObjectByType<PlayerInputHandler>();
            _bank = BulletVfxBank.LoadDefault();

#if UNITY_EDITOR
            // --- Editor convenience: wire floating text prefab without scene plumbing ---
            if (bulletNameTextPrefab == null)
            {
                bulletNameTextPrefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(
                    "Assets/Prefabs/Ships/BulletNameTextPrefab.prefab");
            }
#endif
        }

        /// <summary>Each frame: latch B if pressed, publish ShipInput, show category name on cycle.</summary>
        void Update()
        {
            // --- Per-frame refresh ---
            if (_input == null)
                return;

            bool cyclePressed = _input.CycleBulletPressed && !MoonOrbitClientState.IsOrbitMenuVisible;

            // --- Latch B until ShipInputApplySystem copies it onto the ghost ---
            // [TITAN-ORBIT] Without this, WasPressedThisFrame dies before GhostInputSystemGroup.
            if (cyclePressed)
                ShipPendingInput.LatchCycleBullet();

            if (cyclePressed)
                TryShowBulletCycleName();

            ShipPendingInput.Set(BuildInput(cyclePressed), localHostMode: false);
        }

        /// <summary>
        /// Converts PlayerInputHandler state into a ShipInput struct for ECS consumption.
        /// Aim direction is computed from mouse world position relative to local ship.
        /// </summary>
        /// <param name="cyclePressedThisFrame">True when B was pressed this Unity frame (also latched).</param>
        ShipInput BuildInput(bool cyclePressedThisFrame)
        {
            // --- Build data ---
            // Cache Camera.main — looking it up every frame was part of a ~4ms Update (Profiler 41220).
            if (_cachedCamera == null)
                _cachedCamera = UnityEngine.Camera.main;
            var cam = _cachedCamera;
            float2 aimDir = float2.zero;
            // [HYBRID] Prefer presentation pose (already synced) before ECS ship queries.
            if (cam != null)
            {
                Vector3 aimWorld = _input.GetMouseWorldPosition(cam);
                Vector3 shipPos = Vector3.zero;
                if (ShipDisplayPose.HasLocalPose)
                    shipPos = ShipDisplayPose.LocalPosition;
                else if (!EcsGameBridge.TryGetLocalShipPosition(out shipPos))
                    shipPos = Vector3.zero;
                Vector3 toAim = aimWorld - shipPos;
                toAim.y = 0f;
                if (toAim.sqrMagnitude > 0.001f)
                {
                    Vector3 dir = toAim.normalized;
                    aimDir = new float2(dir.x, dir.z);
                }
            }

            bool thrust = _input.MoveForwardPressed;

            // [NETCODE] InputEvent.Set() marks fire as pressed this tick (one-shot for ghost input).
            var fire = new InputEvent();
            if (_input.ShootPressed && !MoonOrbitClientState.IsOrbitMenuVisible)
                fire.Set();

            // [TITAN-ORBIT] B / CycleBullet — latch + Set; ShipPendingInput.Set merges latch again.
            var cycleBullet = new InputEvent();
            if (cyclePressedThisFrame || ShipPendingInput.CycleBulletLatched)
                cycleBullet.Set();

            return new ShipInput
            {
                AimPlanarDir = aimDir,
                MovePlanarDir = float2.zero,
                Thrust = thrust,
                Fire = fire,
                CycleBullet = cycleBullet,
                SpaceBrakes = _input.SpaceBrakesEnabled,
                WantDepositGems = MoonOrbitClientState.WantDepositGems,
            };
        }

        /// <summary>
        /// Spawns SimpleFloatingText with the next category name above the local ship.
        /// Advances a local display index so the name walks the full bank list (not stuck on "Bullets").
        /// </summary>
        void TryShowBulletCycleName()
        {
            // --- Resolve bank ---
            if (_bank == null)
                _bank = BulletVfxBank.LoadDefault();
            if (_bank == null || _bank.CategoryCount < 1)
                return;

            // --- Sync display index from ghost when we have never cycled this session ---
            if (_displayBankIndex < 0)
            {
                _displayBankIndex = 0;
                if (EcsGameBridge.TryGetLocalShipLoadout(out ShipLoadoutState loadout) &&
                    loadout.RuntimeBulletIndex >= 0)
                    _displayBankIndex = loadout.RuntimeBulletIndex;
            }

            // --- Advance (same math as ShipCycleBulletSystem) ---
            _displayBankIndex = (_displayBankIndex + 1) % _bank.CategoryCount;
            string name = _bank.GetCategoryName(_displayBankIndex);
            if (string.IsNullOrEmpty(name))
                return;

            if (bulletNameTextPrefab == null)
            {
                Debug.Log($"[BulletBank] {_displayBankIndex}: {name}");
                return;
            }

            if (!EcsGameBridge.TryGetLocalShipPosition(out Vector3 shipPos))
                return;

            // --- Spawn floating label (legacy NGO ShowBulletNameLocal parity) ---
            // SimpleFloatingText lives in Assembly-CSharp — call Initialize via reflection
            // so TitanOrbit.Game does not take a hard asmdef reference.
            Vector3 pos = shipPos + Vector3.up * 5f;
            GameObject go = Instantiate(bulletNameTextPrefab, pos, Quaternion.identity);
            TryInitializeFloatingText(go, name, Color.white, 2f);
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
