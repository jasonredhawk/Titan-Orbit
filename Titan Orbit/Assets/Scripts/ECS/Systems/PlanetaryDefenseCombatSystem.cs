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
    /// or people transport within engage range (pad→orbit gap × Level 1→6 fire-distance multiplier
    /// from <see cref="PlanetaryDefenseConfig"/>) and append <see cref="BulletElement"/> shots with
    /// <see cref="BulletDamageFilter.ShipsAndTransports"/>.
    /// <para>
    /// [TITAN-ORBIT] No turret ghosts — muzzle pose is derived from planet transform + slot index
    /// (same formula as client visuals / hit spheres). OwnerNetworkId is 0; OwnerTeam is planet
    /// ownership so friendly-fire rules still work. Aim uses simple velocity lead so moving
    /// ships are not under-shot; <see cref="BulletVisualScale"/> grows tracers with fire power.
    /// Bullet bank comes from the turret asset category name, else the family's
    /// <see cref="ShipFamilyDefinition.bulletPrefabIndex"/>.
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

            // Per-category upgrade knob (same bank path as ship guns) — grows tracer with damage.
            float categoryUpgradeScale = 1f;
            if (_vfxBank != null)
            {
                // Bank-wide upgrade cache for BulletVisualScale (ship sim also writes this).
                BulletVisualScale.ActiveUpgradeVisualScaleMultiplier = _vfxBank.UpgradeVisualScaleMultiplier;
            }

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
                ShipFamilyDefinition familyDef = null;
                if (_familyConfig != null)
                {
                    var entry = _familyConfig.GetFamilyByConfigIndex(planet.ShipFamilyConfigIndex);
                    familyDef = entry != null ? entry.shipFamilyDefinition : null;
                }

                var xf = EntityManager.GetComponentData<LocalTransform>(planetEntity);
                float3 planetPos = xf.Position;
                float planetSize = math.max(0.25f, xf.Scale);
                int slotCount = buffer.Length;
                byte ownerTeam = (byte)planet.Ownership;
                int bankIndex = ResolveBankIndex(config, familyDef);
                if (_vfxBank != null)
                    categoryUpgradeScale = _vfxBank.GetCategoryUpgradeVisualScaleMultiplier(bankIndex);

                // Level-1 damage is the visual “no upgrade” reference for this turret recipe.
                float referenceDamage = math.max(0.1f, config.GetLevelStats(1).damage);

                // Ship-style delayed regen: heal only after healthRegenDelayAfterDamage since last hit.
                bool regen = config.regenerateHealth && config.healthRegenPerSecond > 0f;
                float regenDelay = math.max(0f, config.healthRegenDelayAfterDamage);
                double nowDouble = SystemAPI.Time.ElapsedTime;
                var regenBuf = PlanetaryDefenseLogic.EnsureRegenBuffer(
                    EntityManager, planetEntity, slotCount, wipeExisting: false);

                for (int i = 0; i < slotCount; i++)
                {
                    var slot = buffer[i];
                    if (slot.TurretLevel == 0 || slot.Health <= 0f)
                        continue;

                    if (regen && slot.Health < slot.MaxHealth)
                    {
                        double lastDamage = i < regenBuf.Length
                            ? regenBuf[i].LastDamageServerTime
                            : 0.0;
                        // [TITAN-ORBIT] Same gate as ShipVitalsRegenSystem — no heal while under fire.
                        if (nowDouble >= lastDamage + regenDelay)
                        {
                            slot.Health = math.min(
                                slot.MaxHealth,
                                slot.Health + config.healthRegenPerSecond * dt);
                            buffer[i] = slot;
                        }
                    }

                    var stats = config.GetLevelStats(slot.TurretLevel);
                    float fireRate = math.max(0.05f, stats.fireRate);
                    float bulletSpeed = math.max(1f, stats.bulletSpeed);
                    float damage = math.max(0.05f, stats.damage);
                    int cooldownKey = (planetEntity.Index << 16) ^ (i & 0xFFFF);
                    if (_nextFireTime.TryGetValue(cooldownKey, out float next) && now < next)
                        continue;

                    float3 muzzle = PlanetaryDefenseMath.GetSlotWorldPosition(
                        planetPos, planetSize, planet.PlanetLevel, i, slotCount);
                    muzzle.y = PlanetaryDefenseMath.FixedY;

                    // [TITAN-ORBIT] Per-turret-level fire distance: pad→orbit gap × multiplier (2→3).
                    float engageRange = PlanetaryDefenseMath.GetEngageRangeFromTurret(
                        planetSize, planet.PlanetLevel, stats.engageRangeMultiplier);
                    float engageRangeSq = engageRange * engageRange;

                    if (!TryFindNearestHostile(
                            muzzle, (TeamId)ownerTeam, engageRangeSq, mapW, mapH,
                            enemyShips, transports,
                            out float3 targetPos, out float3 targetVel))
                        continue;

                    // --- Velocity lead so shots land on moving ships (not behind them) ---
                    float3 toTarget = ToroidalMapEcs.ShortestOffsetXZ(muzzle, targetPos, mapW, mapH);
                    toTarget.y = 0f;
                    float dist = math.length(toTarget);
                    float leadT = dist / bulletSpeed;
                    // Cap lead so extreme speeds cannot aim wildly past the target.
                    leadT = math.min(leadT, 0.85f);
                    float3 aimPoint = targetPos + targetVel * leadT;
                    aimPoint.y = PlanetaryDefenseMath.FixedY;

                    float3 aim = ToroidalMapEcs.ShortestOffsetXZ(muzzle, aimPoint, mapW, mapH);
                    aim.y = 0f;
                    if (math.lengthsq(aim) < 0.0001f)
                        continue;
                    aim = math.normalize(aim);

                    // Same fire-power → tracer size path as ship guns.
                    float visualScale = BulletVisualScale.ComputePerShotScale(
                        config.bulletVisualScale,
                        damage,
                        bulletSpeed,
                        referenceDamage,
                        bulletSpeed,
                        categoryUpgradeScale);

                    float3 bulletVel = aim * bulletSpeed;
                    uint sequence = BulletVfxBridge.NextSequence();
                    var spawn = new BulletElement
                    {
                        Position = muzzle,
                        Velocity = bulletVel,
                        MaxDistance = math.max(1f, config.bulletMaxDistance),
                        Lifetime = math.max(0.1f, config.bulletLifetimeSeconds),
                        Damage = damage,
                        OwnerNetworkId = 0,
                        OwnerTeam = ownerTeam,
                        Sequence = sequence,
                        BankIndex = math.max(0, bankIndex),
                        ScaleMultiplier = math.max(0.1f, visualScale),
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
        /// Nearest living enemy ship or people transport within engage range of the turret muzzle
        /// (toroidal). Prefers the closest hostile to that muzzle. Also returns planar velocity
        /// for lead aiming.
        /// </summary>
        bool TryFindNearestHostile(
            float3 muzzle,
            TeamId ownerTeam,
            float engageRangeSq,
            float mapW,
            float mapH,
            NativeArray<Entity> enemyShips,
            NativeArray<Entity> transports,
            out float3 targetPos,
            out float3 targetVel)
        {
            targetPos = default;
            targetVel = float3.zero;
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

                float3 fromMuzzle = ToroidalMapEcs.ShortestOffsetXZ(muzzle, pos, mapW, mapH);
                float muzzleDistSq = math.lengthsq(new float3(fromMuzzle.x, 0f, fromMuzzle.z));
                if (muzzleDistSq > engageRangeSq || muzzleDistSq >= bestMuzzleDistSq)
                    continue;

                bestMuzzleDistSq = muzzleDistSq;
                targetPos = pos;
                targetVel = float3.zero;
                if (EntityManager.HasComponent<ShipKinematics>(e))
                {
                    float3 vel = EntityManager.GetComponentData<ShipKinematics>(e).Velocity;
                    vel.y = 0f;
                    targetVel = vel;
                }

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

                float3 fromMuzzle = ToroidalMapEcs.ShortestOffsetXZ(muzzle, pos, mapW, mapH);
                float muzzleDistSq = math.lengthsq(new float3(fromMuzzle.x, 0f, fromMuzzle.z));
                if (muzzleDistSq > engageRangeSq || muzzleDistSq >= bestMuzzleDistSq)
                    continue;

                bestMuzzleDistSq = muzzleDistSq;
                targetPos = pos;
                // Transports often lack ship kinematics — lead stays zero (still fine for slow pods).
                targetVel = float3.zero;
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

        /// <summary>
        /// Resolves BulletVfxBank category: turret asset name first, then family bullet index.
        /// </summary>
        int ResolveBankIndex(PlanetaryDefenseConfig config, ShipFamilyDefinition family)
        {
            if (config == null)
                config = _defaultConfig;
            // Key by asset name + family bullet index so family fallback caches separately.
            int familyBullet = family != null ? family.bulletPrefabIndex : 0;
            int key = (config != null ? config.name.GetHashCode() : 0) ^ (familyBullet * 397);
            if (_bankIndexByFamily.TryGetValue(key, out int cached))
                return cached;

            // --- Prefer turret asset category name ---
            int idx = -1;
            if (_vfxBank != null &&
                config != null &&
                !string.IsNullOrEmpty(config.bulletBankCategoryName) &&
                _vfxBank.TryGetCategoryIndexByName(config.bulletBankCategoryName, out int found))
            {
                idx = found;
            }

            // --- Fallback: owning family's ship bullet bank index ---
            if (idx < 0)
                idx = BulletBankProfileUtility.ResolveBankIndexForFamily(family);

            // --- Last resort: fighter drone bank name ---
            if (idx < 0 &&
                _vfxBank != null &&
                _vfxBank.TryGetCategoryIndexByName(DroneSwarmLogic.FighterBankCategoryName, out int fighter))
            {
                idx = fighter;
            }

            idx = math.max(0, idx);
            _bankIndexByFamily[key] = idx;
            return idx;
        }
    }
}
