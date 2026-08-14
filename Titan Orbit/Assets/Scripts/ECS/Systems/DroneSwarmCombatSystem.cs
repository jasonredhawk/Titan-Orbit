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
    /// <see cref="StoreItemData.GetCombatDroneDamage"/> (not ship <c>BulletDamage</c>).
    /// Mining bolts use <see cref="BulletDamageFilter.AsteroidsOnly"/>; fighters use
    /// <see cref="BulletDamageFilter.ShipsOnly"/> — Starblast-style pass-through.
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

        /// <summary>Warmed once — avoid Resources.Load every tick.</summary>
        BulletVfxBank _vfxBank;
        int _fighterBankIndex;
        int _miningBankIndex;
        bool _banksResolved;

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

            EnsureBanksResolved();

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
            bool ownEnemies = false;
            bool ownAsteroids = false;
            if (anyFighter)
            {
                enemyShips = _enemyShipQuery.ToEntityArray(Allocator.Temp);
                ownEnemies = true;
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
                float3 shipVel = kinematics.Velocity;
                shipVel.y = 0f;
                int ownerNetId = ghostOwner.NetworkId;
                byte ownerTeam = (byte)shipState.Team;
                // [TITAN-ORBIT] Combat drones use their purchase ItemLevel damage — NOT ship BulletDamage.
                // Range / lifetime still borrow the hull weapon config so bolts travel a sensible distance.
                float maxDist = math.max(10f, weaponCfg.BulletMaxDistance);
                float lifetime = math.max(0.1f, weaponCfg.BulletLifetime);
                int rearCount = math.max(1, _rearSlots.Count);

                // One nearest-target lookup per ship (reuse for every ready drone of that type).
                // Declare outs before short-circuit guards — CS0170 if `out` sits inside `&&`.
                float3 enemyTarget = default;
                float3 rockTarget = default;
                bool hasEnemy = false;
                bool hasRock = false;
                if (anyFighter && enemyShips.IsCreated)
                {
                    hasEnemy = TryFindNearestEnemyShip(
                        enemyShips, shipPos, (TeamId)ownerTeam, ownerNetId,
                        DroneSwarmLogic.FighterEngageRange, mapW, mapH, out enemyTarget);
                }
                if (anyMining && asteroids.IsCreated)
                {
                    hasRock = TryFindNearestAsteroid(
                        asteroids, shipPos, DroneSwarmLogic.MiningEngageRange, mapW, mapH, out rockTarget);
                }

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
                    var pose = DroneSwarmPositioning.EvaluateSlotPose(type, slot, in poseCtx);
                    Vector3 firePos = pose.WorldPosition;
                    firePos.y = DroneSwarmLogic.FixedY;

                    bool isFighter = type == StoreItemType.FighterDrone;
                    float fireRate = isFighter ? DroneSwarmLogic.FighterFireRate : DroneSwarmLogic.MiningFireRate;
                    float bulletSpeed = isFighter ? DroneSwarmLogic.FighterBulletSpeed : DroneSwarmLogic.MiningBulletSpeed;
                    int bankIndex = isFighter ? _fighterBankIndex : _miningBankIndex;

                    // --- Per-drone leveled damage (purchase ItemLevel, not live ship guns) ---
                    // ItemLevel 0 = legacy drone (pre-leveling) — treat as reference max for damage.
                    int droneLevel = buf[slot].ItemLevel > 0
                        ? buf[slot].ItemLevel
                        : StoreItemData.DroneReferenceMaxLevel;
                    float damage = math.max(0.05f, StoreItemData.GetCombatDroneDamage(droneLevel));
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
                        if (!hasEnemy)
                            continue;
                        Vector3 off = DroneSwarmLogic.ToroidalOffsetXZ(
                            firePos, new Vector3(enemyTarget.x, 0f, enemyTarget.z), mapW, mapH);
                        aimDir = new float3(off.x, 0f, off.z);
                    }
                    else
                    {
                        if (!hasRock)
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
                        bankIndex, ref damage, ref bulletSpeed, ref shotMax, ref shotLife, ref fireRateForMods);
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
                        ScaleMultiplier = DroneSwarmLogic.DroneBulletVisualScale,
                        DamageFilter = damageFilter,
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
            }
        }

        /// <summary>Warm BulletVfxBank category indices once.</summary>
        void EnsureBanksResolved()
        {
            if (_banksResolved)
                return;
            _vfxBank = BulletVfxBank.LoadDefault();
            _fighterBankIndex = ResolveBankIndex(_vfxBank, DroneSwarmLogic.FighterBankCategoryName);
            _miningBankIndex = ResolveBankIndex(_vfxBank, DroneSwarmLogic.MiningBankCategoryName);
            _banksResolved = true;
        }

        /// <summary>Resolves BulletVfxBank category by name; falls back to 0.</summary>
        static int ResolveBankIndex(BulletVfxBank bank, string categoryName)
        {
            if (bank != null && bank.TryGetCategoryIndexByName(categoryName, out int idx))
                return idx;
            return 0;
        }

        /// <summary>Nearest living enemy ship within engage range (toroidal from owner).</summary>
        bool TryFindNearestEnemyShip(
            NativeArray<Entity> entities,
            Vector3 ownerPos,
            TeamId ownerTeam,
            int ownerNetworkId,
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
                if (sq >= bestSq)
                    continue;
                bestSq = sq;
                targetPos = pos;
                found = true;
            }

            return found;
        }

        /// <summary>Nearest living asteroid within engage range (toroidal from owner).</summary>
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
