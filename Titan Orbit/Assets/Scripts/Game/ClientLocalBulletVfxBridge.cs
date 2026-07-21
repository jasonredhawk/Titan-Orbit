using TitanOrbit.Core;
using TitanOrbit.ECS;
using TitanOrbit.Input;
using TitanOrbit.NetCode;
using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Local-owner bullet anticipation: enqueues cosmetic tracers into <see cref="BulletVfxBridge"/>
    /// from live weapon component transforms (<see cref="BulletMuzzlePresentation"/>) so muzzle
    /// flash matches the drawn barrel (including BankPivot). Server remains authoritative for
    /// damage (<see cref="BulletSimulationSystem"/>).
    /// <para>
    /// [TITAN-ORBIT] Mirrors <see cref="ShipWeaponFireLogic"/>: full-volley when predicted energy
    /// covers every mount; otherwise only <c>_nextMountIndex</c> may spend energy until it fires,
    /// then the next barrel in sequence (0→1→2→…→0). When <see cref="BulletSpawnRpc"/> arrives,
    /// <see cref="BulletVfxDriver"/> binds Sequence without snapping pose back to the lagged
    /// server muzzle.
    /// </para>
    /// <para>
    /// [TITAN-ORBIT] Anticipation deducts a <b>local predicted energy</b> pool (mirrors server
    /// spend). Using only replicated <c>ShipState.CurrentEnergy</c> over-fired cosmetics while
    /// ghost energy lagged — optimistic “HP Left: 0” on asteroids the server had not killed.
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
        /// Reused shot plan — mirrors server <c>BulletSimulationSystem</c> scratch (no per-frame alloc).
        /// </summary>
        static readonly ShipWeaponFireLogic.MountShot[] s_ShotScratch =
            new ShipWeaponFireLogic.MountShot[ShipWeaponFireLogic.MaxShotsPerTick];

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
        /// [TITAN-ORBIT] Local energy-queue cursor mirroring server
        /// <see cref="ShipWeaponState.NextMountIndex"/>. Not ghosted — cosmetic only.
        /// </summary>
        int _nextMountIndex;

        /// <summary>Cached reference to scene input — resolved in Start.</summary>
        PlayerInputHandler _input;

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

        void OnDisable()
        {
            _energyPrimed = false;
            _predictedEnergy = 0f;
            _lastGhostEnergy = 0f;
            _nextMountIndex = 0;
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

            // Skip Instantiates window — driver also gates; avoid queueing anticipation during join.
            if (ClientJoinSettleCache.Settling || ClientJoinSettleCache.GhostSpawnBacklog)
                return;

            if (MoonOrbitClientState.IsOrbitMenuVisible)
                return;

            if (!TryGetLocalShipCombatState(world.EntityManager, out Entity shipEntity, out ShipWeaponConfig weaponCfg,
                    out ShipState shipState, out int ownerNetworkId, out int bankIndex, out bool fireHeld))
                return;

            if (shipState.IsDead || shipState.AwaitingTeamSelection)
                return;

            // --- Need ECS mounts for per-barrel cooldown + damage (live GO count alone is not enough) ---
            if (!world.EntityManager.HasBuffer<ShipWeaponMountElement>(shipEntity))
                return;

            var mounts = world.EntityManager.GetBuffer<ShipWeaponMountElement>(shipEntity);
            if (mounts.Length <= 0)
                return;

            float dt = Time.deltaTime;
            // Tick cooldowns even when Fire is released so barrels stay in sync with server cadence.
            ShipWeaponFireLogic.TickMountCooldowns(mounts, dt);

            if (!fireHeld)
                return;

            // --- Sync predicted energy with ghost (before planning fire) ---
            SyncPredictedEnergy(shipState.CurrentEnergy);

            // --- Volley vs energy-queue round-robin (same planner as server) ---
            if (!ShipWeaponFireLogic.TryPlanFire(
                    _predictedEnergy,
                    mounts,
                    _nextMountIndex,
                    weaponCfg.BulletDamage,
                    weaponCfg.FireRate,
                    s_ShotScratch,
                    out int shotCount,
                    out float energySpend,
                    out int nextMountIndexAfter))
                return;

            // --- Cap pending anticipations — do not arm cooldowns / cursor if the queue is full ---
            if (!BulletVfxBridge.CanEnqueueAnticipation(shotCount))
                return;

            float fallbackRefDamage = weaponCfg.ReferenceBulletDamage > 0f
                ? weaponCfg.ReferenceBulletDamage
                : BulletVisualScale.DefaultReferenceBulletDamage;
            float refSpeed = weaponCfg.ReferenceBulletSpeed > 0f
                ? weaponCfg.ReferenceBulletSpeed
                : BulletVisualScale.DefaultReferenceBulletSpeed;

            int enqueued = 0;
            float spent = 0f;

            // --- Enqueue planned mounts from live weapon transforms ---
            for (int shot = 0; shot < shotCount; shot++)
            {
                var planned = s_ShotScratch[shot];
                int mountIdx = planned.MountIndex;
                if (!BulletMuzzlePresentation.TryResolveMuzzle(
                        world.EntityManager, shipEntity, mountIdx,
                        out float3 fireOrigin, out float3 fireForward, out bool displaySpace,
                        out float3 shipVel))
                    continue;

                ShipWeaponMountElement mount = mounts[mountIdx];
                float refDamage = mount.ReferenceFirePower > 0.01f
                    ? mount.ReferenceFirePower
                    : fallbackRefDamage;
                float visualScale = BulletVisualScale.ComputePerShotScale(
                    weaponCfg.BulletScale,
                    planned.Damage,
                    weaponCfg.BulletSpeed,
                    refDamage,
                    refSpeed);

                float3 bulletVel = BulletMuzzlePresentation.BuildBulletWorldVelocity(
                    fireForward, weaponCfg.BulletSpeed, shipVel);

                if (!BulletVfxBridge.TryEnqueueSpawn(new BulletVfxBridge.SpawnRequest
                {
                    Sequence = 0,
                    SpawnPosition = fireOrigin,
                    Velocity = bulletVel,
                    Lifetime = math.max(0.1f, weaponCfg.BulletLifetime),
                    MaxDistance = math.max(10f, weaponCfg.BulletMaxDistance),
                    Damage = planned.Damage,
                    OwnerTeam = (byte)shipState.Team,
                    OwnerNetworkId = ownerNetworkId,
                    BankIndex = bankIndex,
                    ScaleMultiplier = visualScale,
                    MountIndex = mountIdx,
                    IsAnticipation = true,
                    IsDisplaySpace = displaySpace,
                }))
                    break;

                // Arm this barrel’s client-side cooldown so we do not spam tracers faster than server.
                mount.FireCooldown = planned.CooldownSeconds;
                mounts[mountIdx] = mount;
                spent += planned.EnergyCost;
                enqueued++;
            }

            if (enqueued > 0)
            {
                // Prefer planned energy when every shot queued; otherwise spend only what enqueued.
                float spend = enqueued == shotCount ? energySpend : spent;
                _predictedEnergy = math.max(0f, _predictedEnergy - spend);
                // Only advance the energy-queue cursor when the full plan enqueued (avoids skips).
                if (enqueued == shotCount)
                    _nextMountIndex = nextMountIndexAfter;
            }
        }

        /// <summary>
        /// Keeps <see cref="_predictedEnergy"/> aligned with replicated energy without allowing
        /// unlimited anticipation while the ghost value is still high after server spends.
        /// </summary>
        /// <param name="ghostEnergy">Current replicated <see cref="ShipState.CurrentEnergy"/>.</param>
        void SyncPredictedEnergy(float ghostEnergy)
        {
            if (!_energyPrimed)
            {
                _predictedEnergy = ghostEnergy;
                _lastGhostEnergy = ghostEnergy;
                _energyPrimed = true;
                return;
            }

            // Server spent (or we overshot) — never stay above the ghost.
            if (ghostEnergy < _predictedEnergy - 0.01f)
                _predictedEnergy = ghostEnergy;

            // Regen / refill — ghost rose since last sample; adopt the new pool.
            if (ghostEnergy > _lastGhostEnergy + 0.01f)
                _predictedEnergy = ghostEnergy;

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
                bankIndex = math.max(0, em.GetComponentData<ShipLoadoutState>(shipEntity).RuntimeBulletIndex);

            // --- Fire gate: ECS Fire InputEvent when present, else raw input ---
            if (em.HasComponent<ShipInput>(shipEntity) && em.GetComponentData<ShipInput>(shipEntity).Fire.IsSet)
                fireHeld = true;
            else if (_input != null && _input.ShootPressed)
                fireHeld = true;

            return true;
        }
    }
}
