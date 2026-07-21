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
    /// [TITAN-ORBIT] Multi-cannon hulls mirror <see cref="ShipWeaponFireLogic"/>: full-volley
    /// anticipation when energy covers every mount; otherwise round-robin drip — one tracer
    /// from <c>_nextMountIndex</c>, then advance +1 (0→1→2→…→0). When <see cref="BulletSpawnRpc"/>
    /// arrives, <see cref="BulletVfxDriver"/> binds Sequence without snapping pose back to the
    /// lagged server muzzle.
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
        /// <summary>Client-side fire-rate gate mirroring server FireRate cooldown.</summary>
        float _fireCooldown;

        /// <summary>
        /// [TITAN-ORBIT] Local round-robin cursor mirroring server
        /// <see cref="ShipWeaponState.NextMountIndex"/> for drip anticipation.
        /// Not ghosted — cosmetic only; adopt uses MountIndex from the spawn RPC.
        /// </summary>
        int _nextMountIndex;

        /// <summary>
        /// Local energy estimate after anticipation spends. Snaps down when ghost energy is lower;
        /// snaps up when ghost energy rises (regen / refill).
        /// </summary>
        float _predictedEnergy;

        /// <summary>Last replicated <see cref="ShipState.CurrentEnergy"/> — detects regen snaps.</summary>
        float _lastGhostEnergy;

        /// <summary>True after the first successful energy sync this session.</summary>
        bool _energyPrimed;

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

            float dt = Time.deltaTime;
            if (_fireCooldown > 0f)
                _fireCooldown = Mathf.Max(0f, _fireCooldown - dt);

            if (_fireCooldown > 0f)
                return;

            if (!TryGetLocalShipCombatState(world.EntityManager, out Entity shipEntity, out ShipWeaponConfig weaponCfg,
                    out ShipState shipState, out int ownerNetworkId, out int bankIndex, out bool fireHeld))
                return;

            if (!fireHeld)
                return;

            if (shipState.IsDead || shipState.AwaitingTeamSelection)
                return;

            // --- Sync predicted energy with ghost (before planning fire) ---
            SyncPredictedEnergy(shipState.CurrentEnergy);

            // --- Mount count: prefer ECS buffer (server truth for round-robin), else live GOs ---
            int mountCount = 0;
            if (world.EntityManager.HasBuffer<ShipWeaponMountElement>(shipEntity))
                mountCount = world.EntityManager.GetBuffer<ShipWeaponMountElement>(shipEntity).Length;
            if (mountCount <= 0)
                mountCount = BulletMuzzlePresentation.GetLiveWeaponMountCount(
                    world.EntityManager, shipEntity);

            if (mountCount <= 0)
                return;

            // --- Volley vs drip (same planner as server) ---
            // Gate on predicted energy (not raw ghost) so we cannot over-fire while CurrentEnergy lags.
            float energyCostPerBarrel = weaponCfg.EnergyCostPerShot > 0f
                ? weaponCfg.EnergyCostPerShot
                : weaponCfg.BulletDamage;
            if (!ShipWeaponFireLogic.TryPlanFire(
                    _predictedEnergy,
                    energyCostPerBarrel,
                    weaponCfg.BulletDamage,
                    weaponCfg.FireRate,
                    mountCount,
                    _nextMountIndex,
                    out var firePlan))
                return;

            // --- Cap pending anticipations for this plan (volley = N, drip = FireCount) ---
            // [TITAN-ORBIT] Do not advance cooldown / cursor here — retry next frame when adopt frees slots.
            if (!BulletVfxBridge.CanEnqueueAnticipation(firePlan.FireCount))
                return;

            float visualScale = BulletVisualScale.ComputePerShotScale(
                weaponCfg.BulletScale,
                weaponCfg.BulletDamage,
                weaponCfg.BulletSpeed,
                weaponCfg.ReferenceBulletDamage > 0f
                    ? weaponCfg.ReferenceBulletDamage
                    : BulletVisualScale.DefaultReferenceBulletDamage,
                weaponCfg.ReferenceBulletSpeed > 0f
                    ? weaponCfg.ReferenceBulletSpeed
                    : BulletVisualScale.DefaultReferenceBulletSpeed);

            int enqueued = 0;

            // --- Enqueue planned mounts from live weapon transforms ---
            for (int shot = 0; shot < firePlan.FireCount; shot++)
            {
                int mountIdx = ShipWeaponFireLogic.ResolveMountIndex(in firePlan, shot, mountCount);
                if (!BulletMuzzlePresentation.TryResolveMuzzle(
                        world.EntityManager, shipEntity, mountIdx,
                        out float3 fireOrigin, out float3 fireForward, out bool displaySpace,
                        out float3 shipVel))
                    continue;

                float3 bulletVel = BulletMuzzlePresentation.BuildBulletWorldVelocity(
                    fireForward, weaponCfg.BulletSpeed, shipVel);

                if (!BulletVfxBridge.TryEnqueueSpawn(new BulletVfxBridge.SpawnRequest
                {
                    Sequence = 0,
                    SpawnPosition = fireOrigin,
                    Velocity = bulletVel,
                    Lifetime = math.max(0.1f, weaponCfg.BulletLifetime),
                    MaxDistance = math.max(10f, weaponCfg.BulletMaxDistance),
                    Damage = firePlan.DamagePerBullet,
                    OwnerTeam = (byte)shipState.Team,
                    OwnerNetworkId = ownerNetworkId,
                    BankIndex = bankIndex,
                    ScaleMultiplier = visualScale,
                    MountIndex = mountIdx,
                    IsAnticipation = true,
                    IsDisplaySpace = displaySpace,
                }))
                    break;

                enqueued++;
            }

            // Only start cooldown / advance drip cursor when at least one muzzle queued.
            if (enqueued > 0)
            {
                // Spend predicted energy for the full plan (matches server EnergySpend).
                float spend = firePlan.EnergySpend;
                if (enqueued < firePlan.FireCount && firePlan.FireCount > 0)
                    spend = firePlan.EnergySpend * (enqueued / (float)firePlan.FireCount);

                _predictedEnergy = math.max(0f, _predictedEnergy - spend);
                _fireCooldown = firePlan.CooldownSeconds;
                _nextMountIndex = firePlan.NextMountIndexAfter;
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
