using TitanOrbit.Core;
using TitanOrbit.ECS;
using TitanOrbit.Input;
using TitanOrbit.NetCode;
using TitanOrbit.Shared;
using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Local-owner presentation hook. Tracers come from <see cref="BulletSpawnRpc"/>
    /// so the barrel that fires is the one the server chose. This bridge does not
    /// pick a weapon or spend a second energy pool.
    /// <para>
    /// [TITAN-ORBIT] No anticipation while <see cref="ShipOrbitState.InOrbitRing"/> — matches
    /// server <see cref="BulletSimulationSystem"/> weapons lock in planet orbit rings.
    /// </para>
    /// <para>
    /// [UNITY] LateUpdate after <see cref="EcsWorldVisualizer"/> (66000) so the hull / bank pose
    /// is published; velocity uses kinematics or hull pose-delta.
    /// </para>
    /// </summary>
    [DefaultExecutionOrder(66100)]
    public class ClientLocalBulletVfxBridge : MonoBehaviour
    {
        /// <summary>
        /// Local energy estimate after anticipation spends. Snaps down when ghost energy is lower;
        /// snaps up when ghost energy rises (regen / refill).
        /// </summary>
        float _predictedEnergy;

        /// <summary>Last replicated <see cref="ShipState.CurrentEnergy"/> — detects regen snaps.</summary>
        float _lastGhostEnergy;

        /// <summary>True after the first successful energy sync this session.</summary>
        bool _energyPrimed;

        /// <summary>
        /// Seconds predicted energy has sat below a stable (non-dropping) ghost pool.
        /// MEGA Shift volleys can spend cosmetics on a tick the server never fires;
        /// without a reconcile the predicted pool stays 0 and the guns look jammed.
        /// </summary>
        float _predictedBelowGhostStableTime;

        /// <summary>
        /// [TITAN-ORBIT] Local energy-queue cursor mirroring server
        /// <see cref="ShipWeaponState.NextMountIndex"/>. Not ghosted — cosmetic only.
        /// </summary>
        int _nextMountIndex;

        /// <summary>
        /// Mount that last anticipated a shot. The drip walker skips it so the
        /// same arsenal square cannot flash twice in a row.
        /// </summary>
        int _lastFiredMountIndex = -1;

        /// <summary>
        /// Seconds left on the arsenal square that is energizing. Counts down
        /// only while Fire is held. The HUD bar is this clock, not the whole tank.
        /// </summary>
        float _energyChargeCooldown;

        /// <summary>
        /// Full energize time of <see cref="_energyChargeCooldown"/> so the bar
        /// can draw <c>1 - remaining / duration</c> and start empty on each square.
        /// </summary>
        float _energyChargeDuration;

        /// <summary>
        /// Last fire bank we planned against. B-key changes reset predicted energy
        /// and mount cooldowns so a costly / slow bank cannot jam every owned gun.
        /// </summary>
        int _lastFireBankIndex = int.MinValue;

        /// <summary>Cached reference to scene input — resolved in Start.</summary>
        PlayerInputHandler _input;

        /// <summary>
        /// Live overlay instance. The arsenal HUD reads predicted energy / queue
        /// from here so the fill bar matches local tracers, not lagged ghost energy.
        /// </summary>
        static ClientLocalBulletVfxBridge _instance;

        /// <summary>[UNITY] Auto-install when session manager exists in scene.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void EnsureInstalled()
        {
            if (FindAnyObjectByType<ClientLocalBulletVfxBridge>() != null)
                return;

            var session = FindAnyObjectByType<TitanOrbitSessionManager>();
            if (session != null)
                session.gameObject.AddComponent<ClientLocalBulletVfxBridge>();
        }

        void Start()
        {
            _input = FindAnyObjectByType<PlayerInputHandler>();
        }

        void OnEnable()
        {
            _instance = this;
        }

        void OnDisable()
        {
            if (_instance == this)
                _instance = null;
            _energyPrimed = false;
            _predictedEnergy = 0f;
            _lastGhostEnergy = 0f;
            _predictedBelowGhostStableTime = 0f;
            _nextMountIndex = 0;
            _lastFiredMountIndex = -1;
            _energyChargeCooldown = 0f;
            _energyChargeDuration = 0f;
            _lastFireBankIndex = int.MinValue;
        }

        /// <summary>
        /// Predicted energy-queue the arsenal HUD should paint. False when this
        /// bridge has not synced a local ship yet — HUD then uses ghost energy.
        /// </summary>
        /// <param name="nextMountIndex">Barrel whose square is energizing.</param>
        /// <param name="predictedEnergy">Local pool after anticipation spends.</param>
        /// <param name="chargeRemaining">Seconds left before that square may fire.</param>
        /// <param name="chargeDuration">Full energize time for the fill bar (remaining / duration).</param>
        /// <returns>True when the values are live for this frame.</returns>
        public static bool TryGetLocalEnergyQueue(
            out int nextMountIndex,
            out float predictedEnergy,
            out float chargeRemaining,
            out float chargeDuration,
            out int lastFiredMountIndex)
        {
            nextMountIndex = 0;
            predictedEnergy = 0f;
            chargeRemaining = 0f;
            chargeDuration = 0f;
            lastFiredMountIndex = -1;
            if (_instance == null || !_instance._energyPrimed)
                return false;

            nextMountIndex = _instance._nextMountIndex;
            predictedEnergy = _instance._predictedEnergy;
            chargeRemaining = _instance._energyChargeCooldown;
            chargeDuration = _instance._energyChargeDuration;
            lastFiredMountIndex = _instance._lastFiredMountIndex;
            return true;
        }

        /// <summary>
        /// Enqueues anticipation spawns for the local ship (host + dedicated client).
        /// Prefers ECS <see cref="ShipInput.Fire"/>; falls back to <see cref="PlayerInputHandler.ShootPressed"/>.
        /// </summary>
        void LateUpdate()
        {
            // --- Per-frame refresh ---
            // [UNITY] _input is optional — ECS ShipInput.Fire can arm anticipation without it.
            if (!TitanOrbitDedicatedServerAutoBoot.ShouldRunClientPresentation())
                return;

            var world = EcsGameBridge.ClientWorld;
            if (world == null || !world.IsCreated ||
                !TitanOrbitSessionManager.IsClientGameplayReady(world))
                return;

            // Skip Instantiates / TeamChoice window — driver also gates; avoid ship gathers.
            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
                return;

            if (MoonOrbitClientState.IsOrbitMenuVisible)
                return;
            if (ShipCommsClientState.IsOpen)
                return;

            // --- Turret possession: Fire drives the pad, not ship mounts ---
            // [TITAN-ORBIT] Ship anticipation would fly hull-forward and steal PD SpawnRpc adopt.
            if (PlanetaryDefenseTurretClientState.IsControlling)
                return;

            // Server BulletSpawnRpc is the tracer. Do not pick a barrel here.
        }


        /// <summary>
        /// Keeps <see cref="_predictedEnergy"/> aligned with replicated energy without allowing
        /// unlimited anticipation while the ghost value is still high after server spends.
        /// </summary>
        /// <param name="ghostEnergy">Current replicated <see cref="ShipState.CurrentEnergy"/>.</param>
        /// <param name="dt">Unity frame dt — used to time the stuck-below-ghost reconcile.</param>
        void SyncPredictedEnergy(float ghostEnergy, float dt)
        {
            if (!_energyPrimed)
            {
                _predictedEnergy = ghostEnergy;
                _lastGhostEnergy = ghostEnergy;
                _predictedBelowGhostStableTime = 0f;
                _energyPrimed = true;
                return;
            }

            // Server spent (or we overshot) — never stay above the ghost.
            if (ghostEnergy < _predictedEnergy - 0.01f)
            {
                _predictedEnergy = ghostEnergy;
                _predictedBelowGhostStableTime = 0f;
            }
            else if (ghostEnergy > _lastGhostEnergy + 0.01f)
            {
                // Regen / refill — ghost rose since last sample; adopt the new pool.
                _predictedEnergy = ghostEnergy;
                _predictedBelowGhostStableTime = 0f;
            }
            else if (ghostEnergy > _predictedEnergy + 0.01f)
            {
                // Predicted spent, ghost is stable and higher. Wait for the snapshot to
                // drop (real server spend). If it never does — MEGA Shift cosmetics on a
                // tick the server skipped — restore so Fire is not locked out.
                _predictedBelowGhostStableTime += math.max(0f, dt);
                if (_predictedBelowGhostStableTime >= 0.4f)
                {
                    _predictedEnergy = ghostEnergy;
                    _predictedBelowGhostStableTime = 0f;
                }
            }
            else
            {
                _predictedBelowGhostStableTime = 0f;
            }

            _lastGhostEnergy = ghostEnergy;
        }

        /// <summary>
        /// Reads local ship combat components and whether fire is held this frame.
        /// Does not require <see cref="ShipKinematics"/> — velocity falls back in the muzzle helper.
        /// </summary>
        bool TryGetLocalShipCombatState(
            EntityManager em,
            out Entity shipEntity,
            out ShipWeaponConfig weaponCfg,
            out ShipState shipState,
            out int ownerNetworkId,
            out int bankIndex,
            out bool fireHeld)
        {
            shipEntity = Entity.Null;
            weaponCfg = default;
            shipState = default;
            ownerNetworkId = 0;
            bankIndex = 0;
            fireHeld = false;

            if (!BulletMuzzlePresentation.TryGetLocalShipEntity(em, out shipEntity) ||
                !em.Exists(shipEntity))
                return false;

            if (!em.HasComponent<ShipWeaponConfig>(shipEntity) ||
                !em.HasComponent<ShipState>(shipEntity))
                return false;

            weaponCfg = em.GetComponentData<ShipWeaponConfig>(shipEntity);
            shipState = em.GetComponentData<ShipState>(shipEntity);

            if (em.HasComponent<GhostOwner>(shipEntity))
                ownerNetworkId = em.GetComponentData<GhostOwner>(shipEntity).NetworkId;
            if (ownerNetworkId <= 0)
                ownerNetworkId = EcsGameBridge.GetLocalNetworkId();

            if (em.HasComponent<ShipLoadoutState>(shipEntity))
                bankIndex = BulletBankFireResolve.ResolveFireBankIndex(
                    em.GetComponentData<ShipLoadoutState>(shipEntity));

            // --- Fire gate: ECS Fire InputEvent when present, else raw input ---
            if (em.HasComponent<ShipInput>(shipEntity) && em.GetComponentData<ShipInput>(shipEntity).Fire.IsSet)
                fireHeld = true;
            else if (_input != null && _input.ShootPressed)
                fireHeld = true;

            return true;
        }
    }
}
