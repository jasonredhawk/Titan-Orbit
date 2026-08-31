using System.Collections.Generic;
using TitanOrbit;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.Entities;
using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Physics;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server-authoritative bullet simulation and ship firing. Runs after
    /// <see cref="PredictedFixedStepSimulationSystemGroup"/> (which contains
    /// <see cref="ShipPhysicsDriveSystem"/>) so muzzle positions use current transforms.
    /// <para>
    /// Multi-cannon fire uses <see cref="ShipWeaponFireLogic"/>: shared energy pool with
    /// per-barrel firePower / fireRate. Sequencing follows <see cref="ShipWeaponConfig.FireMode"/>
    /// (from <see cref="ShipFamilyDefinition.weaponFireMode"/>): Energy Hybrid volleys when
    /// affordable else round-robins; Always Fire Together waits for a full bank; Always Round-Robin
    /// never volleys. Empty mount buffer = unarmed.
    /// </para>
    /// <para>
    /// [TITAN-ORBIT] Ships cannot fire while <see cref="ShipOrbitState.InOrbitRing"/> is true —
    /// orbit rings are movement / people-transport / tractor zones only. Client anticipation
    /// mirrors this gate in <c>ClientLocalBulletVfxBridge</c>.
    /// </para>
    /// <para>
    /// Starblast-style hardening vs asteroid tunneling:
    /// (1) same-frame spawn collide on the first <c>vel*dt</c> segment (not a bare point test —
    /// wing muzzles inside side rocks must not count); (2) substep advance when travel is large
    /// vs <see cref="GemEconomyConstants.MinAsteroidHitRadius"/>; (3) collide before lifetime cull.
    /// MEGA hulls use this same Phase B spawn + collide along barrel forward after
    /// <see cref="MegaShipAutoFireSystem"/> slews the mount. Do not spawn MEGA rounds
    /// in another system.
    /// </para>
    /// Broadcasts <see cref="BulletSpawnRpc"/> / <see cref="BulletHitRpc"/> via
    /// <see cref="BulletNetNotify"/>. Damage is server-only. Not Burst-compiled — managed notify.
    /// </summary>
    // [ECS/DOTS] ShipPhysicsDriveSystem lives in PredictedFixedStepSimulationSystemGroup — cannot
    // UpdateAfter that sibling type from SimulationSystemGroup (Unity warns and ignores the attribute).
    // Ordering after the whole predicted fixed-step group keeps fire/hits after the drive tick.
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(PredictedFixedStepSimulationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    public partial struct BulletSimulationSystem : ISystem
    {
        /// <summary>
        /// Reused per-tick shot plan — avoids allocating a new array for every armed ship.
        /// </summary>
        static readonly ShipWeaponFireLogic.MountShot[] s_ShotScratch =
            new ShipWeaponFireLogic.MountShot[ShipWeaponFireLogic.MaxShotsPerTick];

        /// <summary>
        /// Derived drone hit spheres for this tick (no drone ghosts — pose from equipment + ship).
        /// </summary>
        static readonly List<DroneHitTarget> s_DroneHitTargets = new List<DroneHitTarget>(64);

        static readonly List<int> s_DroneRearScratch = new List<int>(8);
        static readonly List<int> s_DroneShieldScratch = new List<int>(8);
        static readonly List<int> s_DroneEnemyIdsScratch = new List<int>(16);
        static readonly Dictionary<int, float3> s_DroneEnemyPos = new Dictionary<int, float3>(16);
        static readonly Dictionary<int, DroneSwarmPositioning.ShieldAssignment> s_DroneShieldAssign =
            new Dictionary<int, DroneSwarmPositioning.ShieldAssignment>(8);

        /// <summary>Equipment slot of the winning drone hit (−1 when not a drone).</summary>
        static int s_BestDroneSlot = -1;

        /// <summary>Throttles expensive shield-sphere rebuilds.</summary>
        static int s_DroneHitRebuildCounter;

        /// <summary>
        /// Planetary defense hit spheres this tick (no turret ghosts — pose from planet + slot).
        /// </summary>
        static readonly List<PlanetaryDefenseHitTarget> s_DefenseHitTargets =
            new List<PlanetaryDefenseHitTarget>(64);

        /// <summary>Winning defense slot index (−1 when not a turret).</summary>
        static int s_BestDefenseSlot = -1;

        static PlanetShipFamilyConfig s_DefenseFamilyConfig;
        static PlanetaryDefenseConfig s_DefenseDefaultConfig;
        static bool s_DefenseConfigWarmed;

        EntityQuery _droneShipQuery;
        EntityQuery _allShipQuery;
        EntityQuery _asteroidHashQuery;
        EntityQuery _transportHashQuery;
        EntityQuery _defensePlanetQuery;
        EntityQuery _planetSweepQuery;

        /// <summary>
        /// Last spatial-hash build cost (ms). Dedicated netdiag reads this — ProfilerRecorder is
        /// stripped in IL2CPP release so Stopwatch is the only reliable sample.
        /// </summary>
        public static float LastHashBuildMs;

        /// <summary>Broadphase built once per tick — used by <see cref="TryResolveBulletHit"/>.</summary>
        BulletObstacleSpatialHash _obstacleHash;
        NativeList<int> _nearbyObstacles;
        NativeHashSet<int> _nearbySeen;

        /// <summary>Resources.Load once — Phase B used to call LoadDefault every tick.</summary>
        static BulletVfxBank s_CachedVfxBank;

        /// <summary>Require bullet singleton before ticking.</summary>
        public void OnCreate(ref SystemState state)
        {
            // [ECS/DOTS] RequireForUpdate — system skips OnUpdate until a bullet singleton exists.
            state.RequireForUpdate<ActiveBulletsTag>();

            _droneShipQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<ShipTag>(),
                ComponentType.ReadOnly<ShipState>(),
                ComponentType.ReadOnly<LocalTransform>(),
                ComponentType.ReadOnly<GhostOwner>(),
                ComponentType.ReadOnly<EquippedEquipmentElement>());
            _allShipQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<ShipTag>(),
                ComponentType.ReadOnly<ShipState>(),
                ComponentType.ReadOnly<LocalTransform>(),
                ComponentType.ReadOnly<GhostOwner>());
            _asteroidHashQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<AsteroidTag>(),
                ComponentType.ReadOnly<AsteroidState>(),
                ComponentType.ReadOnly<LocalTransform>());
            _transportHashQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<PeopleTransportTag>(),
                ComponentType.ReadOnly<PeopleTransportState>(),
                ComponentType.ReadOnly<LocalTransform>());
            _nearbyObstacles = new NativeList<int>(64, Allocator.Persistent);
            _nearbySeen = new NativeHashSet<int>(64, Allocator.Persistent);
            _defensePlanetQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<PlanetTag>(),
                ComponentType.ReadOnly<PlanetState>(),
                ComponentType.ReadOnly<LocalTransform>(),
                ComponentType.ReadOnly<PlanetaryDefenseSlotElement>());
            _planetSweepQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<PlanetTag>(),
                ComponentType.ReadOnly<PlanetState>(),
                ComponentType.ReadOnly<LocalTransform>(),
                ComponentType.ReadOnly<PlanetGemMoonState>());

            // [TITAN-ORBIT] Pull Upgrade Visual Scale Multiplier from the single Resources bank
            // so ScaleMultiplier on spawns matches designer Inspector values (client + server).
            var vfxBank = BulletVfxBank.LoadDefault();
            s_CachedVfxBank = vfxBank;
            if (vfxBank != null)
                BulletVisualScale.ActiveUpgradeVisualScaleMultiplier = vfxBank.UpgradeVisualScaleMultiplier;
        }

        /// <summary>Releases persistent broadphase scratch.</summary>
        public void OnDestroy(ref SystemState state)
        {
            if (_nearbyObstacles.IsCreated)
                _nearbyObstacles.Dispose();
            if (_nearbySeen.IsCreated)
                _nearbySeen.Dispose();
            if (_obstacleHash.IsCreated)
                _obstacleHash.Dispose();
            BulletObstacleSpatialHash.DisposeAsteroidCache();
        }

        /// <summary>
        /// Advance live bullets (hits + expiry), then spawn from Fire input on armed ships.
        /// New spawns run same-frame collide before entering the live buffer.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // --- Singleton bullet buffers ---
            if (!SystemAPI.TryGetSingletonEntity<ActiveBulletsTag>(out var bulletEntity))
                return;

            if (!state.EntityManager.HasBuffer<BulletElement>(bulletEntity) ||
                !state.EntityManager.HasBuffer<BulletSpawnEventElement>(bulletEntity))
                return;

            var bullets = state.EntityManager.GetBuffer<BulletElement>(bulletEntity);
            var spawnEvents = state.EntityManager.GetBuffer<BulletSpawnEventElement>(bulletEntity);
            float dt = SystemAPI.Time.DeltaTime;
            // [UNITY] World elapsed — shield hit timestamps / regen cooldowns (not moon orbit phase).
            double serverElapsed = SystemAPI.Time.ElapsedTime;

            // [NETCODE] ECB for spawn/hit RPCs, gem expulsion, and first-hit burn buffers.
            // Playback after BulletElement mutations — never AddBuffer on EntityManager mid-loop.
            var ecb = new EntityCommandBuffer(Allocator.Temp);
            BulletBankHitEffects.ClearPendingBurns();

            // --- Shared orbit clock for moon hit tests ---
            // [TITAN-ORBIT] Bullet vs moon must use ServerTick seconds (same as collider / visuals).
            int hz = 0;
            if (SystemAPI.TryGetSingleton<ClientServerTickRate>(out var tickRate))
                hz = tickRate.SimulationTickRate;
            double moonElapsed = SystemAPI.TryGetSingleton<NetworkTime>(out var networkTime)
                ? PlanetGemMoonOrbitClock.GetElapsedSeconds(networkTime, hz, includeTickFraction: false)
                : serverElapsed;

            // [TITAN-ORBIT] Map size for toroidal unwrap during swept collision tests.
            // Missing size = skip this tick (do not invent 1000×1000).
            if (!SystemAPI.TryGetSingleton<MapStateSingleton>(out var mapState) ||
                !ToroidalMapEcs.IsValidMapSize(mapState.MapWidth, mapState.MapHeight))
            {
                ecb.Dispose();
                return;
            }

            float mapW = mapState.MapWidth;
            float mapH = mapState.MapHeight;

            // Empty dedicated lobby (0 ships, 0 live rounds): do not ToEntityArray 333 asteroids
            // four times per Unity frame. Docker/GCE 2026-08-31: that gather was ~80 ms/tick,
            // wallSim≈12 Hz, 100% of one core, client snap-back. Combat still builds the hash.
            if (bullets.Length == 0 && _allShipQuery.IsEmpty)
            {
                if (_obstacleHash.IsCreated)
                    _obstacleHash.Dispose();
                _obstacleHash = default;
                LastHashBuildMs = 0f;
                spawnEvents.Clear();
                ecb.Dispose();
                return;
            }

            // --- Broadphase for ship / asteroid / transport sweeps ---
            // [TITAN-ORBIT] Built once from MapStateSingleton size. TryResolveBulletHit
            // queries nearby cells instead of walking every rock per bullet.
            // TempJob: IJob.Run() cannot touch Allocator.Temp containers.
            var hashWatch = System.Diagnostics.Stopwatch.StartNew();
            _obstacleHash = BulletObstacleSpatialHash.Build(
                state.EntityManager,
                _allShipQuery,
                _asteroidHashQuery,
                _transportHashQuery,
                mapW,
                mapH,
                Allocator.TempJob);
            LastHashBuildMs = (float)hashWatch.Elapsed.TotalMilliseconds;
            try
            {

            // Gem prefab for cargo spill after hull breaks (optional — damage still applies).
            Entity gemPrefab = Entity.Null;
            if (SystemAPI.TryGetSingleton<GamePrefabs>(out var gamePrefabs))
                gemPrefab = gamePrefabs.Gem;

            float gemSpawnServerTime = PlanetGemMoonOrbitClock.GetElapsedSecondsOrFallback(
                state.EntityManager, serverElapsed);

            // --- Derived shield drone spheres (throttle — full rebuild is expensive) ---
            // [TITAN-ORBIT] Only needed when live bullets exist; rebuild every N ticks and reuse.
            double droneTime = moonElapsed;
            DroneSwarmSimTime.Publish(droneTime);
            s_DroneHitRebuildCounter++;
            bool needDroneHits = bullets.Length > 0;
            if (needDroneHits && (s_DroneHitTargets.Count == 0 || (s_DroneHitRebuildCounter % 3) == 0))
            {
                using var droneShips = _droneShipQuery.ToEntityArray(Allocator.Temp);
                using var allShips = _allShipQuery.ToEntityArray(Allocator.Temp);
                DroneSwarmHitScan.RebuildTargets(
                    state.EntityManager, droneShips, allShips, droneTime, mapW, mapH,
                    s_DroneHitTargets, s_DroneRearScratch, s_DroneShieldScratch,
                    s_DroneEnemyIdsScratch, s_DroneEnemyPos, s_DroneShieldAssign);
            }
            else if (!needDroneHits)
            {
                s_DroneHitTargets.Clear();
            }

            // --- Planetary defense hit spheres ---
            // [TITAN-ORBIT] Rebuild every tick while bullets fly — few owned planets, and stale
            // spheres make “I shot the turret” feel random. Do not share the drone %3 throttle.
            EnsureDefenseConfigWarmed();
            if (needDroneHits)
            {
                using var defensePlanets = _defensePlanetQuery.ToEntityArray(Allocator.Temp);
                PlanetaryDefenseHitScan.RebuildTargets(
                    state.EntityManager, defensePlanets, mapW, mapH,
                    s_DefenseFamilyConfig, s_DefenseDefaultConfig, s_DefenseHitTargets);
            }
            else
            {
                s_DefenseHitTargets.Clear();
            }

            // --- Phase A: homing steer (managed), then Burst sweep ---
            for (int i = 0; i < bullets.Length; i++)
            {
                var b = bullets[i];
                if (b.Homing == 0 || b.TurnSpeedDeg <= 0.01f)
                    continue;

                bool hadLock = b.HomingHasLock != 0;
                if (RocketHomingTargeting.TryFindClosestTarget(
                        state.EntityManager, b.Position, b.OwnerTeam, b.OwnerNetworkId,
                        b.AcquireRange, mapW, mapH,
                        b.HomingLockPos, hadLock, out float3 lockPos,
                        includeOwner: TitanOrbitDebugFlags.IsSelfHarmArmed(b.Age)))
                {
                    b.HomingLockPos = lockPos;
                    b.HomingHasLock = 1;
                    float3 vel = b.Velocity;
                    RocketHomingLogic.TrySteerToward(
                        b.Position, ref vel, lockPos, b.TurnSpeedDeg, dt, mapW, mapH);
                    b.Velocity = vel;
                }
                else
                {
                    b.HomingHasLock = 0;
                }

                bullets[i] = b;
            }

            AdvanceLiveBulletsBurst(
                ref state, ref ecb, bulletEntity, bullets,
                gemPrefab, gemSpawnServerTime, dt, mapW, mapH, moonElapsed, serverElapsed);

            // Hit resolution may have destroyed entities — refresh before fire mutates the same buffers.
            bullets = state.EntityManager.GetBuffer<BulletElement>(bulletEntity);
            spawnEvents = state.EntityManager.GetBuffer<BulletSpawnEventElement>(bulletEntity);

            // --- Phase B: ship firing + same-frame spawn collide ---
            // [TITAN-ORBIT] Category Upgrade Visual Scale from the cached Resources bank.
            var vfxBankForScale = s_CachedVfxBank;

            foreach (var (input, weaponCfg, weaponState, shipState, kinematics, transform, ghostOwner, entity) in SystemAPI
                         .Query<RefRO<ShipInput>, RefRO<ShipWeaponConfig>, RefRW<ShipWeaponState>, RefRW<ShipState>, RefRO<ShipKinematics>, RefRO<LocalTransform>, RefRO<GhostOwner>>()
                         .WithAll<ShipTag>()
                         .WithEntityAccess())
            {
                if (shipState.ValueRO.IsDead)
                    continue;

                // --- Turret possession: Fire drives the pad, not ship mounts ---
                if (SystemAPI.HasComponent<ShipTurretControlState>(entity) &&
                    SystemAPI.GetComponentRO<ShipTurretControlState>(entity).ValueRO.IsControlling)
                    continue;

                // [TITAN-ORBIT] Empty mounts = intentional unarmed — no fire, no default muzzle.
                if (!SystemAPI.HasBuffer<ShipWeaponMountElement>(entity))
                    continue;

                var mounts = SystemAPI.GetBuffer<ShipWeaponMountElement>(entity);
                if (mounts.Length == 0)
                    continue;

                // --- Per-barrel cooldown tick (independent cadences) ---
                // [TITAN-ORBIT] Cooldowns keep ticking in the ring so leaving orbit does not dump
                // a stale "all barrels ready" volley the moment Fire becomes legal again.
                ShipWeaponFireLogic.TickMountCooldowns(mounts, dt);

                bool isMega = SystemAPI.HasComponent<MegaShipState>(entity) &&
                              SystemAPI.GetComponentRO<MegaShipState>(entity).ValueRO.IsMega;
                bool ownerShocked = SystemAPI.HasComponent<ShipElectricShockState>(entity) &&
                                    SystemAPI.GetComponentRO<ShipElectricShockState>(entity).ValueRO
                                        .IsActive(serverElapsed);
                bool ownerInOrbit = SystemAPI.HasComponent<ShipOrbitState>(entity) &&
                                    SystemAPI.GetComponentRO<ShipOrbitState>(entity).ValueRO.InOrbitRing;
                bool ownerMayFire = input.ValueRO.Fire.IsSet && !ownerShocked && !ownerInOrbit;

                if (!isMega)
                {
                    if (!input.ValueRO.Fire.IsSet)
                        continue;

                    // --- Electric shock: cannot fire while stunned ---
                    if (ownerShocked)
                        continue;

                    // --- Orbit ring: weapons locked ---
                    // [TITAN-ORBIT] InOrbitRing is written by ShipPhysicsDriveLogic (toroidal annulus).
                    // Fire input may still be held (player mashing shoot) — ignore it here; thrust
                    // remains the only way to leave the passive orbit motor.
                    if (ownerInOrbit)
                        continue;
                }

                // [TITAN-ORBIT] Family bank from ghosted loadout (ShipStatApplyLogic writes it).
                int bankIndex = 0;
                if (SystemAPI.HasComponent<ShipLoadoutState>(entity))
                    bankIndex = BulletBankFireResolve.ResolveFireBankIndex(
                        SystemAPI.GetComponentRO<ShipLoadoutState>(entity).ValueRO);

                int firePowerAbilityLv = 0;
                if (SystemAPI.HasComponent<ShipAttributeUpgradeState>(entity))
                    firePowerAbilityLv = SystemAPI.GetComponentRO<ShipAttributeUpgradeState>(entity).ValueRO.FirePower;
                int firePowerExtras = isMega
                    ? 0
                    : BulletBankCombatLogic.CountFirePowerExtraLevels(
                        shipState.ValueRO.ShipLevel, firePowerAbilityLv);
                float abilityEnergy = isMega
                    ? 0f
                    : BulletBankCombatLogic.GetAbilityEnergyDrain(bankIndex, firePowerExtras);

                // Per-category Upgrade Visual Scale (default 1). Global category scale is applied
                // later in BulletVisualFactory — ScaleMultiplier is fire-power upgrade only.
                float categoryUpgradeScale = vfxBankForScale != null
                    ? vfxBankForScale.GetCategoryUpgradeVisualScaleMultiplier(bankIndex)
                    : 1f;

                float3 shipVel = kinematics.ValueRO.Velocity;
                shipVel.y = 0f;

                if (isMega)
                {
                    FireMegaReadyMountsAlongBarrel(
                        ref state, ref ecb, bulletEntity, entity,
                        mounts, ownerMayFire, weaponCfg.ValueRO, ref shipState.ValueRW,
                        transform.ValueRO, ghostOwner.ValueRO,
                        bankIndex, vfxBankForScale, shipVel,
                        dt, mapW, mapH, moonElapsed, serverElapsed,
                        gemPrefab, gemSpawnServerTime);
                    continue;
                }

                // --- Volley / round-robin / hybrid per ShipWeaponConfig.FireMode ---
                if (!ShipWeaponFireLogic.TryPlanFire(
                        shipState.ValueRO.CurrentEnergy,
                        mounts,
                        weaponState.ValueRO.NextMountIndex,
                        weaponCfg.ValueRO.BulletDamage,
                        weaponCfg.ValueRO.FireRate,
                        weaponCfg.ValueRO.FireMode,
                        s_ShotScratch,
                        out int shotCount,
                        out float energySpend,
                        out int nextMountIndexAfter,
                        abilityEnergy))
                    continue;

                // --- Spawn each planned barrel with that mount’s own damage / VFX scale ---
                for (int shot = 0; shot < shotCount; shot++)
                {
                    var planned = s_ShotScratch[shot];
                    int mountIdx = planned.MountIndex;
                    var mount = mounts[mountIdx];
                    ResolveFirePose(transform.ValueRO, in mount, out float3 fireOrigin, out float3 fireForward);

                    float fireRateMul = SpawnAndCollideShipBullet(
                        ref state, ref ecb, bulletEntity, mountIdx,
                        fireOrigin, fireForward, planned.Damage,
                        weaponCfg.ValueRO, in mount, bankIndex, firePowerExtras,
                        categoryUpgradeScale, shipVel,
                        ghostOwner.ValueRO.NetworkId, (byte)shipState.ValueRO.Team,
                        shipState.ValueRO.ShipLevel,
                        dt, gemPrefab, gemSpawnServerTime, mapW, mapH,
                        moonElapsed, serverElapsed);

                    // --- Arm this barrel’s own cooldown (independent of other mounts) ---
                    mount.FireCooldown = planned.CooldownSeconds / math.max(0.05f, fireRateMul);
                    mounts[mountIdx] = mount;
                }

                // Energy equals sum of each firing barrel’s firePower this tick.
                shipState.ValueRW.CurrentEnergy = math.max(0f, shipState.ValueRO.CurrentEnergy - energySpend);
                // Advance energy-queue cursor (0 after full volley; +1 after a drip shot).
                weaponState.ValueRW.NextMountIndex = nextMountIndexAfter;
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
            BulletBankHitEffects.FlushPendingBurns(state.EntityManager);
            }
            finally
            {
                if (_obstacleHash.IsCreated)
                    _obstacleHash.Dispose();
                _obstacleHash = default;
            }
        }

        /// <summary>
        /// MEGA Phase B: same <see cref="ResolveFirePose"/> + <see cref="SpawnAndCollideShipBullet"/>
        /// as regular hulls (barrel origin + barrel forward). Only the MEGA owner may fire.
        /// Owner Shift changes where guns point (mouse). Per-mount FirePower / energy stay.
        /// Lead intercept distance from <see cref="MegaShipAutoAimSlotElement"/> grows
        /// <c>MaxDistance</c> so fleeing shots are not culled early.
        /// </summary>
        void FireMegaReadyMountsAlongBarrel(
            ref SystemState state,
            ref EntityCommandBuffer ecb,
            Entity bulletEntity,
            Entity mega,
            DynamicBuffer<ShipWeaponMountElement> mounts,
            bool ownerMayFire,
            in ShipWeaponConfig weaponCfg,
            ref ShipState shipState,
            in LocalTransform transform,
            in GhostOwner ghostOwner,
            int fallbackBankIndex,
            BulletVfxBank vfxBankForScale,
            float3 shipVel,
            float dt,
            float mapW,
            float mapH,
            double moonElapsed,
            double serverElapsed,
            Entity gemPrefab,
            float gemSpawnServerTime)
        {
            int megaOwnerNet = ghostOwner.NetworkId;
            float energy = shipState.CurrentEnergy;

            var aims = state.EntityManager.HasBuffer<MegaShipAutoAimSlotElement>(mega)
                ? state.EntityManager.GetBuffer<MegaShipAutoAimSlotElement>(mega)
                : default;

            for (int m = 0; m < mounts.Length; m++)
            {
                var mount = mounts[m];
                if (mount.FireCooldown > 0.001f || mount.FirePower <= 0.01f)
                    continue;

                if (!ownerMayFire)
                    continue;

                if (energy < mount.FirePower)
                    continue;

                int mountBank = mount.BulletBankIndex >= 0 ? mount.BulletBankIndex : fallbackBankIndex;
                float categoryUpgradeScale = vfxBankForScale != null
                    ? vfxBankForScale.GetCategoryUpgradeVisualScaleMultiplier(mountBank)
                    : 1f;

                float interceptDistance = 0f;
                if (aims.IsCreated && m < aims.Length)
                    interceptDistance = aims[m].InterceptDistance;

                ResolveFirePose(transform, in mount, out float3 fireOrigin, out float3 fireForward);
                float fireRateMul = SpawnAndCollideShipBullet(
                    ref state, ref ecb, bulletEntity, m,
                    fireOrigin, fireForward, mount.FirePower,
                    weaponCfg, in mount, mountBank, firePowerExtras: 0,
                    categoryUpgradeScale, shipVel,
                    megaOwnerNet, (byte)shipState.Team,
                    shipState.ShipLevel,
                    dt, gemPrefab, gemSpawnServerTime, mapW, mapH,
                    moonElapsed, serverElapsed,
                    interceptDistance);

                float fireRate = math.max(0.15f, mount.FireRate > 0.01f ? mount.FireRate : weaponCfg.FireRate);
                mount.FireCooldown = (1f / fireRate) / math.max(0.05f, fireRateMul);
                mounts[m] = mount;
                energy -= mount.FirePower;
            }

            shipState.CurrentEnergy = math.max(0f, energy);
        }

        /// <summary>
        /// Shared muzzle resolve — same fallback as the old Phase B inline block.
        /// </summary>
        static void ResolveFirePose(
            in LocalTransform transform,
            in ShipWeaponMountElement mount,
            out float3 fireOrigin,
            out float3 fireForward)
        {
            if (ShipWeaponPose.TryResolve(transform, mount, out fireOrigin, out fireForward))
                return;

            float3 localFwd = math.mul(mount.LocalRotation, new float3(0f, 0f, 1f));
            localFwd.y = 0f;
            if (math.lengthsq(localFwd) < 0.0001f)
                localFwd = new float3(0f, 0f, 1f);
            else
                localFwd = math.normalize(localFwd);
            fireForward = math.rotate(transform.Rotation, localFwd);
            fireForward.y = 0f;
            if (math.lengthsq(fireForward) < 0.0001f)
                fireForward = new float3(0f, 0f, 1f);
            else
                fireForward = math.normalize(fireForward);
            float ecsScale = math.max(0.25f, transform.Scale);
            float3 presentationLocal = mount.LocalPosition
                * (BodyCollisionMath.ShipPresentationScale * ecsScale);
            fireOrigin = transform.Position + math.rotate(transform.Rotation, presentationLocal);
        }

        /// <summary>
        /// One ship/MEGA shot: modifiers, spawn RPC, same-frame <see cref="TryResolveBulletHit"/>.
        /// Misses stay in the live buffer for Phase A next tick — never advance twice this tick.
        /// </summary>
        float SpawnAndCollideShipBullet(
            ref SystemState state,
            ref EntityCommandBuffer ecb,
            Entity bulletEntity,
            int mountIdx,
            float3 fireOrigin,
            float3 fireForward,
            float damage,
            in ShipWeaponConfig weaponCfg,
            in ShipWeaponMountElement mount,
            int bankIndex,
            int firePowerExtras,
            float categoryUpgradeScale,
            float3 shipVel,
            int ownerNetworkId,
            byte ownerTeam,
            int shipLevel,
            float dt,
            Entity gemPrefab,
            float gemSpawnServerTime,
            float mapW,
            float mapH,
            double moonElapsed,
            double serverElapsed,
            float interceptDistance = 0f)
        {
            float fallbackRefDamage = weaponCfg.ReferenceBulletDamage > 0f
                ? weaponCfg.ReferenceBulletDamage
                : BulletVisualScale.DefaultReferenceBulletDamage;
            float refSpeed = weaponCfg.ReferenceBulletSpeed > 0f
                ? weaponCfg.ReferenceBulletSpeed
                : BulletVisualScale.DefaultReferenceBulletSpeed;
            float refDamage = mount.ReferenceFirePower > 0.01f
                ? mount.ReferenceFirePower
                : fallbackRefDamage;
            float muzzleSpeed = BulletShotMath.ResolveMuzzleSpeed(mount.BulletSpeed, weaponCfg.BulletSpeed);
            float refMuzzleSpeed = mount.BulletSpeed > 0.01f ? mount.BulletSpeed : refSpeed;
            float maxDistanceForLife = BulletShotMath.ResolveMaxDistance(
                mount.BulletRange, weaponCfg.BulletMaxDistance);
            float lifetime = mount.BulletSpeed > 0.01f
                ? math.max(0.25f, maxDistanceForLife / math.max(1f, muzzleSpeed))
                : weaponCfg.BulletLifetime;

            var plan = BulletShotMath.Build(
                fireOrigin,
                fireForward,
                shipVel,
                damage,
                muzzleSpeed,
                weaponCfg.BulletMaxDistance,
                lifetime,
                weaponCfg.FireRate,
                mount.BulletRange,
                weaponCfg.BulletScale,
                refDamage,
                refMuzzleSpeed,
                bankIndex,
                firePowerExtras,
                categoryUpgradeScale);

            byte homing = 0;
            float turnSpeedDeg = 0f;
            float acquireRange = 0f;
            if (RocketHomingFire.TryApply(
                    bankIndex, shipLevel, fireForward, ref plan,
                    out turnSpeedDeg, out acquireRange))
            {
                homing = 1;
            }

            // --- Lead flight budget (MEGA auto-aim) ---
            // [TITAN-ORBIT] Same bug planetary turrets had: intercept for a fleeing /
            // crossing target sits past current range. Without this, the sim culls the
            // round before it arrives and it looks like undershoot.
            // Rockets use catalog lifetime / unlimited travel — do not clamp them to
            // MaxBulletTravelDistance or they cannot chase.
            if (homing == 0 && interceptDistance > 0.5f)
            {
                float engage = plan.MaxDistance;
                float lead = PlanetaryDefenseAimMath.ComputeBulletMaxDistance(
                    engage, interceptDistance);
                // MEGA volleys were flying to the lead point with no cap (48u+).
                // That kept dozens of live rounds in sim/VFX. Cap travel; undershoot
                // a fleeing target past MaxBulletTravelDistance rather than hitch.
                plan.MaxDistance = math.min(lead, MegaShipCatalog.MaxBulletTravelDistance);
                if (plan.MaxDistance > engage + 0.01f)
                {
                    float planarMuzzleSpeed = math.length(plan.Velocity - shipVel);
                    if (planarMuzzleSpeed < 1f)
                        planarMuzzleSpeed = 1f;
                    plan.Lifetime = math.max(plan.Lifetime, plan.MaxDistance / planarMuzzleSpeed);
                }
            }

            uint sequence = BulletVfxBridge.NextSequence();
            var spawn = new BulletElement
            {
                Position = plan.Origin,
                Velocity = plan.Velocity,
                MaxDistance = plan.MaxDistance,
                Lifetime = plan.Lifetime,
                Damage = plan.Damage,
                OwnerNetworkId = ownerNetworkId,
                OwnerTeam = ownerTeam,
                Sequence = sequence,
                BankIndex = bankIndex,
                ScaleMultiplier = plan.VisualScale,
                FirePowerExtraLevels = firePowerExtras,
                Homing = homing,
                TurnSpeedDeg = turnSpeedDeg,
                AcquireRange = acquireRange,
            };

            var spawnEvents = state.EntityManager.GetBuffer<BulletSpawnEventElement>(bulletEntity);
            spawnEvents.Add(new BulletSpawnEventElement
            {
                SpawnPosition = spawn.Position,
                Velocity = spawn.Velocity,
                Lifetime = spawn.Lifetime,
                MaxDistance = spawn.MaxDistance,
                Damage = spawn.Damage,
                OwnerTeam = spawn.OwnerTeam,
                Sequence = spawn.Sequence,
                BankIndex = bankIndex,
                ScaleMultiplier = plan.VisualScale,
            });

            BulletNetNotify.SendSpawn(ref ecb, spawn, mountIdx);

            // --- Same-frame spawn collide (substepped — MEGA sniper + shipVel can skip a rock) ---
            BulletFlight.GetStep(plan.Origin, plan.Velocity, dt, out float3 firstEnd, out int spawnSteps);
            float3 cursor = plan.Origin;
            bool spawnHit = false;
            float3 spawnHitPoint = firstEnd;
            float spawnAsteroidHealthAfter = -1f;
            int spawnPdPlanetId = 0;
            byte spawnPdSlotIndex = 0;
            float spawnPdHealthAfter = -1f;
            GatherHashedObstaclesForSegment(plan.Origin, firstEnd);
            for (int s = 0; s < spawnSteps; s++)
            {
                float3 next = BulletFlight.SubstepEnd(plan.Origin, firstEnd, s, spawnSteps);
                if (TryResolveBulletHit(
                        ref state, ecb, gemPrefab, gemSpawnServerTime,
                        in spawn, cursor, next, mapW, mapH, moonElapsed, serverElapsed,
                        out spawnHitPoint, out spawnAsteroidHealthAfter,
                        out spawnPdPlanetId, out spawnPdSlotIndex,
                        out spawnPdHealthAfter,
                        hashedObstaclesAlreadyGathered: true))
                {
                    spawnHit = true;
                    break;
                }

                cursor = next;
            }

            var bullets = state.EntityManager.GetBuffer<BulletElement>(bulletEntity);
            if (spawnHit)
            {
                BulletNetNotify.SendHit(
                    ref ecb, spawn, spawnHitPoint, spawnAsteroidHealthAfter,
                    spawnPdPlanetId, spawnPdSlotIndex, spawnPdHealthAfter,
                    mountIdx);
            }
            else
            {
                bullets.Add(spawn);
            }

            return plan.FireRateMul;
        }

        /// <summary>
        /// Which body category won the nearest-hit scan for one bullet segment.
        /// Planet blocks without damage; the rest apply server-authoritative damage.
        /// </summary>
        enum BulletHitKind : byte
        {
            /// <summary>No intersection this segment.</summary>
            None = 0,
            /// <summary>Planet body — absorbs the bullet, no HP write.</summary>
            Planet = 1,
            /// <summary>Enemy gem-moon shield on a planet entity.</summary>
            Moon = 2,
            /// <summary>Enemy ship hull.</summary>
            Ship = 3,
            /// <summary>Asteroid rock (sets <see cref="AsteroidState.IsDestroyed"/> at 0 HP).</summary>
            Asteroid = 4,
            /// <summary>Enemy people transport.</summary>
            Transport = 5,
            /// <summary>
            /// Derived drone body (shield / fighter / mining). Damages equipment RemainingCharges.
            /// </summary>
            Drone = 6,
            /// <summary>
            /// Planetary defense turret on an owned planet (ghosted slot buffer HP).
            /// </summary>
            PlanetaryDefense = 7,
        }

        /// <summary>
        /// Burst-advances every live bullet, then applies managed hit Pass 2 + RPCs.
        /// </summary>
        void AdvanceLiveBulletsBurst(
            ref SystemState state,
            ref EntityCommandBuffer ecb,
            Entity bulletEntity,
            DynamicBuffer<BulletElement> bullets,
            Entity gemPrefab,
            float gemSpawnServerTime,
            float dt,
            float mapW,
            float mapH,
            double moonElapsed,
            double serverElapsed)
        {
            int n = bullets.Length;
            if (n <= 0)
                return;

            var jobBullets = new NativeArray<BulletElement>(n, Allocator.TempJob);
            var outcomes = new NativeArray<byte>(n, Allocator.TempJob);
            var hitPoints = new NativeArray<float3>(n, Allocator.TempJob);
            var stepFrom = new NativeArray<float3>(n, Allocator.TempJob);
            var stepTo = new NativeArray<float3>(n, Allocator.TempJob);
            var planets = new NativeList<BulletJobPlanet>(16, Allocator.TempJob);
            NativeArray<PlanetaryDefenseHitTarget> defense = default;
            NativeArray<DroneHitTarget> drones = default;
            try
            {
                for (int i = 0; i < n; i++)
                    jobBullets[i] = bullets[i];

                using var planetStates = _planetSweepQuery.ToComponentDataArray<PlanetState>(Allocator.Temp);
                using var planetXfs = _planetSweepQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
                using var moonStates = _planetSweepQuery.ToComponentDataArray<PlanetGemMoonState>(Allocator.Temp);
                for (int p = 0; p < planetStates.Length; p++)
                {
                    float scale = math.max(0.25f, planetXfs[p].Scale);
                    bool home = planetStates[p].IsHomePlanet;
                    planets.Add(new BulletJobPlanet
                    {
                        Position = planetXfs[p].Position,
                        Scale = scale,
                        MoonBodyRadius = PlanetGemMoonMath.GetMoonBodyRadiusWorld(scale, home),
                        MoonShieldRadius = PlanetGemMoonMath.GetMoonBulletHitRadiusWorld(
                            scale, home, moonStates[p].CurrentShield, attackerFriendlyToMoon: false),
                        PlanetLevel = planetStates[p].PlanetLevel,
                        PlanetId = planetStates[p].PlanetId,
                        Ownership = (byte)planetStates[p].Ownership,
                        IsHome = home,
                        HasMoon = true,
                    });
                }

                defense = new NativeArray<PlanetaryDefenseHitTarget>(
                    s_DefenseHitTargets.Count, Allocator.TempJob);
                for (int i = 0; i < s_DefenseHitTargets.Count; i++)
                    defense[i] = s_DefenseHitTargets[i];

                drones = new NativeArray<DroneHitTarget>(s_DroneHitTargets.Count, Allocator.TempJob);
                for (int i = 0; i < s_DroneHitTargets.Count; i++)
                    drones[i] = s_DroneHitTargets[i];

                var job = new BulletAdvanceJob
                {
                    Dt = dt,
                    MapW = mapW,
                    MapH = mapH,
                    MoonElapsed = moonElapsed,
                    Bullets = jobBullets,
                    Outcomes = outcomes,
                    HitPoints = hitPoints,
                    StepFrom = stepFrom,
                    StepTo = stepTo,
                    Hash = _obstacleHash,
                    Nearby = _nearbyObstacles,
                    Seen = _nearbySeen,
                    Planets = planets.AsArray(),
                    Defense = defense,
                    Drones = drones,
                    AllowSelfHarmHits = TitanOrbitDebugFlags.SelfHarmRocketsAndMines ? (byte)1 : (byte)0,
                    SelfHarmArmDelay = TitanOrbitDebugFlags.SelfHarmArmDelaySeconds,
                };
                job.Run();

                for (int i = n - 1; i >= 0; i--)
                {
                    byte outcome = outcomes[i];
                    if (outcome == BulletAdvanceJob.OutcomeExpire)
                    {
                        bullets = state.EntityManager.GetBuffer<BulletElement>(bulletEntity);
                        bullets.RemoveAtSwapBack(i);
                        continue;
                    }

                    if (outcome == BulletAdvanceJob.OutcomeFly)
                    {
                        bullets = state.EntityManager.GetBuffer<BulletElement>(bulletEntity);
                        bullets[i] = jobBullets[i];
                        continue;
                    }

                    var b = jobBullets[i];
                    GatherHashedObstaclesForSegment(stepFrom[i], stepTo[i]);
                    if (TryResolveBulletHit(
                            ref state, ecb, gemPrefab, gemSpawnServerTime,
                            in b, stepFrom[i], stepTo[i], mapW, mapH, moonElapsed, serverElapsed,
                            out float3 hitPoint, out float asteroidHealthAfter,
                            out int pdPlanetId, out byte pdSlotIndex, out float pdHealthAfter,
                            hashedObstaclesAlreadyGathered: true))
                    {
                        BulletNetNotify.SendHit(
                            ref ecb, b, hitPoint, asteroidHealthAfter,
                            pdPlanetId, pdSlotIndex, pdHealthAfter);
                        bullets = state.EntityManager.GetBuffer<BulletElement>(bulletEntity);
                        bullets.RemoveAtSwapBack(i);
                    }
                    else
                    {
                        b.Position = stepTo[i];
                        b.Age += dt;
                        b.Traveled += math.distance(stepFrom[i], stepTo[i]);
                        bullets = state.EntityManager.GetBuffer<BulletElement>(bulletEntity);
                        bullets[i] = b;
                    }
                }
            }
            finally
            {
                if (jobBullets.IsCreated)
                    jobBullets.Dispose();
                if (outcomes.IsCreated)
                    outcomes.Dispose();
                if (hitPoints.IsCreated)
                    hitPoints.Dispose();
                if (stepFrom.IsCreated)
                    stepFrom.Dispose();
                if (stepTo.IsCreated)
                    stepTo.Dispose();
                if (planets.IsCreated)
                    planets.Dispose();
                if (defense.IsCreated)
                    defense.Dispose();
                if (drones.IsCreated)
                    drones.Dispose();
            }
        }

        /// <summary>
        /// One hash gather for the whole [from, to] step. Substeps reuse this list.
        /// </summary>
        void GatherHashedObstaclesForSegment(float3 from, float3 to)
        {
            if (_obstacleHash.IsCreated && _nearbyObstacles.IsCreated && _nearbySeen.IsCreated)
                _obstacleHash.GatherAlongSegment(from, to, _nearbyObstacles, _nearbySeen);
        }

        /// <summary>
        /// Swept segment hit test + damage for planets, moons, ships, asteroids, transports.
        /// Shared by advance substeps and same-frame spawn collide.
        /// <para>
        /// [TITAN-ORBIT] Nearest contact along the segment wins (smallest t), matching
        /// <c>BulletCosmeticHitQuery.TryHitSegment</c>. Older code returned the first ECS-query
        /// intersection — that damaged rocks behind the aim target while client floats showed
        /// 0 HP on the front rock that never received server damage.
        /// </para>
        /// </summary>
        /// <param name="state">Server system state (vitals write / transport destroy).</param>
        /// <param name="ecb">Structural buffer for RPCs and ship gem expulsion spawns.</param>
        /// <param name="gemPrefab"><see cref="GamePrefabs.Gem"/> or Null when unavailable.</param>
        /// <param name="gemSpawnServerTime">Server elapsed for gem lifetime stamps.</param>
        /// <param name="b">Bullet dealing damage (OwnerTeam / Damage).</param>
        /// <param name="from">Segment start (unbounded XZ).</param>
        /// <param name="to">Segment end (unbounded XZ). Equal to <paramref name="from"/> = point test.</param>
        /// <param name="mapW">Toroidal map width.</param>
        /// <param name="mapH">Toroidal map height.</param>
        /// <param name="moonElapsed">ServerTick seconds for gem-moon orbit phase.</param>
        /// <param name="serverElapsed">World elapsed for shield hit timestamps.</param>
        /// <param name="hitPoint">Nearest contact along the segment when true.</param>
        /// <param name="asteroidHealthAfter">
        /// Asteroid Health after this hit, or &lt; 0 when the winner was not an asteroid.
        /// </param>
        /// <param name="planetaryDefensePlanetId">
        /// Stable planet id when a PD turret was damaged; 0 otherwise.
        /// </param>
        /// <param name="planetaryDefenseSlotIndex">Slot index when PlanetId &gt; 0.</param>
        /// <param name="planetaryDefenseHealthAfter">
        /// Turret Health after damage (0 = destroyed); &lt; 0 / unused when PlanetId is 0.
        /// </param>
        /// <returns>True when this segment scored a hit and applied damage (or planet block).</returns>
        bool TryResolveBulletHit(
            ref SystemState state,
            EntityCommandBuffer ecb,
            Entity gemPrefab,
            float gemSpawnServerTime,
            in BulletElement b,
            float3 from,
            float3 to,
            float mapW,
            float mapH,
            double moonElapsed,
            double serverElapsed,
            out float3 hitPoint,
            out float asteroidHealthAfter,
            out int planetaryDefensePlanetId,
            out byte planetaryDefenseSlotIndex,
            out float planetaryDefenseHealthAfter,
            bool hashedObstaclesAlreadyGathered = false)
        {
            hitPoint = to;
            asteroidHealthAfter = -1f;
            planetaryDefensePlanetId = 0;
            planetaryDefenseSlotIndex = 0;
            planetaryDefenseHealthAfter = -1f;

            BulletBankCombatLogic.TryGetProfile(b.BankIndex, out BulletBankProfile profile);
            bool healFriendly = BulletBankCombatLogic.HasHealFriendly(profile);

            // --- Pass 1: scan every obstacle, keep nearest contact (smallest segment t) ---
            // [TITAN-ORBIT] Do not apply damage inside the scan — a farther body must not win
            // just because its category or entity index appears earlier in the query.
            float bestT = float.MaxValue;
            float3 bestHit = to;
            var bestKind = BulletHitKind.None;
            Entity bestEntity = Entity.Null;
            s_BestDroneSlot = -1;
            s_BestDefenseSlot = -1;

            // --- Planets + gem-moon shields ---
            foreach (var (planetState, planetTransform, moonState, planetEntity) in SystemAPI
                         .Query<RefRO<PlanetState>, RefRO<LocalTransform>, RefRO<PlanetGemMoonState>>()
                         .WithAll<PlanetTag>()
                         .WithEntityAccess())
            {
                float planetSize = math.max(0.25f, planetTransform.ValueRO.Scale);
                float3 planetPos = planetTransform.ValueRO.Position;

                // Planet body blocks the shot (no HP on planets — still stops the bullet).
                if (BulletCollision.SegmentHitsPlanetToroidal(
                        from, to, planetPos, planetSize, mapW, mapH, out float3 planetHit) &&
                    TryKeepNearestHit(from, to, planetHit, ref bestT, ref bestHit))
                {
                    bestKind = BulletHitKind.Planet;
                    bestEntity = planetEntity;
                }

                // --- Gem moon: body always blocks; friendly shields are pass-through ---
                // [TITAN-ORBIT] Allied shots fly through the shield bubble and only stop on the
                // solid moon (like a small asteroid). Enemy/neutral still collide with the shield
                // shell when it is up. Damage in Pass 2 early-outs on friendlies.
                var attackerTeam = (TeamId)b.OwnerTeam;
                bool friendlyMoon = PlanetGemMoonCombatLogic.IsTeamFriendlyToMoon(
                    planetState.ValueRO.Ownership, attackerTeam);
                float hitRadius = PlanetGemMoonMath.GetMoonBulletHitRadiusWorld(
                    planetSize,
                    planetState.ValueRO.IsHomePlanet,
                    moonState.ValueRO.CurrentShield,
                    attackerFriendlyToMoon: friendlyMoon);

                if (BulletCollision.SegmentHitsMoonNear(
                        from, to, planetPos, planetSize,
                        planetState.ValueRO.PlanetLevel, planetState.ValueRO.PlanetId, moonElapsed,
                        planetState.ValueRO.IsHomePlanet, hitRadius, mapW, mapH, out float3 moonHit) &&
                    TryKeepNearestHit(from, to, moonHit, ref bestT, ref bestHit))
                {
                    bestKind = BulletHitKind.Moon;
                    bestEntity = planetEntity;
                }
            }

            // --- Ships / asteroids / transports (spatial hash) ---
            // [TITAN-ORBIT] Map size from MapStateSingleton at hash build. Exact tests
            // are unchanged — only the candidate set is nearby cells, not the whole belt.
            if (_obstacleHash.IsCreated && _nearbyObstacles.IsCreated && _nearbySeen.IsCreated)
            {
                if (!hashedObstaclesAlreadyGathered)
                    _obstacleHash.GatherAlongSegment(from, to, _nearbyObstacles, _nearbySeen);
                bool wantShip = AllowsHitKind(b.DamageFilter, BulletHitKind.Ship);
                bool wantAsteroid = AllowsHitKind(b.DamageFilter, BulletHitKind.Asteroid);
                bool wantTransport = AllowsHitKind(b.DamageFilter, BulletHitKind.Transport);
                var em = state.EntityManager;
                for (int n = 0; n < _nearbyObstacles.Length; n++)
                {
                    var entry = _obstacleHash.Entries[_nearbyObstacles[n]];
                    switch (entry.Kind)
                    {
                        case BulletObstacleKind.Ship:
                            if (wantShip)
                                ConsiderHashedShip(
                                    em, in b, from, to, mapW, mapH, healFriendly, entry.Entity,
                                    ref bestT, ref bestHit, ref bestKind, ref bestEntity);
                            break;
                        case BulletObstacleKind.Asteroid:
                            if (wantAsteroid)
                                ConsiderHashedAsteroid(
                                    em, in b, from, to, mapW, mapH, entry.Entity,
                                    ref bestT, ref bestHit, ref bestKind, ref bestEntity);
                            break;
                        case BulletObstacleKind.Transport:
                            if (wantTransport)
                                ConsiderHashedTransport(
                                    em, in b, from, to, mapW, mapH, entry.Entity,
                                    ref bestT, ref bestHit, ref bestKind, ref bestEntity);
                            break;
                    }
                }
            }

            // --- Derived drones (shield wall / escort bodies) ---
            // [TITAN-ORBIT] No PhysX — sphere tests vs EvaluateSlotPose centers rebuilt this tick.
            // Mining bolts pass through enemy drones; fighters can still clip escort HP.
            if (AllowsHitKind(b.DamageFilter, BulletHitKind.Drone) &&
                DroneSwarmHitScan.TryKeepNearestDroneHit(
                    in b, from, to, mapW, mapH, s_DroneHitTargets,
                    ref bestT, ref bestHit, out int droneIdx))
            {
                DroneHitTarget hitDrone = s_DroneHitTargets[droneIdx];
                bestKind = BulletHitKind.Drone;
                bestEntity = hitDrone.ShipEntity;
                s_BestDroneSlot = hitDrone.SlotIndex;
            }

            // --- Planetary defense turrets (derived spheres on owned planets) ---
            // Scan even when a nearer planet-body hit already won — PreferDefenseOverPlanetBody
            // below lets pad shots land instead of dying on the hull behind the turret.
            float defenseBestT = float.MaxValue;
            float3 defenseBestHit = to;
            int defenseIdx = -1;
            if (AllowsHitKind(b.DamageFilter, BulletHitKind.PlanetaryDefense) &&
                PlanetaryDefenseHitScan.TryKeepNearestTurretHit(
                    in b, from, to, mapW, mapH, s_DefenseHitTargets,
                    ref defenseBestT, ref defenseBestHit, out defenseIdx) &&
                defenseIdx >= 0)
            {
                bool takeDefense = defenseBestT <= bestT;
                if (!takeDefense &&
                    bestKind == BulletHitKind.Planet &&
                    bestEntity == s_DefenseHitTargets[defenseIdx].PlanetEntity)
                {
                    // Same planet: planet chord was slightly nearer than the pad sphere, but the
                    // shot was clearly meant for the turret (common when aiming at the mesh).
                    takeDefense = PlanetaryDefenseHitScan.PreferDefenseOverPlanetBody(
                        defenseBestT, bestT);
                }

                if (takeDefense)
                {
                    bestT = defenseBestT;
                    bestHit = defenseBestHit;
                    bestKind = BulletHitKind.PlanetaryDefense;
                    bestEntity = s_DefenseHitTargets[defenseIdx].PlanetEntity;
                    s_BestDefenseSlot = s_DefenseHitTargets[defenseIdx].SlotIndex;
                }
            }

            // --- No intersection ---
            if (bestKind == BulletHitKind.None || bestEntity == Entity.Null)
                return false;

            // --- Pass 2: apply damage (or planet block) only to the nearest winner ---
            hitPoint = bestHit;
            float hitDamage = BulletBankCombatLogic.ResolveHitDamage(
                profile, BulletBankAbilityTargeting.FromHitKind((byte)bestKind), b.Damage,
                b.FirePowerExtraLevels);

            // Heal mode: allies gain HP; enemy ships / drones / PD / moons / transports take
            // no damage and no on-hit status. Asteroids still take full damage.
            if (healFriendly && bestKind != BulletHitKind.Asteroid)
            {
                if (bestKind == BulletHitKind.Ship &&
                    state.EntityManager.HasComponent<ShipState>(bestEntity))
                {
                    var writable = SystemAPI.GetComponentRW<ShipState>(bestEntity);
                    ref var ship = ref writable.ValueRW;
                    if (ship.Team == (TeamId)b.OwnerTeam && !ship.IsDead)
                    {
                        float heal = BulletBankCombatLogic.GetHealFriendlyAmount(
                            profile, b.FirePowerExtraLevels)
                            * BulletBankHitEffects.ResolveStrengthScale(b.StrengthScale);
                        if (heal > 0f)
                            ship.Health = math.min(ship.MaxHealth, ship.Health + heal);
                    }
                }

                return true;
            }

            switch (bestKind)
            {
                case BulletHitKind.Planet:
                    // Body absorbs the round — no component write.
                    TrySpawnWell(ref state, hitPoint, profile, serverElapsed, mapW, mapH, in b, ecb, gemPrefab, hitDamage, bestEntity);
                    return true;

                case BulletHitKind.Moon:
                {
                    // Gem-moon shield lives on the planet entity (same as bake / orbit systems).
                    // Bullet always stops here (friendly or not). ApplyBulletDamage no-ops on
                    // same-team moons so home moons block without taking HP.
                    if (!state.EntityManager.HasComponent<PlanetGemMoonState>(bestEntity) ||
                        !state.EntityManager.HasComponent<PlanetState>(bestEntity))
                    {
                        TrySpawnWell(ref state, hitPoint, profile, serverElapsed, mapW, mapH, in b, ecb, gemPrefab, hitDamage, bestEntity);
                        return true;
                    }

                    var moon = state.EntityManager.GetComponentData<PlanetGemMoonState>(bestEntity);
                    var planet = state.EntityManager.GetComponentData<PlanetState>(bestEntity);
                    PlanetGemMoonCombatLogic.ApplyBulletDamage(
                        ref moon,
                        hitDamage,
                        (TeamId)b.OwnerTeam,
                        planet.Ownership,
                        serverElapsed);
                    state.EntityManager.SetComponentData(bestEntity, moon);
                    TrySpawnWell(ref state, hitPoint, profile, serverElapsed, mapW, mapH, in b, ecb, gemPrefab, hitDamage, bestEntity);
                    return true;
                }

                case BulletHitKind.Ship:
                {
                    // [TITAN-ORBIT] Hull first, leftover damage spills gems 1:1; death only when both empty.
                    var writable = SystemAPI.GetComponentRW<ShipState>(bestEntity);
                    ref var ship = ref writable.ValueRW;

                    // Fully moon-docked ships are immune (same as legacy Starship).
                    bool moonImmune = false;
                    if (state.EntityManager.HasComponent<ShipMoonDockState>(bestEntity))
                    {
                        var moonDock = state.EntityManager.GetComponentData<ShipMoonDockState>(bestEntity);
                        moonImmune = moonDock.MoonPlanetId != 0 &&
                                     moonDock.LandingProgress >= GemEconomyConstants.MoonLandingCompleteThreshold;
                    }

                    // --- HealFriendly: ally hulls gain HP instead of taking damage ---
                    if (healFriendly && ship.Team == (TeamId)b.OwnerTeam)
                    {
                        float heal = BulletBankCombatLogic.GetHealFriendlyAmount(profile, b.FirePowerExtraLevels)
                            * BulletBankHitEffects.ResolveStrengthScale(b.StrengthScale);
                        if (heal > 0f && !ship.IsDead)
                            ship.Health = math.min(ship.MaxHealth, ship.Health + heal);
                        TrySpawnWell(ref state, hitPoint, profile, serverElapsed, mapW, mapH, in b, ecb, gemPrefab, hitDamage, bestEntity);
                        return true;
                    }

                    float health = ship.Health;
                    float gems = ship.CurrentGems;
                    bool isDead = ship.IsDead;
                    bool selfHarmHit = TitanOrbitDebugFlags.IsHomingSelfHarmArmed(b.Homing, b.Age);
                    var damageTeam = selfHarmHit && ship.Team == (TeamId)b.OwnerTeam
                        ? TeamId.None
                        : (TeamId)b.OwnerTeam;
                    var result = ShipDamageLogic.ApplyHullAndGemDamage(
                        ref health,
                        ref gems,
                        ref isDead,
                        CardEffectQuery.ScaleIncomingDamage(state.EntityManager, bestEntity, hitDamage),
                        ship.Team,
                        damageTeam,
                        gemExpulsionPerHullDamage: ShipDamageLogic.ExcessDamageGemExpulsionPerHullDamage,
                        isImmune: moonImmune);

                    ship.Health = health;
                    ship.CurrentGems = gems;
                    ship.IsDead = isDead;

                    if (result.AppliedHullDamage &&
                        state.EntityManager.HasComponent<ShipVitalsState>(bestEntity))
                    {
                        var vitals = state.EntityManager.GetComponentData<ShipVitalsState>(bestEntity);
                        vitals.LastHullDamageTime = serverElapsed;
                        state.EntityManager.SetComponentData(bestEntity, vitals);
                    }

                    // --- Kill attribution (last damager for ShipMatchStats.Kills) ---
                    // [TITAN-ORBIT] Stamp whenever real damage landed so ShipDeathRecordingSystem
                    // can credit the bullet owner even if death happens on a later gem-spill hit.
                    if (result.AppliedHullDamage || result.GemsToExpel > 0.0001f || result.BecameDead)
                    {
                        float2 impulse = new float2(b.Velocity.x, b.Velocity.z);
                        ShipMatchStatsLogic.SetLastDamager(
                            state.EntityManager,
                            bestEntity,
                            b.OwnerNetworkId,
                            (float)serverElapsed,
                            impulse,
                            hitDamage);
                    }

                    if (result.GemsToExpel > 0.0001f &&
                        state.EntityManager.HasComponent<LocalTransform>(bestEntity))
                    {
                        float3 shipPos = state.EntityManager.GetComponentData<LocalTransform>(bestEntity).Position;
                        // [TITAN-ORBIT] Stamp GhostOwner.NetworkId so the hit ship cannot reclaim
                        // spilled gems until GemExplosionSettings.SelfPickupBlockSeconds elapses.
                        int sourceNetworkId = 0;
                        if (state.EntityManager.HasComponent<GhostOwner>(bestEntity))
                            sourceNetworkId = state.EntityManager.GetComponentData<GhostOwner>(bestEntity).NetworkId;

                        // Legacy bullet expulsion intensity default was 0.5.
                        ShipGemExpulsion.SpawnFromDamage(
                            ecb,
                            gemPrefab,
                            shipPos,
                            result.GemsToExpel,
                            intensity: 0.5f,
                            salt: (uint)(bestEntity.Index * 19349663) ^ (uint)(serverElapsed * 1000.0),
                            gemSpawnServerTime,
                            sourceNetworkId);
                    }

                    if (!moonImmune && !ship.IsDead)
                    {
                        float3 shipPosForFx = state.EntityManager.HasComponent<LocalTransform>(bestEntity)
                            ? state.EntityManager.GetComponentData<LocalTransform>(bestEntity).Position
                            : hitPoint;
                        BulletBankHitEffects.ApplyShipOnHit(
                            state.EntityManager, ecb, bestEntity, in b, hitPoint, shipPosForFx,
                            profile, serverElapsed, mapW, mapH);
                    }

                    TrySpawnWell(ref state, hitPoint, profile, serverElapsed, mapW, mapH, in b, ecb, gemPrefab, hitDamage, bestEntity);
                    return true;
                }

                case BulletHitKind.Asteroid:
                {
                    // [TITAN-ORBIT] Health → 0 sets IsDestroyed; AsteroidDestructionSystem despawns
                    // (also accepts Health<=0 alone so a missed flag cannot leave zombies).
                    var asteroid = state.EntityManager.GetComponentData<AsteroidState>(bestEntity);
                    if (asteroid.IsDestroyed || asteroid.Health <= 0f)
                    {
                        // Still report 0 so clients can hide a lingering proxy.
                        asteroidHealthAfter = 0f;
                        // [PHYSICS] A 0-HP zombie that missed DestroyEntity still blocks the hull.
                        AsteroidDeathPhysics.QueueStripColliders(ecb, state.EntityManager, bestEntity);
                        return true;
                    }

                    asteroid.Health -= hitDamage;
                    // [TITAN-ORBIT] Destroy yellow gems use LastInteractTeam ∩ TerritoryTeamsMask.
                    asteroid.LastInteractTeam = (TeamId)b.OwnerTeam;
                    if (asteroid.Health <= 0f)
                    {
                        asteroid.Health = 0f;
                        asteroid.IsDestroyed = true;
                    }

                    // Publish post-hit HP on BulletHitRpc — ghost snapshots lag MaxSendRate.
                    asteroidHealthAfter = asteroid.Health;
                    state.EntityManager.SetComponentData(bestEntity, asteroid);

                    // [PHYSICS] Lethal hits drop the hull immediately. Waiting for
                    // AsteroidDestructionSystem left a server PhysX sphere after the client hid
                    // the mesh — the predicted ship then reconciled into empty space.
                    if (asteroid.IsDestroyed || asteroid.Health <= 0.01f)
                        AsteroidDeathPhysics.QueueStripColliders(ecb, state.EntityManager, bestEntity);
                    else if (profile != null &&
                             profile.TryGetResolvedAbility(
                                 BulletBankAbilityType.BurnOverTime, b.FirePowerExtraLevels,
                                 out BulletBankAbility asteroidBurn) &&
                             asteroidBurn != null)
                        BulletBankHitEffects.ApplyAsteroidBurn(
                            state.EntityManager, ecb, bestEntity, asteroidBurn, in b, hitPoint,
                            state.EntityManager.HasComponent<LocalTransform>(bestEntity)
                                ? state.EntityManager.GetComponentData<LocalTransform>(bestEntity).Position
                                : hitPoint,
                            serverElapsed, mapW, mapH);

                    TrySpawnWell(ref state, hitPoint, profile, serverElapsed, mapW, mapH, in b, ecb, gemPrefab, hitDamage, bestEntity);
                    return true;
                }

                case BulletHitKind.Transport:
                {
                    var t = state.EntityManager.GetComponentData<PeopleTransportState>(bestEntity);
                    t.Health -= hitDamage;
                    if (t.Health <= 0f)
                        PeopleTransportSimulationSystem.DestroyFromBulletDamage(ref state, bestEntity, t);
                    else
                        state.EntityManager.SetComponentData(bestEntity, t);

                    TrySpawnWell(ref state, hitPoint, profile, serverElapsed, mapW, mapH, in b, ecb, gemPrefab, hitDamage, bestEntity);
                    return true;
                }

                case BulletHitKind.Drone:
                {
                    // Equipment RemainingCharges is the ghosted drone HP (store GetDroneMaxHp).
                    DroneSwarmHitScan.ApplyDamageToDroneSlot(
                        state.EntityManager, bestEntity, s_BestDroneSlot, hitDamage);
                    TrySpawnWell(ref state, hitPoint, profile, serverElapsed, mapW, mapH, in b, ecb, gemPrefab, hitDamage, bestEntity);
                    return true;
                }

                case BulletHitKind.PlanetaryDefense:
                {
                    // [TITAN-ORBIT] Slot HP → 0 resets to empty placeholder (rebuild with gems).
                    PlanetaryDefenseHitScan.ApplyDamage(
                        state.EntityManager, bestEntity, s_BestDefenseSlot, hitDamage, serverElapsed);

                    // Publish post-hit HP on BulletHitRpc — planet ghost MaxSendRate lags the bar.
                    if (state.EntityManager.HasComponent<PlanetState>(bestEntity))
                    {
                        planetaryDefensePlanetId =
                            state.EntityManager.GetComponentData<PlanetState>(bestEntity).PlanetId;
                        planetaryDefenseSlotIndex = (byte)math.clamp(s_BestDefenseSlot, 0, 255);
                        planetaryDefenseHealthAfter = 0f;
                        if (state.EntityManager.HasBuffer<PlanetaryDefenseSlotElement>(bestEntity))
                        {
                            var buf = state.EntityManager.GetBuffer<PlanetaryDefenseSlotElement>(bestEntity);
                            if (s_BestDefenseSlot >= 0 && s_BestDefenseSlot < buf.Length)
                            {
                                var slot = buf[s_BestDefenseSlot];
                                // Destroyed → CreateEmptySlot (TurretLevel 0, Health 0).
                                planetaryDefenseHealthAfter =
                                    slot.TurretLevel > 0 ? math.max(0f, slot.Health) : 0f;
                            }
                        }
                    }

                    TrySpawnWell(ref state, hitPoint, profile, serverElapsed, mapW, mapH, in b, ecb, gemPrefab, hitDamage, bestEntity);
                    return true;
                }

                default:
                    return false;
            }
        }

        void TrySpawnWell(
            ref SystemState state,
            float3 hitPoint,
            BulletBankProfile profile,
            double serverElapsed,
            float mapW,
            float mapH,
            in BulletElement bullet,
            EntityCommandBuffer ecb,
            Entity gemPrefab,
            float hitDamage = 0f,
            Entity skipEntity = default)
        {
            if (profile == null)
                return;

            if (SystemAPI.TryGetSingletonEntity<ActiveBulletsTag>(out var bulletEntity) &&
                state.EntityManager.HasBuffer<GravityWellElement>(bulletEntity))
            {
                var wells = state.EntityManager.GetBuffer<GravityWellElement>(bulletEntity);
                BulletBankHitEffects.TrySpawnGravityWell(
                    wells, hitPoint, profile, serverElapsed, bullet.OwnerNetworkId, bullet.OwnerTeam,
                    bullet.FirePowerExtraLevels, bullet.StrengthScale);
            }

            BulletBankHitEffects.TryApplyConcussiveGemBlast(
                state.EntityManager, hitPoint, profile, mapW, mapH, bullet.FirePowerExtraLevels,
                bullet.StrengthScale);

            // --- Splash damage (center = full hitDamage, edge = 0) ---
            // [TITAN-ORBIT] Skip the primary hit so it is not double-dipped.
            if (hitDamage > 0.01f)
            {
                BulletBankHitEffects.TryApplyConcussiveBlastDamage(
                    state.EntityManager, hitPoint, profile, mapW, mapH,
                    in bullet, hitDamage, skipEntity, serverElapsed,
                    s_DefenseHitTargets, s_DroneHitTargets,
                    ecb, gemPrefab, ShipDamageLogic.ExcessDamageGemExpulsionPerHullDamage);
            }
        }

        /// <summary>Warm family/default defense configs once for hit-sphere rebuilds.</summary>
        static void EnsureDefenseConfigWarmed()
        {
            if (s_DefenseConfigWarmed)
                return;
            s_DefenseFamilyConfig =
                UnityEngine.Resources.Load<PlanetShipFamilyConfig>("PlanetShipFamilyConfig");
            s_DefenseDefaultConfig = PlanetaryDefenseConfig.LoadDefault();
            s_DefenseConfigWarmed = true;
        }

        /// <summary>
        /// Exact ship test for one hashed candidate. Same filters as the old
        /// all-ships foreach (dead / stowed / friendly / self / MEGA parts).
        /// </summary>
        static void ConsiderHashedShip(
            EntityManager em,
            in BulletElement b,
            float3 from,
            float3 to,
            float mapW,
            float mapH,
            bool healFriendly,
            Entity shipEntity,
            ref float bestT,
            ref float3 bestHit,
            ref BulletHitKind bestKind,
            ref Entity bestEntity)
        {
            if (!em.Exists(shipEntity) || !em.HasComponent<ShipState>(shipEntity) ||
                !em.HasComponent<LocalTransform>(shipEntity))
                return;

            var shipState = em.GetComponentData<ShipState>(shipEntity);
            if (shipState.IsDead)
                return;
            if (em.HasComponent<ShipTurretControlState>(shipEntity) &&
                em.GetComponentData<ShipTurretControlState>(shipEntity).IsControlling)
                return;

            bool selfHarm = TitanOrbitDebugFlags.IsHomingSelfHarmArmed(b.Homing, b.Age);
            if (shipState.Team == (TeamId)b.OwnerTeam && !healFriendly && !selfHarm)
                return;
            if (!selfHarm &&
                b.OwnerNetworkId > 0 &&
                em.HasComponent<GhostOwner>(shipEntity) &&
                em.GetComponentData<GhostOwner>(shipEntity).NetworkId == b.OwnerNetworkId)
                return;

            var shipTransform = em.GetComponentData<LocalTransform>(shipEntity);
            float bulletPad = math.clamp(b.ScaleMultiplier * 0.18f, 0f, 0.85f);
            float3 shipHit;
            bool isMega = em.HasComponent<MegaShipState>(shipEntity)
                          && em.GetComponentData<MegaShipState>(shipEntity).IsMega;
            if (isMega)
            {
                if (!MegaShipCombatAim.TryHitBulletSegment(
                        em, shipEntity, shipTransform,
                        from, to, bulletPad, mapW, mapH, out shipHit, out _))
                    return;
            }
            else
            {
                float shipRadius;
                if (em.HasComponent<PhysicsCollider>(shipEntity))
                {
                    var physicsCollider = em.GetComponentData<PhysicsCollider>(shipEntity);
                    shipRadius = MegaShipCombatAim.GetHitRadiusWorld(
                        em, shipEntity, physicsCollider, shipTransform.Scale);
                }
                else
                {
                    shipRadius = BodyCollisionMath.GetShipHullRadiusWorld(shipTransform.Scale);
                }

                shipRadius += bulletPad;
                float3 shipCenter = MegaShipCombatAim.GetAimPoint(em, shipEntity, shipTransform);
                if (!BulletCollision.SegmentHitsSphereToroidal(
                        from, to, shipCenter, shipRadius, mapW, mapH, out shipHit))
                    return;
            }

            if (!TryKeepNearestHit(from, to, shipHit, ref bestT, ref bestHit))
                return;

            bestKind = BulletHitKind.Ship;
            bestEntity = shipEntity;
        }

        /// <summary>Exact asteroid test for one hashed candidate.</summary>
        static void ConsiderHashedAsteroid(
            EntityManager em,
            in BulletElement b,
            float3 from,
            float3 to,
            float mapW,
            float mapH,
            Entity asteroidEntity,
            ref float bestT,
            ref float3 bestHit,
            ref BulletHitKind bestKind,
            ref Entity bestEntity)
        {
            if (!em.Exists(asteroidEntity) || !em.HasComponent<AsteroidState>(asteroidEntity) ||
                !em.HasComponent<LocalTransform>(asteroidEntity))
                return;

            var asteroidState = em.GetComponentData<AsteroidState>(asteroidEntity);
            if (asteroidState.IsDestroyed || asteroidState.Health <= 0f)
                return;

            var asteroidTransform = em.GetComponentData<LocalTransform>(asteroidEntity);
            float hitRadius = BulletCollision.AsteroidHitRadiusForSweep(
                asteroidTransform.Scale, b.ScaleMultiplier);
            if (!BulletCollision.SegmentHitsSphereToroidal(
                    from, to, asteroidTransform.Position, hitRadius, mapW, mapH, out float3 rockHit))
                return;

            if (!TryKeepNearestHit(from, to, rockHit, ref bestT, ref bestHit))
                return;

            bestKind = BulletHitKind.Asteroid;
            bestEntity = asteroidEntity;
        }

        /// <summary>Exact transport test for one hashed candidate.</summary>
        static void ConsiderHashedTransport(
            EntityManager em,
            in BulletElement b,
            float3 from,
            float3 to,
            float mapW,
            float mapH,
            Entity transportEntity,
            ref float bestT,
            ref float3 bestHit,
            ref BulletHitKind bestKind,
            ref Entity bestEntity)
        {
            if (!em.Exists(transportEntity) ||
                !em.HasComponent<PeopleTransportState>(transportEntity) ||
                !em.HasComponent<LocalTransform>(transportEntity))
                return;

            var t = em.GetComponentData<PeopleTransportState>(transportEntity);
            if (t.Amount <= 0f || t.Health <= 0f)
                return;

            var sourceTeam = (TeamId)t.Team;
            var ownerTeam = (TeamId)b.OwnerTeam;
            if (sourceTeam == TeamId.None || sourceTeam == ownerTeam)
                return;

            var transform = em.GetComponentData<LocalTransform>(transportEntity);
            float hitRadius = PeopleTransportMath.GetBulletHitRadius(transform.Scale);
            if (!BulletCollision.SegmentHitsSphereToroidal(
                    from, to, transform.Position, hitRadius, mapW, mapH, out float3 transportHit))
                return;

            if (!TryKeepNearestHit(from, to, transportHit, ref bestT, ref bestHit))
                return;

            bestKind = BulletHitKind.Transport;
            bestEntity = transportEntity;
        }

        /// <summary>
        /// Keeps the contact closest to segment start (parameter t in [0,1]).
        /// Same rule as client <c>BulletCosmeticHitQuery</c> so floats/VFX target the damaged body.
        /// </summary>
        /// <param name="from">Segment start.</param>
        /// <param name="to">Segment end.</param>
        /// <param name="candidateHit">New intersection point to consider.</param>
        /// <param name="bestT">Best t so far (updated when candidate is nearer).</param>
        /// <param name="bestHit">Best hit point so far (updated with candidate).</param>
        /// <returns>True when <paramref name="candidateHit"/> is the new nearest contact.</returns>
        static bool TryKeepNearestHit(
            float3 from,
            float3 to,
            float3 candidateHit,
            ref float bestT,
            ref float3 bestHit)
        {
            float t = BulletCollision.GetSegmentHitParameter(from, to, candidateHit);
            if (t > bestT)
                return false;

            bestT = t;
            bestHit = candidateHit;
            return true;
        }

        /// <summary>
        /// Whether this bullet's <see cref="BulletDamageFilter"/> may collide with / damage
        /// the given hit kind. Planets and gem moons always block (solid world). Mining drones
        /// skip ships; fighters skip asteroids — Starblast-style pass-through. Planetary defense
        /// hits ships, transports, and asteroids (rocks must not be pass-through).
        /// </summary>
        /// <param name="filter">Per-bullet mask from spawn (ship / drone / PD).</param>
        /// <param name="kind">Candidate obstacle class from the swept test.</param>
        /// <returns>True when this bullet should stop on / damage that kind.</returns>
        static bool AllowsHitKind(BulletDamageFilter filter, BulletHitKind kind)
        {
            // --- Planet + moon bodies always block ---
            // [TITAN-ORBIT] Solid world geometry for every filter — even mining bolts stop here.
            // Moon HP still only applies to enemy/neutral moons in Pass 2.
            if (kind == BulletHitKind.Planet || kind == BulletHitKind.Moon)
                return true;

            switch (filter)
            {
                case BulletDamageFilter.Everything:
                    // Ship guns: full collision set (planets/moons already handled above).
                    return true;

                case BulletDamageFilter.AsteroidsOnly:
                    // Mining: rocks only. Pass through ships, drones, transports.
                    return kind == BulletHitKind.Asteroid;

                case BulletDamageFilter.ShipsOnly:
                    // Fighter: enemy ships + their drones + enemy planetary turrets.
                    // Pass through asteroids / transports.
                    return kind == BulletHitKind.Ship ||
                           kind == BulletHitKind.Drone ||
                           kind == BulletHitKind.PlanetaryDefense;

                case BulletDamageFilter.ShipsAndTransports:
                    // Planetary defense: enemy ships + people transports + asteroids.
                    // [TITAN-ORBIT] Asteroids use the same toroidal swept path + Health write as
                    // Everything — previously PD skipped rocks and bolts tunneled through belts.
                    return kind == BulletHitKind.Ship ||
                           kind == BulletHitKind.Transport ||
                           kind == BulletHitKind.Asteroid;

                default:
                    return true;
            }
        }
    }
}
