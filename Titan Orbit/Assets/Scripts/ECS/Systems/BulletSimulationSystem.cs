using TitanOrbit.Core;
using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server-authoritative bullet simulation and ship firing. Runs after
    /// <see cref="PredictedFixedStepSimulationSystemGroup"/> (which contains
    /// <see cref="ShipPhysicsDriveSystem"/>) so muzzle positions use current transforms.
    /// <para>
    /// Multi-cannon fire uses <see cref="ShipWeaponFireLogic"/>: shared energy pool with
    /// per-barrel firePower / fireRate. Full energy + all cooldowns ready → same-tick volley;
    /// otherwise only <see cref="ShipWeaponState.NextMountIndex"/> may spend energy until it
    /// fires, then the next mount in sequence (0→1→2→…→0). Empty mount buffer = unarmed.
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

        /// <summary>Require bullet singleton before ticking.</summary>
        public void OnCreate(ref SystemState state)
        {
            // [ECS/DOTS] RequireForUpdate — system skips OnUpdate until a bullet singleton exists.
            state.RequireForUpdate<ActiveBulletsTag>();

            // [TITAN-ORBIT] Pull Upgrade Visual Scale Multiplier from the single Resources bank
            // so ScaleMultiplier on spawns matches designer Inspector values (client + server).
            var vfxBank = TitanOrbit.Data.BulletVfxBank.LoadDefault();
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

            // [NETCODE] ECB for spawn/hit RPCs — playback after buffer mutations.
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            // --- Shared orbit clock for moon hit tests ---
            // [TITAN-ORBIT] Bullet vs moon must use ServerTick seconds (same as collider / visuals).
            int hz = 0;
            if (SystemAPI.TryGetSingleton<ClientServerTickRate>(out var tickRate))
                hz = tickRate.SimulationTickRate;
            double moonElapsed = SystemAPI.TryGetSingleton<NetworkTime>(out var networkTime)
                ? PlanetGemMoonOrbitClock.GetElapsedSeconds(networkTime, hz, includeTickFraction: false)
                : serverElapsed;

            // [TITAN-ORBIT] Map size for toroidal unwrap during swept collision tests.
            float mapW = 1000f;
            float mapH = 1000f;
            if (SystemAPI.TryGetSingleton<MapStateSingleton>(out var mapState))
            {
                mapW = math.max(100f, mapState.MapWidth);
                mapH = math.max(100f, mapState.MapHeight);
            }

            // --- Phase A: advance existing bullets (substepped sweeps) ---
            for (int i = bullets.Length - 1; i >= 0; i--)
            {
                var b = bullets[i];
                float3 startPos = b.Position;
                float3 endPos = startPos + b.Velocity * dt;
                // [TITAN-ORBIT] Euclidean step on unbounded flight (not a wrapped-torus path sum).
                float stepDistance = math.distance(startPos, endPos);

                // Collide before lifetime/range cull so the final segment still scores hits.
                bool wouldExpire = (b.Age + dt) >= b.Lifetime ||
                                   (b.Traveled + stepDistance) >= b.MaxDistance;

                // --- Substep when |vel|*dt is large vs smallest asteroid ---
                // [TITAN-ORBIT] Starblast continuous feel: split long steps so grazing rocks cannot
                // fall between discrete samples while flying at shipVel + BulletSpeed.
                // Upgraded hulls (higher BulletSpeed + shipVel) need far more than 4 samples.
                int substeps = BulletCollision.ComputeAdvanceSubstepCount(stepDistance);
                float3 cursor = startPos;
                bool hit = false;
                float3 hitPoint = endPos;
                float asteroidHealthAfter = -1f;

                for (int s = 0; s < substeps; s++)
                {
                    float t1 = (s + 1) / (float)substeps;
                    float3 next = math.lerp(startPos, endPos, t1);
                    if (TryResolveBulletHit(
                            ref state, in b, cursor, next, mapW, mapH, moonElapsed, serverElapsed,
                            out hitPoint, out asteroidHealthAfter))
                    {
                        hit = true;
                        break;
                    }

                    cursor = next;
                }

                if (hit)
                {
                    // [NETCODE] Server owns impact timing — clients play VFX from BulletHitRpc.
                    // AsteroidHealthAfter lets clients show true HP Left / hide on kill without
                    // waiting for lagging asteroid ghost snapshots.
                    BulletNetNotify.SendHit(ref ecb, b, hitPoint, asteroidHealthAfter);
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

                // [TITAN-ORBIT] Empty mounts = intentional unarmed — no fire, no default muzzle.
                if (!SystemAPI.HasBuffer<ShipWeaponMountElement>(entity))
                    continue;

                var mounts = SystemAPI.GetBuffer<ShipWeaponMountElement>(entity);
                if (mounts.Length == 0)
                    continue;

                // --- Per-barrel cooldown tick (independent cadences) ---
                ShipWeaponFireLogic.TickMountCooldowns(mounts, dt);

                if (!input.ValueRO.Fire.IsSet)
                    continue;

                // --- Volley vs energy-queue round-robin ---
                if (!ShipWeaponFireLogic.TryPlanFire(
                        shipState.ValueRO.CurrentEnergy,
                        mounts,
                        weaponState.ValueRO.NextMountIndex,
                        weaponCfg.ValueRO.BulletDamage,
                        weaponCfg.ValueRO.FireRate,
                        s_ShotScratch,
                        out int shotCount,
                        out float energySpend,
                        out int nextMountIndexAfter))
                    continue;

                // [TITAN-ORBIT] Family bank from ghosted loadout (ShipStatApplyLogic writes it).
                int bankIndex = 0;
                if (SystemAPI.HasComponent<ShipLoadoutState>(entity))
                    bankIndex = math.max(0, SystemAPI.GetComponentRO<ShipLoadoutState>(entity).ValueRO.RuntimeBulletIndex);

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

                    float refDamage = mount.ReferenceFirePower > 0.01f
                        ? mount.ReferenceFirePower
                        : fallbackRefDamage;
                    float visualScale = BulletVisualScale.ComputePerShotScale(
                        weaponCfg.ValueRO.BulletScale,
                        planned.Damage,
                        weaponCfg.ValueRO.BulletSpeed,
                        refDamage,
                        refSpeed,
                        categoryUpgradeScale);

                    float3 bulletVel = fireForward * math.max(1f, weaponCfg.ValueRO.BulletSpeed) + shipVel;
                    uint sequence = BulletVfxBridge.NextSequence();
                    var spawn = new BulletElement
                    {
                        Position = fireOrigin,
                        Velocity = bulletVel,
                        MaxDistance = math.max(10f, weaponCfg.ValueRO.BulletMaxDistance),
                        Lifetime = math.max(0.1f, weaponCfg.ValueRO.BulletLifetime),
                        Damage = planned.Damage,
                        OwnerNetworkId = ghostOwner.ValueRO.NetworkId,
                        OwnerTeam = (byte)shipState.ValueRO.Team,
                        Sequence = sequence,
                        BankIndex = bankIndex,
                        ScaleMultiplier = visualScale,
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
                    mount.FireCooldown = planned.CooldownSeconds;
                    mounts[mountIdx] = mount;

                    // --- Same-frame spawn collide (first-bullet tunnel fix) ---
                    // [TITAN-ORBIT] Collide the first vel*dt segment immediately so nose-touch shots
                    // do not idle one tick. Do NOT point-test fireOrigin alone — wing muzzles on
                    // 4-gun hulls sit inside side rocks in clusters and that registered false hits
                    // with no aim (player saw forward tracers; side asteroids died).
                    float3 firstEnd = fireOrigin + bulletVel * dt;
                    bool spawnHit = TryResolveBulletHit(
                        ref state, in spawn, fireOrigin, firstEnd, mapW, mapH, moonElapsed, serverElapsed,
                        out float3 spawnHitPoint, out float spawnAsteroidHealthAfter);

                    if (spawnHit)
                    {
                        BulletNetNotify.SendHit(ref ecb, spawn, spawnHitPoint, spawnAsteroidHealthAfter);
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
        /// <returns>True when this segment scored a hit and applied damage (or planet block).</returns>
        bool TryResolveBulletHit(
            ref SystemState state,
            in BulletElement b,
            float3 from,
            float3 to,
            float mapW,
            float mapH,
            double moonElapsed,
            double serverElapsed,
            out float3 hitPoint,
            out float asteroidHealthAfter)
        {
            hitPoint = to;
            asteroidHealthAfter = -1f;

            // --- Pass 1: scan every obstacle, keep nearest contact (smallest segment t) ---
            // [TITAN-ORBIT] Do not apply damage inside the scan — a farther body must not win
            // just because its category or entity index appears earlier in the query.
            float bestT = float.MaxValue;
            float3 bestHit = to;
            var bestKind = BulletHitKind.None;
            Entity bestEntity = Entity.Null;

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

                // Friendly moons do not absorb — bullets pass through to rocks/ships behind.
                var attackerTeam = (TeamId)b.OwnerTeam;
                if (PlanetGemMoonCombatLogic.IsTeamFriendlyToMoon(planetState.ValueRO.Ownership, attackerTeam))
                    continue;

                float hitRadius = PlanetGemMoonMath.GetMoonBulletHitRadiusWorld(
                    planetSize,
                    planetState.ValueRO.IsHomePlanet,
                    moonState.ValueRO.CurrentShield);

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
            foreach (var (shipState, shipTransform, shipEntity) in SystemAPI
                         .Query<RefRO<ShipState>, RefRO<LocalTransform>>()
                         .WithAll<ShipTag>()
                         .WithEntityAccess())
            {
                if (shipState.ValueRO.IsDead)
                    continue;
                if (shipState.ValueRO.Team == (TeamId)b.OwnerTeam)
                    continue;
                if (b.OwnerNetworkId > 0 &&
                    state.EntityManager.HasComponent<GhostOwner>(shipEntity) &&
                    state.EntityManager.GetComponentData<GhostOwner>(shipEntity).NetworkId == b.OwnerNetworkId)
                    continue;

                float shipRadius = BodyCollisionMath.GetShipHullRadiusWorld(shipTransform.ValueRO.Scale);
                if (!BulletCollision.SegmentHitsSphereToroidal(
                        from, to, shipTransform.ValueRO.Position, shipRadius, mapW, mapH, out float3 shipHit))
                    continue;

                if (!TryKeepNearestHit(from, to, shipHit, ref bestT, ref bestHit))
                    continue;

                bestKind = BulletHitKind.Ship;
                bestEntity = shipEntity;
            }

            // --- Asteroids ---
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

            // --- Enemy people transports ---
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

            // --- No intersection ---
            if (bestKind == BulletHitKind.None || bestEntity == Entity.Null)
                return false;

            // --- Pass 2: apply damage (or planet block) only to the nearest winner ---
            hitPoint = bestHit;
            switch (bestKind)
            {
                case BulletHitKind.Planet:
                    // Body absorbs the round — no component write.
                    return true;

                case BulletHitKind.Moon:
                {
                    // Gem-moon shield lives on the planet entity (same as bake / orbit systems).
                    if (!state.EntityManager.HasComponent<PlanetGemMoonState>(bestEntity) ||
                        !state.EntityManager.HasComponent<PlanetState>(bestEntity))
                        return true;

                    var moon = state.EntityManager.GetComponentData<PlanetGemMoonState>(bestEntity);
                    var planet = state.EntityManager.GetComponentData<PlanetState>(bestEntity);
                    PlanetGemMoonCombatLogic.ApplyBulletDamage(
                        ref moon,
                        b.Damage,
                        (TeamId)b.OwnerTeam,
                        planet.Ownership,
                        serverElapsed);
                    state.EntityManager.SetComponentData(bestEntity, moon);
                    return true;
                }

                case BulletHitKind.Ship:
                {
                    var writable = SystemAPI.GetComponentRW<ShipState>(bestEntity);
                    writable.ValueRW.Health -= b.Damage;
                    if (writable.ValueRW.Health <= 0f)
                        writable.ValueRW.IsDead = true;
                    if (state.EntityManager.HasComponent<ShipVitalsState>(bestEntity))
                    {
                        var vitals = state.EntityManager.GetComponentData<ShipVitalsState>(bestEntity);
                        vitals.LastHullDamageTime = SystemAPI.Time.ElapsedTime;
                        state.EntityManager.SetComponentData(bestEntity, vitals);
                    }

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
                        return true;
                    }

                    asteroid.Health -= b.Damage;
                    if (asteroid.Health <= 0f)
                    {
                        asteroid.Health = 0f;
                        asteroid.IsDestroyed = true;
                    }

                    // Publish post-hit HP on BulletHitRpc — ghost snapshots lag MaxSendRate.
                    asteroidHealthAfter = asteroid.Health;
                    state.EntityManager.SetComponentData(bestEntity, asteroid);
                    return true;
                }

                case BulletHitKind.Transport:
                {
                    var t = state.EntityManager.GetComponentData<PeopleTransportState>(bestEntity);
                    t.Health -= b.Damage;
                    if (t.Health <= 0f)
                        PeopleTransportSimulationSystem.DestroyFromBulletDamage(ref state, bestEntity, t);
                    else
                        state.EntityManager.SetComponentData(bestEntity, t);

                    return true;
                }

                default:
                    return false;
            }
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
    }
}
