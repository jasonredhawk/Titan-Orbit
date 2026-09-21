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
    /// Level 1 = 0.6, Level 6 = 2.4 (4×). Each drone uses the bullet bank of the
    /// planet family it was bought from (stamped on
    /// <see cref="EquippedEquipmentElement.ComponentId"/>). Bank multipliers stay as
    /// authored (1.25 fire power stays 1.25). Unique-ability Extra Levels use
    /// <c>droneLevel − 1</c> so a Level-6 drone's burn / heal / multipliers are
    /// stronger than a Level-1's. Strength stats (pull/push force, radii, burn DPS)
    /// still use <see cref="DroneSwarmLogic.DroneFirePowerScale"/> (1/6); durations
    /// and tick intervals stay at the bullet type's authored times.
    /// Mining bolts use <see cref="BulletDamageFilter.AsteroidsOnly"/>; fighters use
    /// <see cref="BulletDamageFilter.ShipsOnly"/> — Starblast-style pass-through.
    /// Each fighter / miner picks the closest in-range target from its own idle pose and
    /// slides that idle slot toward it (speed-limited per-drone offset).
    /// Fighters pick the nearest living enemy ship <b>or</b> enemy planetary-defense turret
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

        readonly List<int> _shieldSlots = new List<int>(8);

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
            bool anyShield = false;
            for (int s = 0; s < ships.Length; s++)
            {
                Entity entity = ships[s];
                if (!EntityManager.HasBuffer<EquippedEquipmentElement>(entity))
                    continue;

                // [TITAN-ORBIT] Walk every ship (do not early-out on type flags). 0-HP drone
                // rows still fill a LOADOUT slot; combat is the tick that runs even when
                // nobody is shooting, so leftovers get stripped without waiting for bullets.
                DroneSwarmHitScan.CompactDestroyedDroneSlots(EntityManager, entity);
                if (anyFighter && anyMining && anyShield)
                    continue;

                var buf = EntityManager.GetBuffer<EquippedEquipmentElement>(entity);
                for (int i = 0; i < buf.Length; i++)
                {
                    var type = (StoreItemType)buf[i].ItemType;
                    if (buf[i].RemainingCharges <= 0)
                        continue;
                    if (type == StoreItemType.FighterDrone) anyFighter = true;
                    else if (type == StoreItemType.MiningDrone) anyMining = true;
                    else if (type == StoreItemType.ShieldDrone) anyShield = true;
                }
            }

            if (!anyFighter && !anyMining && !anyShield)
                return;

            NativeArray<Entity> enemyShips = default;
            NativeArray<Entity> asteroids = default;
            NativeArray<Entity> planets = default;
            bool ownEnemies = false;
            bool ownAsteroids = false;
            bool ownPlanets = false;
            _defenseTargets.Clear();
            if (anyFighter || anyShield)
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
                // [TITAN-ORBIT] Hull stowed in a defense pad or fully moon-docked — drones are
                // out of play with the ship (client hides the swarm in the same states).
                if (PlanetaryDefenseTurretControlLogic.IsControllingTurret(EntityManager, entity))
                    continue;
                if (ShipMoonDockState.IsFullyLandedOnMoon(EntityManager, entity))
                    continue;
                if (!EntityManager.HasBuffer<EquippedEquipmentElement>(entity))
                    continue;

                var buf = EntityManager.GetBuffer<EquippedEquipmentElement>(entity);
                _droneSlots.Clear();
                _rearSlots.Clear();
                _shieldSlots.Clear();
                for (int i = 0; i < buf.Length; i++)
                {
                    var e = buf[i];
                    var type = (StoreItemType)e.ItemType;
                    if (!StoreItemData.IsDrone(type) || e.RemainingCharges <= 0)
                        continue;
                    _droneSlots.Add((i, type));
                    if (type == StoreItemType.FighterDrone || type == StoreItemType.MiningDrone)
                        _rearSlots.Add(i);
                    else if (type == StoreItemType.ShieldDrone)
                        _shieldSlots.Add(i);
                }

                if (_droneSlots.Count == 0)
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
                float shieldOrbitRadius = DroneSwarmPositioning.GetShieldOrbitRadiusFromHull(hullRadius);
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
                int shieldCount = math.max(1, _shieldSlots.Count);
                float anchorDt = 1f / hz;

                for (int d = 0; d < _droneSlots.Count; d++)
                {
                    var (slot, type) = _droneSlots[d];
                    bool isFighter = type == StoreItemType.FighterDrone;
                    bool isMining = type == StoreItemType.MiningDrone;
                    bool isShield = type == StoreItemType.ShieldDrone;
                    if (!isFighter && !isMining && !isShield)
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

                    int shieldOrd = 0;
                    for (int sh = 0; sh < _shieldSlots.Count; sh++)
                    {
                        if (_shieldSlots[sh] == slot)
                        {
                            shieldOrd = sh;
                            break;
                        }
                    }

                    // Idle rear slot — offset is measured from here so a ship yaw can retarget each drone.
                    var idleCtx = new DroneSwarmPositioning.SlotEvaluationContext
                    {
                        ShipPos = shipPos,
                        Forward = forward,
                        Right = right,
                        OrbitRadius = isShield ? shieldOrbitRadius : orbitRadius,
                        TimeSeconds = timeSeconds,
                        ShipNetworkId = ownerNetId,
                        MapW = mapW,
                        MapH = mapH,
                        RearOrdinal = rearOrd,
                        RearCount = rearCount,
                        ShieldOrdinal = shieldOrd,
                        ShieldCount = shieldCount,
                        HasShieldTarget = false,
                    };
                    DroneSwarmPositioning.ApplyCoveringHullShape(
                        ref idleCtx, transform.Scale, coverEx, coverEz, coverCx, coverCz);
                    Vector3 shipHome = DroneSwarmPositioning.ResolveFormationHome(in idleCtx);
                    Vector3 idlePos = DroneSwarmPositioning.EvaluateSlotPose(type, slot, in idleCtx).WorldPosition;
                    idlePos.y = DroneSwarmLogic.FixedY;

                    bool hasSwarm;
                    Vector3 aimAt = default;
                    float leash;
                    float targetStandoff = 0f;
                    if (isFighter || isShield)
                    {
                        float3 fighterTarget = default;
                        bool fighterIsPad = false;
                        float hullRange = isShield
                            ? DroneSwarmLogic.ShieldEngageRange
                            : DroneSwarmLogic.FighterEngageRange;
                        float padRange = isShield
                            ? DroneSwarmLogic.DefensePadEngageRange
                            : DroneSwarmLogic.FighterEngageRange;
                        Vector3 findFrom = isShield ? shipHome : idlePos;
                        hasSwarm = enemyShips.IsCreated
                            && TryFindNearestEnemyCombatTarget(
                                enemyShips, _defenseTargets, findFrom, (TeamId)ownerTeam, ownerNetId,
                                hullRange, padRange,
                                mapW, mapH, out fighterTarget, out fighterIsPad, out targetStandoff);
                        leash = isShield
                            ? (fighterIsPad
                                ? DroneSwarmLogic.DefensePadEngageRange
                                : DroneSwarmLogic.ShieldEngageRange)
                            : DroneSwarmLogic.ResolveFighterLeash(fighterIsPad);
                        if (hasSwarm)
                            aimAt = new Vector3(fighterTarget.x, 0f, fighterTarget.z);
                    }
                    else
                    {
                        float3 miningTarget = default;
                        hasSwarm = asteroids.IsCreated
                            && TryFindNearestAsteroid(
                                asteroids, idlePos, DroneSwarmLogic.MiningEngageRange, mapW, mapH,
                                out miningTarget, out targetStandoff);
                        leash = DroneSwarmLogic.MiningEngageRange;
                        if (hasSwarm)
                            aimAt = new Vector3(miningTarget.x, 0f, miningTarget.z);
                    }

                    long offsetKey = DroneSwarmLogic.FormationAnchorKey(ownerNetId, slot);
                    var anchorState = DroneSwarmFormationRuntime.Get(offsetKey);
                    Vector3 desired = isShield
                        ? DroneSwarmLogic.ComputeShieldDesiredFormationOffset(
                            shipHome, aimAt, hasSwarm, targetStandoff, mapW, mapH)
                        : DroneSwarmLogic.ComputeDesiredFormationOffset(
                            idlePos, aimAt, hasSwarm, leash, targetStandoff, mapW, mapH);
                    anchorState = DroneSwarmLogic.StepFormationOffset(
                        anchorState, desired, idlePos, anchorDt, mapW, mapH, hasSwarm);
                    DroneSwarmFormationRuntime.Set(offsetKey, anchorState);

                    if (isShield)
                        continue;

                    Vector3 firePos = idlePos + anchorState.Offset;
                    firePos.y = DroneSwarmLogic.FixedY;

                    if (!hasSwarm)
                        continue;

                    float fireRate = isFighter ? DroneSwarmLogic.FighterFireRate : DroneSwarmLogic.MiningFireRate;
                    float bulletSpeed = isFighter ? DroneSwarmLogic.FighterBulletSpeed : DroneSwarmLogic.MiningBulletSpeed;
                    int bankIndex = ResolveDroneBankIndex(buf[slot], shipState.ShipFamilyConfigIndex);

                    // --- Per-drone leveled damage (purchase ItemLevel, not live ship guns) ---
                    // ItemLevel 0 = legacy drone (pre-leveling) — treat as reference max for damage.
                    int droneLevel = buf[slot].ItemLevel > 0
                        ? buf[slot].ItemLevel
                        : StoreItemData.DroneReferenceMaxLevel;
                    // L1 = 0.6, L6 = 2.4 (4×). Bank multipliers (e.g. 1.25 fire power)
                    // then apply unchanged — never the hull's live guns.
                    float damage = math.max(0.05f, StoreItemData.GetCombatDroneDamage(droneLevel)
                        * CardEffectQuery.GetMul(EntityManager, entity, CardEffectKind.DroneDamageMul));
                    // Extra Levels = droneLevel − 1 so unique bank abilities (burn DPS,
                    // heal, damage muls) climb with the same purchase rung as HP / FP.
                    // StrengthScale (1/6) still shrinks force/radius/DPS vs a ship shot.
                    int firePowerExtras = StoreItemData.GetDroneFirePowerExtraLevels(droneLevel);
                    // [TITAN-ORBIT] Starblast-style target filters — mining ignores ships; fighters ignore rocks.
                    var damageFilter = isFighter
                        ? BulletDamageFilter.ShipsOnly
                        : BulletDamageFilter.AsteroidsOnly;

                    int cooldownKey = (entity.Index << 16) ^ (slot & 0xFFFF);
                    if (_nextFireTime.TryGetValue(cooldownKey, out float next) && now < next)
                        continue;

                    Vector3 off = DroneSwarmLogic.ToroidalOffsetXZ(firePos, aimAt, mapW, mapH);
                    float3 aimDir = new float3(off.x, 0f, off.z);

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
                        // Mini tracer (0.58) is visual only. StrengthScale (1/6) shrinks
                        // unique effects; Damage already used the leveled FP curve.
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
        /// from the owner ship. When both exist, the closer one wins.
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
            out float3 targetPos,
            out bool isDefensePad,
            out float targetStandoff)
        {
            targetPos = default;
            isDefensePad = false;
            targetStandoff = 0f;
            float bestSurface = float.MaxValue;
            bool found = false;
            Vector3 from = new Vector3(ownerPos.x, 0f, ownerPos.z);

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

                var xf = EntityManager.GetComponentData<LocalTransform>(e);
                Vector3 pos = (Vector3)xf.Position;
                pos.y = 0f;
                Quaternion rot = (Quaternion)xf.Rotation;
                DroneSwarmPositioning.GetShipBasis(pos, rot, out pos, out Vector3 fwd, out Vector3 right);
                float coverEx = 0f, coverEz = 0f, coverCx = 0f, coverCz = 0f;
                if (EntityManager.HasComponent<ShipHullColliderState>(e))
                {
                    var hull = EntityManager.GetComponentData<ShipHullColliderState>(e);
                    coverEx = hull.AppliedCoveringExtentX;
                    coverEz = hull.AppliedCoveringExtentZ;
                    coverCx = hull.AppliedCoveringCenterX;
                    coverCz = hull.AppliedCoveringCenterZ;
                }

                pos = DroneSwarmPositioning.ResolveCoveringOrigin(
                    pos, fwd, right, coverCx, coverCz, xf.Scale);
                float standoff = DroneSwarmPositioning.ResolveApproachStandoff(
                    from, pos, fwd, right, coverEx, coverEz, xf.Scale,
                    BodyCollisionMath.GetShipHullRadiusWorld(xf.Scale), mapW, mapH);
                float surface = DroneSwarmLogic.SurfaceDistanceXZ(
                    from, pos, DroneSwarmPositioning.HullRadiusFromApproachStandoff(standoff), mapW, mapH);
                if (surface >= shipEngageRange || surface >= bestSurface)
                    continue;
                bestSurface = surface;
                targetPos = new float3(pos.x, 0f, pos.z);
                targetStandoff = standoff;
                isDefensePad = false;
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

                    Vector3 pos = new Vector3(pad.Position.x, 0f, pad.Position.z);
                    float hull = pad.HitRadius > 0.05f
                        ? pad.HitRadius
                        : DroneSwarmLogic.DefensePadColliderRadius;
                    float standoff = DroneSwarmPositioning.ResolveSphereStandoff(hull);
                    float surface = DroneSwarmLogic.SurfaceDistanceXZ(from, pos, hull, mapW, mapH);
                    if (surface >= turretEngageRange || surface >= bestSurface)
                        continue;
                    bestSurface = surface;
                    targetPos = new float3(pos.x, 0f, pos.z);
                    targetStandoff = standoff;
                    isDefensePad = true;
                    found = true;
                }
            }

            return found;
        }

        /// <summary>Nearest living asteroid within engage range (toroidal from this drone's idle pose).</summary>
        bool TryFindNearestAsteroid(
            NativeArray<Entity> entities,
            Vector3 ownerPos,
            float engageRange,
            float mapW,
            float mapH,
            out float3 targetPos,
            out float targetStandoff)
        {
            targetPos = default;
            targetStandoff = 0f;
            float bestSurface = float.MaxValue;
            bool found = false;
            Vector3 from = new Vector3(ownerPos.x, 0f, ownerPos.z);

            for (int i = 0; i < entities.Length; i++)
            {
                Entity e = entities[i];
                var asteroid = EntityManager.GetComponentData<AsteroidState>(e);
                if (!asteroid.IsAliveForCombat)
                    continue;

                var xf = EntityManager.GetComponentData<LocalTransform>(e);
                // Kill-frame squash (0.01) — corpse is already hidden on clients.
                if (xf.Scale <= AsteroidDeathPhysics.CulledTransformScale * 2f)
                    continue;

                Vector3 pos = new Vector3(xf.Position.x, 0f, xf.Position.z);
                float hull = BodyCollisionMath.GetAsteroidBodyRadiusWorld(xf.Scale);
                float standoff = DroneSwarmPositioning.ResolveSphereStandoff(hull);
                float surface = DroneSwarmLogic.SurfaceDistanceXZ(from, pos, hull, mapW, mapH);
                if (surface >= engageRange || surface >= bestSurface)
                    continue;
                bestSurface = surface;
                targetPos = new float3(pos.x, 0f, pos.z);
                targetStandoff = standoff;
                found = true;
            }

            return found;
        }
    }
}
