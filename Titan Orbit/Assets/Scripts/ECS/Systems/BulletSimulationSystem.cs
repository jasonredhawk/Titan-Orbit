using System.Collections.Generic;
using TitanOrbit;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.Entities;
using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
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
        EntityQuery _defensePlanetQuery;

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
            _defensePlanetQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<PlanetTag>(),
                ComponentType.ReadOnly<PlanetState>(),
                ComponentType.ReadOnly<LocalTransform>(),
                ComponentType.ReadOnly<PlanetaryDefenseSlotElement>());

            // [TITAN-ORBIT] Pull Upgrade Visual Scale Multiplier from the single Resources bank
            // so ScaleMultiplier on spawns matches designer Inspector values (client + server).
            var vfxBank = BulletVfxBank.LoadDefault();
            if (vfxBank != null)
                BulletVisualScale.ActiveUpgradeVisualScaleMultiplier = vfxBank.UpgradeVisualScaleMultiplier;
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

            // --- Phase A: advance existing bullets (substepped sweeps) ---
            for (int i = bullets.Length - 1; i >= 0; i--)
            {
                var b = bullets[i];

                // --- Homing rockets: steer before the sweep so this tick's segment follows the turn ---
                // [TITAN-ORBIT] Closest enemy ship or turret. Asteroids / moons / planets are
                // never acquired (they can still be hit and take damage on the sweep).
                if (b.Homing != 0 && b.TurnSpeedDeg > 0.01f)
                {
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
                }

                float3 startPos = b.Position;
                float3 endPos = startPos + b.Velocity * dt;
                // [TITAN-ORBIT] Euclidean step on unbounded flight (not a wrapped-torus path sum).
                // MaxDistance is a straight-line budget from spawn. Planetary defense sets it via
                // PlanetaryDefenseAimMath.ComputeBulletMaxDistance — at least engage range, but
                // longer when lead intercept is past the acquisition sphere (crossing/fleeing ships).
                float stepDistance = math.distance(startPos, endPos);

                // Collide before lifetime/range cull so the final segment still scores hits.
                // [TITAN-ORBIT] Lifetime <= 0 means "no timer" (planetary defense) — MaxDistance alone.
                bool lifetimeExpired = b.Lifetime > 0f && (b.Age + dt) >= b.Lifetime;
                bool rangeExpired = (b.Traveled + stepDistance) >= b.MaxDistance;
                bool wouldExpire = lifetimeExpired || rangeExpired;

                // --- Substep when |vel|*dt is large vs smallest asteroid ---
                // [TITAN-ORBIT] Starblast continuous feel: split long steps so grazing rocks cannot
                // fall between discrete samples while flying at shipVel + BulletSpeed.
                // Upgraded hulls (higher BulletSpeed + shipVel) need far more than 4 samples.
                int substeps = BulletCollision.ComputeAdvanceSubstepCount(stepDistance);
                float3 cursor = startPos;
                bool hit = false;
                float3 hitPoint = endPos;
                float asteroidHealthAfter = -1f;
                int pdPlanetId = 0;
                byte pdSlotIndex = 0;
                float pdHealthAfter = -1f;

                for (int s = 0; s < substeps; s++)
                {
                    float t1 = (s + 1) / (float)substeps;
                    float3 next = math.lerp(startPos, endPos, t1);
                    if (TryResolveBulletHit(
                            ref state, ecb, gemPrefab, gemSpawnServerTime,
                            in b, cursor, next, mapW, mapH, moonElapsed, serverElapsed,
                            out hitPoint, out asteroidHealthAfter,
                            out pdPlanetId, out pdSlotIndex, out pdHealthAfter))
                    {
                        hit = true;
                        break;
                    }

                    cursor = next;
                }

                if (hit)
                {
                    // [NETCODE] Server owns impact timing — clients play VFX from BulletHitRpc.
                    // AsteroidHealthAfter / PlanetaryDefenseHealthAfter let clients show true HP
                    // without waiting for lagging ghost snapshots (asteroids + planet MaxSendRate).
                    BulletNetNotify.SendHit(
                        ref ecb, b, hitPoint, asteroidHealthAfter,
                        pdPlanetId, pdSlotIndex, pdHealthAfter);
                    // Hit resolution can DestroyEntity (transports). Re-acquire before mutate.
                    bullets = state.EntityManager.GetBuffer<BulletElement>(bulletEntity);
                    bullets.RemoveAtSwapBack(i);
                    continue;
                }

                // --- No hit this tick: apply age/travel, then expire or keep flying ---
                // [TITAN-ORBIT] Range/lifetime expiry is silent — no BulletHitRpc / impact VFX.
                b.Age += dt;
                b.Traveled += stepDistance;
                if (wouldExpire)
                {
                    bullets.RemoveAtSwapBack(i);
                    continue;
                }

                b.Position = endPos;
                bullets[i] = b;
            }

            // Hit resolution may have destroyed entities — refresh before fire mutates the same buffers.
            bullets = state.EntityManager.GetBuffer<BulletElement>(bulletEntity);
            spawnEvents = state.EntityManager.GetBuffer<BulletSpawnEventElement>(bulletEntity);

            // --- Phase B: ship firing + same-frame spawn collide ---
            // [TITAN-ORBIT] Category Upgrade Visual Scale from Resources bank (once per tick).
            var vfxBankForScale = TitanOrbit.Data.BulletVfxBank.LoadDefault();

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

                if (!input.ValueRO.Fire.IsSet)
                    continue;

                // --- Electric shock: cannot fire while stunned ---
                if (SystemAPI.HasComponent<ShipElectricShockState>(entity) &&
                    SystemAPI.GetComponentRO<ShipElectricShockState>(entity).ValueRO.IsActive(serverElapsed))
                    continue;

                // --- Orbit ring: weapons locked ---
                // [TITAN-ORBIT] InOrbitRing is written by ShipPhysicsDriveLogic (toroidal annulus).
                // Fire input may still be held (player mashing shoot) — ignore it here; thrust
                // remains the only way to leave the passive orbit motor.
                if (SystemAPI.HasComponent<ShipOrbitState>(entity) &&
                    SystemAPI.GetComponentRO<ShipOrbitState>(entity).ValueRO.InOrbitRing)
                    continue;

                // [TITAN-ORBIT] Family bank from ghosted loadout (ShipStatApplyLogic writes it).
                int bankIndex = 0;
                if (SystemAPI.HasComponent<ShipLoadoutState>(entity))
                    bankIndex = BulletBankFireResolve.ResolveFireBankIndex(
                        SystemAPI.GetComponentRO<ShipLoadoutState>(entity).ValueRO);

                int firePowerAbilityLv = 0;
                if (SystemAPI.HasComponent<ShipAttributeUpgradeState>(entity))
                    firePowerAbilityLv = SystemAPI.GetComponentRO<ShipAttributeUpgradeState>(entity).ValueRO.FirePower;
                int firePowerExtras = BulletBankCombatLogic.CountFirePowerExtraLevels(
                    shipState.ValueRO.ShipLevel, firePowerAbilityLv);
                float abilityEnergy = BulletBankCombatLogic.GetAbilityEnergyDrain(bankIndex, firePowerExtras);

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

                // Per-category Upgrade Visual Scale (default 1). Global category scale is applied
                // later in BulletVisualFactory — ScaleMultiplier is fire-power upgrade only.
                float categoryUpgradeScale = vfxBankForScale != null
                    ? vfxBankForScale.GetCategoryUpgradeVisualScaleMultiplier(bankIndex)
                    : 1f;

                float3 shipVel = kinematics.ValueRO.Velocity;
                shipVel.y = 0f;
                float fallbackRefDamage = weaponCfg.ValueRO.ReferenceBulletDamage > 0f
                    ? weaponCfg.ValueRO.ReferenceBulletDamage
                    : BulletVisualScale.DefaultReferenceBulletDamage;
                float refSpeed = weaponCfg.ValueRO.ReferenceBulletSpeed > 0f
                    ? weaponCfg.ValueRO.ReferenceBulletSpeed
                    : BulletVisualScale.DefaultReferenceBulletSpeed;

                // --- Spawn each planned barrel with that mount’s own damage / VFX scale ---
                for (int shot = 0; shot < shotCount; shot++)
                {
                    var planned = s_ShotScratch[shot];
                    int mountIdx = planned.MountIndex;
                    var mount = mounts[mountIdx];
                    float3 fireOrigin;
                    float3 fireForward;
                    if (!ShipWeaponPose.TryResolve(transform.ValueRO, mount, out fireOrigin, out fireForward))
                    {
                        // Fallback mirrors ShipWeaponPose (presentation-scaled local offset).
                        float3 localFwd = math.mul(mount.LocalRotation, new float3(0f, 0f, 1f));
                        localFwd.y = 0f;
                        if (math.lengthsq(localFwd) < 0.0001f)
                            localFwd = new float3(0f, 0f, 1f);
                        else
                            localFwd = math.normalize(localFwd);
                        fireForward = math.rotate(transform.ValueRO.Rotation, localFwd);
                        fireForward.y = 0f;
                        if (math.lengthsq(fireForward) < 0.0001f)
                            fireForward = new float3(0f, 0f, 1f);
                        else
                            fireForward = math.normalize(fireForward);
                        float ecsScale = math.max(0.25f, transform.ValueRO.Scale);
                        float3 presentationLocal = mount.LocalPosition
                            * (BodyCollisionMath.ShipPresentationScale * ecsScale);
                        fireOrigin = transform.ValueRO.Position
                            + math.rotate(transform.ValueRO.Rotation, presentationLocal);
                    }

                    float damage = planned.Damage;
                    float bulletSpeed = weaponCfg.ValueRO.BulletSpeed;
                    float maxDistance = weaponCfg.ValueRO.BulletMaxDistance;
                    float lifetime = weaponCfg.ValueRO.BulletLifetime;
                    float fireRate = weaponCfg.ValueRO.FireRate;
                    BulletBankCombatLogic.ApplyFireModifiers(
                        bankIndex, ref damage, ref bulletSpeed, ref maxDistance, ref lifetime, ref fireRate,
                        firePowerExtras);
                    float fireRateMul = fireRate / math.max(0.1f, weaponCfg.ValueRO.FireRate);

                    float refDamage = mount.ReferenceFirePower > 0.01f
                        ? mount.ReferenceFirePower
                        : fallbackRefDamage;
                    float visualScale = BulletVisualScale.ComputePerShotScale(
                        weaponCfg.ValueRO.BulletScale,
                        damage,
                        bulletSpeed,
                        refDamage,
                        refSpeed,
                        categoryUpgradeScale);

                    float3 bulletVel = fireForward * math.max(1f, bulletSpeed) + shipVel;
                    uint sequence = BulletVfxBridge.NextSequence();
                    var spawn = new BulletElement
                    {
                        Position = fireOrigin,
                        Velocity = bulletVel,
                        MaxDistance = math.max(10f, maxDistance),
                        Lifetime = math.max(0.1f, lifetime),
                        Damage = damage,
                        OwnerNetworkId = ghostOwner.ValueRO.NetworkId,
                        OwnerTeam = (byte)shipState.ValueRO.Team,
                        Sequence = sequence,
                        BankIndex = bankIndex,
                        ScaleMultiplier = visualScale,
                        FirePowerExtraLevels = firePowerExtras,
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
                        BankIndex = bankIndex,
                        ScaleMultiplier = visualScale,
                    });

                    // [NETCODE] Cosmetic path for all clients (host bridge + broadcast RPC).
                    BulletNetNotify.SendSpawn(ref ecb, spawn, mountIdx);

                    // --- Arm this barrel’s own cooldown (independent of other mounts) ---
                    mount.FireCooldown = planned.CooldownSeconds / math.max(0.05f, fireRateMul);
                    mounts[mountIdx] = mount;

                    // --- Same-frame spawn collide (first-bullet tunnel fix) ---
                    // [TITAN-ORBIT] Collide the first vel*dt segment immediately so nose-touch shots
                    // do not idle one tick. Do NOT point-test fireOrigin alone — wing muzzles on
                    // 4-gun hulls sit inside side rocks in clusters and that registered false hits
                    // with no aim (player saw forward tracers; side asteroids died).
                    float3 firstEnd = fireOrigin + bulletVel * dt;
                    bool spawnHit = TryResolveBulletHit(
                        ref state, ecb, gemPrefab, gemSpawnServerTime,
                        in spawn, fireOrigin, firstEnd, mapW, mapH, moonElapsed, serverElapsed,
                        out float3 spawnHitPoint, out float spawnAsteroidHealthAfter,
                        out int spawnPdPlanetId, out byte spawnPdSlotIndex,
                        out float spawnPdHealthAfter);

                    if (spawnHit)
                    {
                        BulletNetNotify.SendHit(
                            ref ecb, spawn, spawnHitPoint, spawnAsteroidHealthAfter,
                            spawnPdPlanetId, spawnPdSlotIndex, spawnPdHealthAfter);
                        bullets = state.EntityManager.GetBuffer<BulletElement>(bulletEntity);
                        spawnEvents = state.EntityManager.GetBuffer<BulletSpawnEventElement>(bulletEntity);
                        // Do not add to the live buffer — bullet resolved this frame.
                    }
                    else
                    {
                        bullets.Add(spawn);
                    }
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
            out float planetaryDefenseHealthAfter)
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

            // --- Enemy ships only (pass through self + friendly team) ---
            // [TITAN-ORBIT] Same-team skip covers allies; OwnerNetworkId covers own hull even if
            // Team is briefly unset during Join Team / respawn (muzzle sits inside own radius).
            // Mining drones skip ships entirely (AsteroidsOnly).
            if (AllowsHitKind(b.DamageFilter, BulletHitKind.Ship))
            {
            foreach (var (shipState, shipTransform, shipEntity) in SystemAPI
                         .Query<RefRO<ShipState>, RefRO<LocalTransform>>()
                         .WithAll<ShipTag>()
                         .WithEntityAccess())
            {
                if (shipState.ValueRO.IsDead)
                    continue;
                // --- Stowed in turret: hull is "removed" — ignore bullet hits ---
                if (state.EntityManager.HasComponent<ShipTurretControlState>(shipEntity) &&
                    state.EntityManager.GetComponentData<ShipTurretControlState>(shipEntity).IsControlling)
                    continue;
                bool selfHarm = b.Homing != 0 && TitanOrbitDebugFlags.IsSelfHarmArmed(b.Age);
                if (shipState.ValueRO.Team == (TeamId)b.OwnerTeam && !healFriendly && !selfHarm)
                    continue;
                if (!selfHarm &&
                    b.OwnerNetworkId > 0 &&
                    state.EntityManager.HasComponent<GhostOwner>(shipEntity) &&
                    state.EntityManager.GetComponentData<GhostOwner>(shipEntity).NetworkId == b.OwnerNetworkId)
                    continue;

                // --- Hit radius from attribute-grown PhysicsCollider (XZ AABB) ---
                // [TITAN-ORBIT] Component upgrades scale compound hull children via
                // ShipHullColliderLogic — GetShipHullRadiusWorld only sees tier LocalTransform.Scale
                // and under-hits fat ships. Same helper as ShipToroidalWorldCollisionLogic.
                float shipRadius;
                if (state.EntityManager.HasComponent<PhysicsCollider>(shipEntity))
                {
                    var physicsCollider = state.EntityManager.GetComponentData<PhysicsCollider>(shipEntity);
                    shipRadius = ShipToroidalWorldCollisionLogic.GetShipCollisionRadiusWorld(
                        physicsCollider, shipTransform.ValueRO.Scale);
                }
                else
                {
                    shipRadius = BodyCollisionMath.GetShipHullRadiusWorld(shipTransform.ValueRO.Scale);
                }

                // [TITAN-ORBIT] Heavier fire-power tracers (ScaleMultiplier) get a matching
                // collision pad so big planetary-defense / upgraded shots do not skim past hulls.
                float bulletPad = math.clamp(b.ScaleMultiplier * 0.18f, 0f, 0.85f);
                shipRadius += bulletPad;
                if (!BulletCollision.SegmentHitsSphereToroidal(
                        from, to, shipTransform.ValueRO.Position, shipRadius, mapW, mapH, out float3 shipHit))
                    continue;

                if (!TryKeepNearestHit(from, to, shipHit, ref bestT, ref bestHit))
                    continue;

                bestKind = BulletHitKind.Ship;
                bestEntity = shipEntity;
            }
            }

            // --- Asteroids ---
            // [TITAN-ORBIT] Fighter drones skip rocks (ShipsOnly) — bolts pass through asteroids.
            if (AllowsHitKind(b.DamageFilter, BulletHitKind.Asteroid))
            {
            foreach (var (asteroidState, asteroidTransform, asteroidEntity) in SystemAPI
                         .Query<RefRO<AsteroidState>, RefRO<LocalTransform>>()
                         .WithAll<AsteroidTag>()
                         .WithEntityAccess())
            {
                // Already-dead rocks do not block or absorb further shots this tick.
                if (asteroidState.ValueRO.IsDestroyed || asteroidState.ValueRO.Health <= 0f)
                    continue;

                float hitRadius = BulletCollision.AsteroidHitRadius(asteroidTransform.ValueRO.Scale);
                if (!BulletCollision.SegmentHitsSphereToroidal(
                        from, to, asteroidTransform.ValueRO.Position, hitRadius, mapW, mapH, out float3 rockHit))
                    continue;

                if (!TryKeepNearestHit(from, to, rockHit, ref bestT, ref bestHit))
                    continue;

                bestKind = BulletHitKind.Asteroid;
                bestEntity = asteroidEntity;
            }
            }

            // --- Enemy people transports ---
            if (AllowsHitKind(b.DamageFilter, BulletHitKind.Transport))
            {
            foreach (var (transport, transform, transportEntity) in SystemAPI
                         .Query<RefRO<PeopleTransportState>, RefRO<LocalTransform>>()
                         .WithAll<PeopleTransportTag>()
                         .WithEntityAccess())
            {
                var t = transport.ValueRO;
                if (t.Amount <= 0f || t.Health <= 0f)
                    continue;

                var sourceTeam = (TeamId)t.Team;
                var ownerTeam = (TeamId)b.OwnerTeam;
                if (sourceTeam == TeamId.None || sourceTeam == ownerTeam)
                    continue;

                float hitRadius = PeopleTransportMath.GetBulletHitRadius(transform.ValueRO.Scale);
                if (!BulletCollision.SegmentHitsSphereToroidal(
                        from, to, transform.ValueRO.Position, hitRadius, mapW, mapH, out float3 transportHit))
                    continue;

                if (!TryKeepNearestHit(from, to, transportHit, ref bestT, ref bestHit))
                    continue;

                bestKind = BulletHitKind.Transport;
                bestEntity = transportEntity;
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
                    bool selfHarmHit = b.Homing != 0 && TitanOrbitDebugFlags.IsSelfHarmArmed(b.Age);
                    var damageTeam = selfHarmHit && ship.Team == (TeamId)b.OwnerTeam
                        ? TeamId.None
                        : (TeamId)b.OwnerTeam;
                    var result = ShipDamageLogic.ApplyHullAndGemDamage(
                        ref health,
                        ref gems,
                        ref isDead,
                        hitDamage,
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
                    if ((result.AppliedHullDamage || result.GemsToExpel > 0.0001f || result.BecameDead) &&
                        b.OwnerNetworkId > 0)
                    {
                        ShipMatchStatsLogic.SetLastDamager(
                            state.EntityManager,
                            bestEntity,
                            b.OwnerNetworkId,
                            (float)serverElapsed);
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
