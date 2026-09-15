using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.Entities;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server-authoritative fighter + mining drone fire. Positions come from
    /// <see cref="DroneSwarmPositioning.EvaluateSlotPose"/> — the same pure function the client
    /// visual driver uses — so muzzle origins match buzzing meshes without networking drone
    /// transforms (bandwidth-safe: only ship pose + equipment + bullets hit the wire).
    /// <para>
    /// [TITAN-ORBIT] Lives in the <c>TitanOrbit.ECS</c> assembly (not <c>TitanOrbit.Game</c>) because
    /// Game already references ECS — putting combat here avoids a circular assembly reference.
    /// Shared math lives in <see cref="DroneSwarmLogic"/> / <see cref="DroneSwarmPositioning"/>
    /// (<c>TitanOrbit.Entities</c>).
    /// </para>
    /// <para>
    /// [TITAN-ORBIT] Combat drones fire at purchase-level damage from
    /// <see cref="StoreItemData.GetCombatDroneDamage"/> (not ship <c>BulletDamage</c>) —
    /// about 1/6 of the ship fire-power curve. Each drone uses the bullet bank of the
    /// planet family it was bought from (stamped on
    /// <see cref="EquippedEquipmentElement.ComponentId"/>). Bank multipliers stay as
    /// authored (1.25 fire power stays 1.25). Strength stats (pull/push force, radii,
    /// burn DPS) use <see cref="DroneSwarmLogic.DroneFirePowerScale"/> (1/6); durations
    /// and tick intervals stay at the bullet type's authored times.
    /// Mining bolts use <see cref="BulletDamageFilter.AsteroidsOnly"/>; fighters use
    /// <see cref="BulletDamageFilter.ShipsOnly"/> — Starblast-style pass-through.
    /// Fighter aim is the nearest living enemy ship <b>or</b> enemy planetary-defense turret
    /// (derived pad pose — no turret ghosts). <c>ShipsOnly</c> already damages those pads.
    /// </para>
    /// <para>
    /// World: ServerSimulation. Runs after <see cref="BulletSimulationSystem"/> so ship volleys
    /// resolve first; drone bullets advance on the next tick.
    /// </para>
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(BulletSimulationSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    public partial class DroneSwarmCombatSystem : SystemBase
    {
        /// <summary>Per-slot next-fire time keyed by (ship entity index << 16) | slot.</summary>
        readonly Dictionary<int, float> _nextFireTime = new Dictionary<int, float>(64);

        readonly List<int> _rearSlots = new List<int>(8);
        readonly List<(int slot, StoreItemType type)> _droneSlots = new List<(int, StoreItemType)>(8);

        EntityQuery _shipQuery;
        EntityQuery _enemyShipQuery;
        EntityQuery _asteroidQuery;
        EntityQuery _planetQuery;

        /// <summary>Active defense pads this tick — rebuilt once, reused for every fighter ship.</summary>
        readonly List<PlanetaryDefenseHitTarget> _defenseTargets = new List<PlanetaryDefenseHitTarget>(64);

        /// <summary>Warmed once — avoid Resources.Load every tick.</summary>
        PlanetShipFamilyConfig _familyConfig;
        bool _familyConfigWarmed;

        /// <summary>Cache queries used every tick.</summary>
        protected override void OnCreate()
        {
            RequireForUpdate<ActiveBulletsTag>();
            _shipQuery = GetEntityQuery(
                ComponentType.ReadOnly<ShipTag>(),
                ComponentType.ReadOnly<ShipState>(),
                ComponentType.ReadOnly<LocalTransform>(),
                ComponentType.ReadOnly<GhostOwner>(),
                ComponentType.ReadOnly<ShipKinematics>(),
                ComponentType.ReadOnly<ShipWeaponConfig>(),
                ComponentType.ReadOnly<EquippedEquipmentElement>());
            _enemyShipQuery = GetEntityQuery(
                ComponentType.ReadOnly<ShipTag>(),
                ComponentType.ReadOnly<ShipState>(),
                ComponentType.ReadOnly<LocalTransform>(),
                ComponentType.ReadOnly<GhostOwner>());
            _asteroidQuery = GetEntityQuery(
                ComponentType.ReadOnly<AsteroidTag>(),
                ComponentType.ReadOnly<AsteroidState>(),
                ComponentType.ReadOnly<LocalTransform>());
            _planetQuery = GetEntityQuery(
                ComponentType.ReadOnly<PlanetTag>(),
                ComponentType.ReadOnly<PlanetState>(),
                ComponentType.ReadOnly<LocalTransform>(),
                ComponentType.ReadOnly<PlanetaryDefenseSlotElement>());
        }

        /// <summary>Fire fighter/mining drones for every living ship that has equipped drones.</summary>
        protected override void OnUpdate()
        {
            if (!SystemAPI.TryGetSingletonEntity<ActiveBulletsTag>(out var bulletEntity))
                return;
            if (!EntityManager.HasBuffer<BulletElement>(bulletEntity) ||
                !EntityManager.HasBuffer<BulletSpawnEventElement>(bulletEntity))
                return;

            // --- Shared ServerTick clock (matches client visual buzz) ---
            // [NETCODE] Prefer NetworkTime.ServerTick seconds over World.Time so late-join clients
            // and the server share one buzz timeline (same idea as PlanetGemMoonOrbitClock).
            int hz = PlanetGemMoonOrbitClock.FallbackSimulationHz;
            if (SystemAPI.TryGetSingleton<ClientServerTickRate>(out var tickRate))
                hz = math.max(1, tickRate.SimulationTickRate);
            double timeSeconds = SystemAPI.Time.ElapsedTime;
            if (SystemAPI.TryGetSingleton<NetworkTime>(out var networkTime) && networkTime.ServerTick.IsValid)
                timeSeconds = PlanetGemMoonOrbitClock.ToElapsedSeconds(networkTime, hz, includeTickFraction: false);
            DroneSwarmSimTime.Publish(timeSeconds);

            var bullets = EntityManager.GetBuffer<BulletElement>(bulletEntity);
            var spawnEvents = EntityManager.GetBuffer<BulletSpawnEventElement>(bulletEntity);
            float now = (float)timeSeconds;

            float mapW = 1000f;
            float mapH = 1000f;
            if (SystemAPI.TryGetSingleton<MapStateSingleton>(out var mapState) &&
                mapState.MapWidth >= 100f && mapState.MapHeight >= 100f)
            {
                mapW = mapState.MapWidth;
                mapH = mapState.MapHeight;
            }

            EnsureFamilyConfigWarmed();

            // --- Gather once per tick (not per drone) ---
            using var ships = _shipQuery.ToEntityArray(Allocator.Temp);

            // Cheap pre-pass: skip asteroid/enemy arrays when nobody has combat drones.
            bool anyFighter = false;
            bool anyMining = false;
            for (int s = 0; s < ships.Length && !(anyFighter && anyMining); s++)
            {
                Entity entity = ships[s];
                if (!EntityManager.HasBuffer<EquippedEquipmentElement>(entity))
                    continue;
                var buf = EntityManager.GetBuffer<EquippedEquipmentElement>(entity);
                for (int i = 0; i < buf.Length; i++)
                {
                    var type = (StoreItemType)buf[i].ItemType;
                    if (buf[i].RemainingCharges <= 0)
                        continue;
                    if (type == StoreItemType.FighterDrone) anyFighter = true;
                    else if (type == StoreItemType.MiningDrone) anyMining = true;
                }
            }

            if (!anyFighter && !anyMining)
                return;

            NativeArray<Entity> enemyShips = default;
            NativeArray<Entity> asteroids = default;
            NativeArray<Entity> planets = default;
            bool ownEnemies = false;
            bool ownAsteroids = false;
            bool ownPlanets = false;
            _defenseTargets.Clear();
            if (anyFighter)
            {
                enemyShips = _enemyShipQuery.ToEntityArray(Allocator.Temp);
                ownEnemies = true;
                // Map size from MapStateSingleton above — RebuildTargets only needs poses.
                planets = _planetQuery.ToEntityArray(Allocator.Temp);
                ownPlanets = true;
                PlanetaryDefenseHitScan.RebuildTargets(
                    EntityManager, planets, mapW, mapH, _familyConfig, null, _defenseTargets);
            }
            if (anyMining)
            {
                asteroids = _asteroidQuery.ToEntityArray(Allocator.Temp);
                ownAsteroids = true;
            }

            var ecb = new EntityCommandBuffer(Allocator.Temp);

            try
            {
            for (int s = 0; s < ships.Length; s++)
            {
                Entity entity = ships[s];
                var shipState = EntityManager.GetComponentData<ShipState>(entity);
                if (shipState.IsDead || shipState.AwaitingTeamSelection)
                    continue;
                // [TITAN-ORBIT] Hull stowed in a defense pad — drones are out of play with the ship.
                if (PlanetaryDefenseTurretControlLogic.IsControllingTurret(EntityManager, entity))
                    continue;
                if (!EntityManager.HasBuffer<EquippedEquipmentElement>(entity))
                    continue;

                var buf = EntityManager.GetBuffer<EquippedEquipmentElement>(entity);
                _droneSlots.Clear();
                _rearSlots.Clear();
                for (int i = 0; i < buf.Length; i++)
                {
                    var e = buf[i];
                    var type = (StoreItemType)e.ItemType;
                    if (!StoreItemData.IsDrone(type) || e.RemainingCharges <= 0)
                        continue;
                    _droneSlots.Add((i, type));
                    if (type == StoreItemType.FighterDrone || type == StoreItemType.MiningDrone)
                        _rearSlots.Add(i);
                }

                if (_droneSlots.Count == 0 || _rearSlots.Count == 0)
                    continue;

                var transform = EntityManager.GetComponentData<LocalTransform>(entity);
                var ghostOwner = EntityManager.GetComponentData<GhostOwner>(entity);
                var kinematics = EntityManager.GetComponentData<ShipKinematics>(entity);
                var weaponCfg = EntityManager.GetComponentData<ShipWeaponConfig>(entity);

                Vector3 shipPos = (Vector3)transform.Position;
                Quaternion shipRot = (Quaternion)transform.Rotation;
                DroneSwarmPositioning.GetShipBasis(shipPos, shipRot, out shipPos, out Vector3 forward, out Vector3 right);
                float hullRadius = BodyCollisionMath.GetShipHullRadiusWorld(transform.Scale);
                float orbitRadius = DroneSwarmPositioning.GetDroneOrbitRadiusFromHull(hullRadius);
                float coverEx = 0f, coverEz = 0f, coverCx = 0f, coverCz = 0f;
                if (EntityManager.HasComponent<ShipHullColliderState>(entity))
                {
                    var hull = EntityManager.GetComponentData<ShipHullColliderState>(entity);
                    coverEx = hull.AppliedCoveringExtentX;
                    coverEz = hull.AppliedCoveringExtentZ;
                    coverCx = hull.AppliedCoveringCenterX;
                    coverCz = hull.AppliedCoveringCenterZ;
                }
                float3 shipVel = kinematics.Velocity;
                shipVel.y = 0f;
                int ownerNetId = ghostOwner.NetworkId;
                byte ownerTeam = (byte)shipState.Team;
                // [TITAN-ORBIT] Combat drones use purchase ItemLevel damage — NOT ship BulletDamage.
                // Range / lifetime still borrow the hull weapon config so bolts travel a sensible distance.
                float maxDist = math.max(10f, weaponCfg.BulletMaxDistance);
                float lifetime = math.max(0.1f, weaponCfg.BulletLifetime);
                int rearCount = math.max(1, _rearSlots.Count);

                // Hulls stay close-escort. Pads use bolt travel (or DefensePadEngageRange)
                // so drones return fire while the ship is in a turret fight.
                float turretRange = math.max(DroneSwarmLogic.DefensePadEngageRange, maxDist);

                for (int d = 0; d < _droneSlots.Count; d++)
                {
                    var (slot, type) = _droneSlots[d];
                    if (type != StoreItemType.FighterDrone && type != StoreItemType.MiningDrone)
                        continue;

                    int rearOrd = 0;
                    for (int r = 0; r < _rearSlots.Count; r++)
                    {
                        if (_rearSlots[r] == slot)
                        {
                            rearOrd = r;
                            break;
                        }
                    }

                    // [TITAN-ORBIT] Same EvaluateSlotPose as client — no orbit catch-up on server.
                    var poseCtx = new DroneSwarmPositioning.SlotEvaluationContext
                    {
                        ShipPos = shipPos,
                        Forward = forward,
                        Right = right,
                        OrbitRadius = orbitRadius,
                        TimeSeconds = timeSeconds,
                        ShipNetworkId = ownerNetId,
                        MapW = mapW,
                        MapH = mapH,
                        RearOrdinal = rearOrd,
                        RearCount = rearCount,
                    };
                    DroneSwarmPositioning.ApplyCoveringHullShape(
                        ref poseCtx, transform.Scale, coverEx, coverEz, coverCx, coverCz);
                    var pose = DroneSwarmPositioning.EvaluateSlotPose(type, slot, in poseCtx);
                    Vector3 firePos = pose.WorldPosition;
                    firePos.y = DroneSwarmLogic.FixedY;

                    bool isFighter = type == StoreItemType.FighterDrone;
                    float fireRate = isFighter ? DroneSwarmLogic.FighterFireRate : DroneSwarmLogic.MiningFireRate;
                    float bulletSpeed = isFighter ? DroneSwarmLogic.FighterBulletSpeed : DroneSwarmLogic.MiningBulletSpeed;
                    int bankIndex = ResolveDroneBankIndex(buf[slot], shipState.ShipFamilyConfigIndex);

                    // --- Per-drone leveled damage (purchase ItemLevel, not live ship guns) ---
                    // ItemLevel 0 = legacy drone (pre-leveling) — treat as reference max for damage.
                    int droneLevel = buf[slot].ItemLevel > 0
                        ? buf[slot].ItemLevel
                        : StoreItemData.DroneReferenceMaxLevel;
                    // One-sixth ship fire-power curve (L1 ≈ 0.67, L6 = 1.5). Bank multipliers
                    // (e.g. 1.25 fire power) then apply unchanged — never the hull's live guns.
                    float damage = math.max(0.05f, StoreItemData.GetCombatDroneDamage(droneLevel)
                        * CardEffectQuery.GetMul(EntityManager, entity, CardEffectKind.DroneDamageMul));
                    // Authored primary abilities only; StrengthScale (1/6) shrinks force/radius/DPS.
                    // Durations and tick intervals stay at the bullet type's authored times.
                    const int firePowerExtras = 0;
                    // [TITAN-ORBIT] Starblast-style target filters — mining ignores ships; fighters ignore rocks.
                    var damageFilter = isFighter
                        ? BulletDamageFilter.ShipsOnly
                        : BulletDamageFilter.AsteroidsOnly;

                    int cooldownKey = (entity.Index << 16) ^ (slot & 0xFFFF);
                    if (_nextFireTime.TryGetValue(cooldownKey, out float next) && now < next)
                        continue;

                    float3 aimDir;
                    if (isFighter)
                    {
                        if (!enemyShips.IsCreated ||
                            !TryFindNearestEnemyCombatTarget(
                                enemyShips, _defenseTargets, firePos, (TeamId)ownerTeam, ownerNetId,
                                DroneSwarmLogic.FighterEngageRange, turretRange, mapW, mapH,
                                out float3 enemyTarget))
                            continue;
                        Vector3 off = DroneSwarmLogic.ToroidalOffsetXZ(
                            firePos, new Vector3(enemyTarget.x, 0f, enemyTarget.z), mapW, mapH);
                        aimDir = new float3(off.x, 0f, off.z);
                    }
                    else
                    {
                        if (!asteroids.IsCreated ||
                            !TryFindNearestAsteroid(
                                asteroids, firePos, DroneSwarmLogic.MiningEngageRange, mapW, mapH,
                                out float3 rockTarget))
                            continue;
                        Vector3 off = DroneSwarmLogic.ToroidalOffsetXZ(
                            firePos, new Vector3(rockTarget.x, 0f, rockTarget.z), mapW, mapH);
                        aimDir = new float3(off.x, 0f, off.z);
                    }

                    aimDir.y = 0f;
                    if (math.lengthsq(aimDir) < 0.0001f)
                        continue;
                    aimDir = math.normalize(aimDir);

                    float fireRateForMods = fireRate;
                    float shotMax = maxDist;
                    float shotLife = lifetime;
                    BulletBankCombatLogic.ApplyFireModifiers(
                        bankIndex, ref damage, ref bulletSpeed, ref shotMax, ref shotLife, ref fireRateForMods,
                        firePowerExtras);
                    float3 bulletVel = aimDir * math.max(1f, bulletSpeed) + shipVel;
                    uint sequence = BulletVfxBridge.NextSequence();
                    var spawn = new BulletElement
                    {
                        Position = new float3(firePos.x, DroneSwarmLogic.FixedY, firePos.z),
                        Velocity = bulletVel,
                        MaxDistance = shotMax,
                        Lifetime = shotLife,
                        Damage = damage,
                        OwnerNetworkId = ownerNetId,
                        OwnerTeam = ownerTeam,
                        Sequence = sequence,
                        BankIndex = math.max(0, bankIndex),
                        // Mini tracer (0.58) is visual only. Gameplay strength is 1/6.
                        ScaleMultiplier = DroneSwarmLogic.DroneBulletVisualScale,
                        DamageFilter = damageFilter,
                        FirePowerExtraLevels = firePowerExtras,
                        StrengthScale = DroneSwarmLogic.DroneFirePowerScale,
                    };

                    spawnEvents.Add(new BulletSpawnEventElement
                    {
                        SpawnPosition = spawn.Position,
                        Velocity = spawn.Velocity,
                        Lifetime = spawn.Lifetime,
                        MaxDistance = spawn.MaxDistance,
                        Damage = spawn.Damage,
                        OwnerTeam = spawn.OwnerTeam,
                        Sequence = spawn.Sequence,
                        BankIndex = spawn.BankIndex,
                        ScaleMultiplier = spawn.ScaleMultiplier,
                    });

                    BulletNetNotify.SendSpawn(ref ecb, spawn, mountIndex: DroneSwarmLogic.NoWeaponMountReproject);
                    bullets.Add(spawn);
                    _nextFireTime[cooldownKey] = now + (1f / math.max(0.05f, fireRateForMods));
                }
            }

            ecb.Playback(EntityManager);
            }
            finally
            {
                ecb.Dispose();
                if (ownEnemies && enemyShips.IsCreated)
                    enemyShips.Dispose();
                if (ownAsteroids && asteroids.IsCreated)
                    asteroids.Dispose();
                if (ownPlanets && planets.IsCreated)
                    planets.Dispose();
            }
        }

        void EnsureFamilyConfigWarmed()
        {
            if (_familyConfigWarmed)
                return;
            _familyConfig = Resources.Load<PlanetShipFamilyConfig>("PlanetShipFamilyConfig");
            _familyConfigWarmed = true;
        }

        /// <summary>Purchase-planet family bank, else the hull family's default damage bank.</summary>
        int ResolveDroneBankIndex(in EquippedEquipmentElement equipment, byte hullFamilyIndex)
        {
            ShipFamilyDefinition hullFamily = null;
            if (_familyConfig != null)
            {
                var entry = _familyConfig.GetFamilyByConfigIndex(hullFamilyIndex);
                hullFamily = entry != null ? entry.shipFamilyDefinition : null;
            }

            return BulletBankProfileUtility.ResolveBankIndexForDrone(
                equipment.ComponentId.ToString(), hullFamily);
        }

        /// <summary>
        /// Nearest living enemy ship (hull range) or enemy planetary-defense turret (pad range)
        /// from this drone's fire pose. When both exist, the closer one wins.
        /// Map size is the caller's <c>MapStateSingleton</c>.
        /// </summary>
        bool TryFindNearestEnemyCombatTarget(
            NativeArray<Entity> entities,
            List<PlanetaryDefenseHitTarget> defenseTargets,
            Vector3 ownerPos,
            TeamId ownerTeam,
            int ownerNetworkId,
            float shipEngageRange,
            float turretEngageRange,
            float mapW,
            float mapH,
            out float3 targetPos)
        {
            targetPos = default;
            float bestSq = float.MaxValue;
            bool found = false;
            float3 owner = new float3(ownerPos.x, 0f, ownerPos.z);
            float shipMaxSq = shipEngageRange * shipEngageRange;
            float turretMaxSq = turretEngageRange * turretEngageRange;

            for (int i = 0; i < entities.Length; i++)
            {
                Entity e = entities[i];
                var shipState = EntityManager.GetComponentData<ShipState>(e);
                if (shipState.IsDead)
                    continue;
                if (shipState.Team == ownerTeam)
                    continue;
                var ghost = EntityManager.GetComponentData<GhostOwner>(e);
                if (ownerNetworkId > 0 && ghost.NetworkId == ownerNetworkId)
                    continue;

                float3 pos = EntityManager.GetComponentData<LocalTransform>(e).Position;
                pos.y = 0f;
                float dist = DroneSwarmLogic.ToroidalDistanceXZ(owner.x, owner.z, pos.x, pos.z, mapW, mapH);
                float sq = dist * dist;
                if (sq >= shipMaxSq || sq >= bestSq)
                    continue;
                bestSq = sq;
                targetPos = pos;
                found = true;
            }

            if (defenseTargets != null)
            {
                byte ownerTeamByte = (byte)ownerTeam;
                for (int i = 0; i < defenseTargets.Count; i++)
                {
                    var pad = defenseTargets[i];
                    if (pad.Team == (byte)TeamId.None || pad.Team == ownerTeamByte)
                        continue;

                    float3 pos = pad.Position;
                    pos.y = 0f;
                    float dist = DroneSwarmLogic.ToroidalDistanceXZ(owner.x, owner.z, pos.x, pos.z, mapW, mapH);
                    float sq = dist * dist;
                    if (sq >= turretMaxSq || sq >= bestSq)
                        continue;
                    bestSq = sq;
                    targetPos = pos;
                    found = true;
                }
            }

            return found;
        }

        /// <summary>Nearest living asteroid within engage range (toroidal from this drone).</summary>
        bool TryFindNearestAsteroid(
            NativeArray<Entity> entities,
            Vector3 ownerPos,
            float engageRange,
            float mapW,
            float mapH,
            out float3 targetPos)
        {
            targetPos = default;
            float bestSq = engageRange * engageRange;
            bool found = false;
            float3 owner = new float3(ownerPos.x, 0f, ownerPos.z);

            for (int i = 0; i < entities.Length; i++)
            {
                Entity e = entities[i];
                var asteroid = EntityManager.GetComponentData<AsteroidState>(e);
                if (asteroid.IsDestroyed || asteroid.Health <= 0f)
                    continue;

                float3 pos = EntityManager.GetComponentData<LocalTransform>(e).Position;
                pos.y = 0f;
                float dist = DroneSwarmLogic.ToroidalDistanceXZ(owner.x, owner.z, pos.x, pos.z, mapW, mapH);
                float sq = dist * dist;
                if (sq >= bestSq)
                    continue;
                bestSq = sq;
                targetPos = pos;
                found = true;
            }

            return found;
        }
    }
}
