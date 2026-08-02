using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.Entities;
using TitanOrbit.Generation;
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
    /// Server-authoritative planetary defense fire. Active turrets aim at the nearest enemy ship
    /// or people transport within engage range (~orbit outer + 10%) and append
    /// <see cref="BulletElement"/> shots with <see cref="BulletDamageFilter.ShipsAndTransports"/>.
    /// <para>
    /// [TITAN-ORBIT] No turret ghosts — muzzle pose is derived from planet transform + slot index
    /// (same formula as client visuals / hit spheres). OwnerNetworkId is 0; OwnerTeam is planet
    /// ownership so friendly-fire rules still work.
    /// </para>
    /// World: ServerSimulation. Runs after <see cref="BulletSimulationSystem"/> so ship/drone
    /// volleys resolve first; turret bullets advance on the next tick.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(BulletSimulationSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    public partial class PlanetaryDefenseCombatSystem : SystemBase
    {
        /// <summary>Per-slot next-fire time keyed by (planet entity index &lt;&lt; 16) | slot.</summary>
        readonly Dictionary<int, float> _nextFireTime = new Dictionary<int, float>(64);

        PlanetShipFamilyConfig _familyConfig;
        PlanetaryDefenseConfig _defaultConfig;
        BulletVfxBank _vfxBank;
        readonly Dictionary<int, int> _bankIndexByFamily = new Dictionary<int, int>(16);
        bool _warmed;

        EntityQuery _planetQuery;
        EntityQuery _enemyShipQuery;
        EntityQuery _transportQuery;

        /// <summary>Cache queries used every tick.</summary>
        protected override void OnCreate()
        {
            RequireForUpdate<ActiveBulletsTag>();
            RequireForUpdate<MapStateSingleton>();
            _planetQuery = GetEntityQuery(
                ComponentType.ReadOnly<PlanetTag>(),
                ComponentType.ReadOnly<PlanetState>(),
                ComponentType.ReadOnly<LocalTransform>(),
                ComponentType.ReadOnly<PlanetaryDefenseSlotElement>());
            _enemyShipQuery = GetEntityQuery(
                ComponentType.ReadOnly<ShipTag>(),
                ComponentType.ReadOnly<ShipState>(),
                ComponentType.ReadOnly<LocalTransform>());
            _transportQuery = GetEntityQuery(
                ComponentType.ReadOnly<PeopleTransportTag>(),
                ComponentType.ReadOnly<PeopleTransportState>(),
                ComponentType.ReadOnly<LocalTransform>());
        }

        /// <summary>Fire every ready active turret at its nearest hostile target.</summary>
        protected override void OnUpdate()
        {
            if (!SystemAPI.TryGetSingletonEntity<ActiveBulletsTag>(out var bulletEntity))
                return;
            if (!EntityManager.HasBuffer<BulletElement>(bulletEntity) ||
                !EntityManager.HasBuffer<BulletSpawnEventElement>(bulletEntity))
                return;

            if (!SystemAPI.TryGetSingleton<MapStateSingleton>(out var map) ||
                !ToroidalMapEcs.IsValidMapSize(map.MapWidth, map.MapHeight))
                return;

            EnsureWarmed();

            float mapW = map.MapWidth;
            float mapH = map.MapHeight;
            float now = (float)SystemAPI.Time.ElapsedTime;
            float dt = SystemAPI.Time.DeltaTime;

            var bullets = EntityManager.GetBuffer<BulletElement>(bulletEntity);
            var spawnEvents = EntityManager.GetBuffer<BulletSpawnEventElement>(bulletEntity);
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            using var planets = _planetQuery.ToEntityArray(Allocator.Temp);
            using var enemyShips = _enemyShipQuery.ToEntityArray(Allocator.Temp);
            using var transports = _transportQuery.ToEntityArray(Allocator.Temp);

            for (int p = 0; p < planets.Length; p++)
            {
                Entity planetEntity = planets[p];
                var planet = EntityManager.GetComponentData<PlanetState>(planetEntity);
                if (planet.Ownership == TeamId.None)
                    continue;

                var buffer = EntityManager.GetBuffer<PlanetaryDefenseSlotElement>(planetEntity);
                if (buffer.Length == 0)
                    continue;

                var config = PlanetaryDefenseConfig.ResolveForFamily(
                    _familyConfig, planet.ShipFamilyConfigIndex);
                var xf = EntityManager.GetComponentData<LocalTransform>(planetEntity);
                float3 planetPos = xf.Position;
                float planetSize = math.max(0.25f, xf.Scale);
                int slotCount = buffer.Length;
                float engageRange = PlanetaryDefenseMath.GetEngageRangeFromPlanetCenter(
                    planetSize, planet.PlanetLevel, config.rangeBeyondOrbitOuter);
                float engageRangeSq = engageRange * engageRange;
                byte ownerTeam = (byte)planet.Ownership;
                int bankIndex = ResolveBankIndex(config);

                // Optional HP regen (off by default in config).
                bool regen = config.regenerateHealth && config.healthRegenPerSecond > 0f;

                for (int i = 0; i < slotCount; i++)
                {
                    var slot = buffer[i];
                    if (slot.TurretLevel == 0 || slot.Health <= 0f)
                        continue;

                    if (regen)
                    {
                        slot.Health = math.min(
                            slot.MaxHealth,
                            slot.Health + config.healthRegenPerSecond * dt);
                        buffer[i] = slot;
                    }

                    var stats = config.GetLevelStats(slot.TurretLevel);
                    float fireRate = math.max(0.05f, stats.fireRate);
                    int cooldownKey = (planetEntity.Index << 16) ^ (i & 0xFFFF);
                    if (_nextFireTime.TryGetValue(cooldownKey, out float next) && now < next)
                        continue;

                    float3 muzzle = PlanetaryDefenseMath.GetSlotWorldPosition(
                        planetPos, planetSize, planet.PlanetLevel, i, slotCount);
                    muzzle.y = PlanetaryDefenseMath.FixedY;

                    // [TITAN-ORBIT] Engage range is from planet center (orbit outer × 1.10), not muzzle.
                    if (!TryFindNearestHostile(
                            planetPos, muzzle, (TeamId)ownerTeam, engageRangeSq, mapW, mapH,
                            enemyShips, transports, out float3 targetPos))
                        continue;

                    float3 aim = ToroidalMapEcs.ShortestOffsetXZ(muzzle, targetPos, mapW, mapH);
                    aim.y = 0f;
                    if (math.lengthsq(aim) < 0.0001f)
                        continue;
                    aim = math.normalize(aim);

                    float3 bulletVel = aim * math.max(1f, stats.bulletSpeed);
                    uint sequence = BulletVfxBridge.NextSequence();
                    var spawn = new BulletElement
                    {
                        Position = muzzle,
                        Velocity = bulletVel,
                        MaxDistance = math.max(1f, config.bulletMaxDistance),
                        Lifetime = math.max(0.1f, config.bulletLifetimeSeconds),
                        Damage = math.max(0.05f, stats.damage),
                        OwnerNetworkId = 0,
                        OwnerTeam = ownerTeam,
                        Sequence = sequence,
                        BankIndex = math.max(0, bankIndex),
                        ScaleMultiplier = math.max(0.1f, config.bulletVisualScale),
                        DamageFilter = BulletDamageFilter.ShipsAndTransports,
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

                    BulletNetNotify.SendSpawn(
                        ref ecb, spawn, mountIndex: DroneSwarmLogic.NoWeaponMountReproject);
                    bullets.Add(spawn);
                    _nextFireTime[cooldownKey] = now + (1f / fireRate);
                }
            }

            ecb.Playback(EntityManager);
            ecb.Dispose();
        }

        /// <summary>
        /// Nearest living enemy ship or people transport within engage range of the planet center
        /// (toroidal). Among in-range hostiles, prefers the closest to the firing muzzle.
        /// </summary>
        bool TryFindNearestHostile(
            float3 planetPos,
            float3 muzzle,
            TeamId ownerTeam,
            float engageRangeSq,
            float mapW,
            float mapH,
            NativeArray<Entity> enemyShips,
            NativeArray<Entity> transports,
            out float3 targetPos)
        {
            targetPos = default;
            float bestMuzzleDistSq = float.MaxValue;
            bool found = false;

            // --- Enemy ships ---
            for (int i = 0; i < enemyShips.Length; i++)
            {
                Entity e = enemyShips[i];
                var ship = EntityManager.GetComponentData<ShipState>(e);
                if (ship.IsDead || ship.AwaitingTeamSelection || ship.Team == TeamId.None)
                    continue;
                if (ship.Team == ownerTeam)
                    continue;

                float3 pos = EntityManager.GetComponentData<LocalTransform>(e).Position;
                pos.y = PlanetaryDefenseMath.FixedY;
                float3 fromPlanet = ToroidalMapEcs.ShortestOffsetXZ(planetPos, pos, mapW, mapH);
                float planetDistSq = math.lengthsq(new float3(fromPlanet.x, 0f, fromPlanet.z));
                if (planetDistSq > engageRangeSq)
                    continue;

                float3 fromMuzzle = ToroidalMapEcs.ShortestOffsetXZ(muzzle, pos, mapW, mapH);
                float muzzleDistSq = math.lengthsq(new float3(fromMuzzle.x, 0f, fromMuzzle.z));
                if (muzzleDistSq >= bestMuzzleDistSq)
                    continue;

                bestMuzzleDistSq = muzzleDistSq;
                targetPos = pos;
                found = true;
            }

            // --- Enemy people transports ---
            for (int i = 0; i < transports.Length; i++)
            {
                Entity e = transports[i];
                var t = EntityManager.GetComponentData<PeopleTransportState>(e);
                if (t.Amount <= 0f || t.Health <= 0f)
                    continue;
                var team = (TeamId)t.Team;
                if (team == TeamId.None || team == ownerTeam)
                    continue;

                float3 pos = EntityManager.GetComponentData<LocalTransform>(e).Position;
                pos.y = PlanetaryDefenseMath.FixedY;
                float3 fromPlanet = ToroidalMapEcs.ShortestOffsetXZ(planetPos, pos, mapW, mapH);
                float planetDistSq = math.lengthsq(new float3(fromPlanet.x, 0f, fromPlanet.z));
                if (planetDistSq > engageRangeSq)
                    continue;

                float3 fromMuzzle = ToroidalMapEcs.ShortestOffsetXZ(muzzle, pos, mapW, mapH);
                float muzzleDistSq = math.lengthsq(new float3(fromMuzzle.x, 0f, fromMuzzle.z));
                if (muzzleDistSq >= bestMuzzleDistSq)
                    continue;

                bestMuzzleDistSq = muzzleDistSq;
                targetPos = pos;
                found = true;
            }

            return found;
        }

        void EnsureWarmed()
        {
            if (_warmed)
                return;
            _familyConfig = Resources.Load<PlanetShipFamilyConfig>("PlanetShipFamilyConfig");
            _defaultConfig = PlanetaryDefenseConfig.LoadDefault();
            _vfxBank = BulletVfxBank.LoadDefault();
            _warmed = true;
        }

        int ResolveBankIndex(PlanetaryDefenseConfig config)
        {
            if (config == null)
                config = _defaultConfig;
            // Key by asset name hash — avoid obsolete GetInstanceID in Unity 6.
            int key = config != null ? config.name.GetHashCode() : 0;
            if (_bankIndexByFamily.TryGetValue(key, out int cached))
                return cached;

            int idx = 0;
            if (_vfxBank != null &&
                !string.IsNullOrEmpty(config.bulletBankCategoryName) &&
                _vfxBank.TryGetCategoryIndexByName(config.bulletBankCategoryName, out int found))
            {
                idx = found;
            }
            else if (_vfxBank != null &&
                     _vfxBank.TryGetCategoryIndexByName(DroneSwarmLogic.FighterBankCategoryName, out int fighter))
            {
                idx = fighter;
            }

            _bankIndexByFamily[key] = idx;
            return idx;
        }
    }
}
