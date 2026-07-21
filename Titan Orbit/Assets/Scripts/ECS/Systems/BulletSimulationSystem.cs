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
    /// <see cref="ShipPhysicsDriveSystem"/> so muzzle positions use current transforms.
    /// <para>
    /// Multi-cannon fire uses <see cref="ShipWeaponFireLogic"/>:
    /// <b>full volley</b> (every mount same tick) when energy covers all weapons;
    /// otherwise <b>round-robin drip</b> — exactly one mount via
    /// <see cref="ShipWeaponState.NextMountIndex"/> (0→1→2→…→0). Damage/energy split evenly
    /// across mounts. Empty mount buffer = unarmed.
    /// </para>
    /// <para>
    /// Starblast-style hardening vs asteroid tunneling:
    /// (1) same-frame spawn collide (point + first <c>vel*dt</c> segment) so the first shot
    /// does not idle one tick at the muzzle; (2) substep advance when travel is large vs
    /// <see cref="GemEconomyConstants.MinAsteroidHitRadius"/>; (3) collide before lifetime cull.
    /// </para>
    /// Broadcasts <see cref="BulletSpawnRpc"/> / <see cref="BulletHitRpc"/> via
    /// <see cref="BulletNetNotify"/>. Damage is server-only. Not Burst-compiled — managed notify.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(ShipPhysicsDriveSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    public partial struct BulletSimulationSystem : ISystem
    {
        /// <summary>Require bullet singleton before ticking.</summary>
        public void OnCreate(ref SystemState state)
        {
            // [ECS/DOTS] RequireForUpdate — system skips OnUpdate until a bullet singleton exists.
            state.RequireForUpdate<ActiveBulletsTag>();
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
                int substeps = BulletCollision.ComputeAdvanceSubstepCount(stepDistance);
                float3 cursor = startPos;
                bool hit = false;
                float3 hitPoint = endPos;

                for (int s = 0; s < substeps; s++)
                {
                    float t1 = (s + 1) / (float)substeps;
                    float3 next = math.lerp(startPos, endPos, t1);
                    if (TryResolveBulletHit(
                            ref state, in b, cursor, next, mapW, mapH, moonElapsed, serverElapsed,
                            out hitPoint))
                    {
                        hit = true;
                        break;
                    }

                    cursor = next;
                }

                if (hit)
                {
                    // [NETCODE] Server owns impact timing — clients play VFX from BulletHitRpc.
                    BulletNetNotify.SendHit(ref ecb, b, hitPoint);
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
            foreach (var (input, weaponCfg, weaponState, shipState, kinematics, transform, ghostOwner, entity) in SystemAPI
                         .Query<RefRO<ShipInput>, RefRO<ShipWeaponConfig>, RefRW<ShipWeaponState>, RefRW<ShipState>, RefRO<ShipKinematics>, RefRO<LocalTransform>, RefRO<GhostOwner>>()
                         .WithAll<ShipTag>()
                         .WithEntityAccess())
            {
                if (shipState.ValueRO.IsDead)
                    continue;

                float cooldown = weaponState.ValueRO.FireCooldown;
                if (cooldown > 0f)
                {
                    cooldown = math.max(0f, cooldown - dt);
                    weaponState.ValueRW.FireCooldown = cooldown;
                }

                if (!input.ValueRO.Fire.IsSet)
                    continue;

                float fireRate = math.max(0.1f, weaponCfg.ValueRO.FireRate);
                if (cooldown > 0f)
                    continue;

                // [TITAN-ORBIT] Empty mounts = intentional unarmed — no fire, no default muzzle.
                if (!SystemAPI.HasBuffer<ShipWeaponMountElement>(entity))
                    continue;

                var mounts = SystemAPI.GetBuffer<ShipWeaponMountElement>(entity);
                if (mounts.Length == 0)
                    continue;

                // --- Volley vs round-robin drip ---
                // [TITAN-ORBIT] Full energy → all mounts same tick. Otherwise exactly one mount
                // from NextMountIndex, then +1 (never partial multi-fire — that skipped barrels).
                float energyCostTotal = weaponCfg.ValueRO.EnergyCostPerShot > 0f
                    ? weaponCfg.ValueRO.EnergyCostPerShot
                    : weaponCfg.ValueRO.BulletDamage;
                int mountCount = mounts.Length;
                if (!ShipWeaponFireLogic.TryPlanFire(
                        shipState.ValueRO.CurrentEnergy,
                        energyCostTotal,
                        weaponCfg.ValueRO.BulletDamage,
                        fireRate,
                        mountCount,
                        weaponState.ValueRO.NextMountIndex,
                        out var firePlan))
                    continue;

                // [TITAN-ORBIT] Family bank from ghosted loadout (ShipStatApplyLogic writes it).
                int bankIndex = 0;
                if (SystemAPI.HasComponent<ShipLoadoutState>(entity))
                    bankIndex = math.max(0, SystemAPI.GetComponentRO<ShipLoadoutState>(entity).ValueRO.RuntimeBulletIndex);

                float3 shipVel = kinematics.ValueRO.Velocity;
                shipVel.y = 0f;
                float visualScale = BulletVisualScale.ComputePerShotScale(
                    weaponCfg.ValueRO.BulletScale,
                    weaponCfg.ValueRO.BulletDamage,
                    weaponCfg.ValueRO.BulletSpeed,
                    weaponCfg.ValueRO.ReferenceBulletDamage > 0f
                        ? weaponCfg.ValueRO.ReferenceBulletDamage
                        : BulletVisualScale.DefaultReferenceBulletDamage,
                    weaponCfg.ValueRO.ReferenceBulletSpeed > 0f
                        ? weaponCfg.ValueRO.ReferenceBulletSpeed
                        : BulletVisualScale.DefaultReferenceBulletSpeed);

                // --- Spawn planned mounts (all for volley; 1+ from cursor for drip) ---
                for (int shot = 0; shot < firePlan.FireCount; shot++)
                {
                    int mountIdx = ShipWeaponFireLogic.ResolveMountIndex(in firePlan, shot, mountCount);
                    var mount = mounts[mountIdx];
                    float3 fireOrigin;
                    float3 fireForward;
                    if (!ShipWeaponPose.TryResolve(transform.ValueRO, mount, out fireOrigin, out fireForward))
                    {
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
                        // Keep mount world Y (same as ShipWeaponPose.TryResolve).
                        fireOrigin = transform.ValueRO.Position
                            + math.rotate(transform.ValueRO.Rotation, mount.LocalPosition);
                    }

                    float3 bulletVel = fireForward * math.max(1f, weaponCfg.ValueRO.BulletSpeed) + shipVel;
                    uint sequence = BulletVfxBridge.NextSequence();
                    var spawn = new BulletElement
                    {
                        Position = fireOrigin,
                        Velocity = bulletVel,
                        MaxDistance = math.max(10f, weaponCfg.ValueRO.BulletMaxDistance),
                        Lifetime = math.max(0.1f, weaponCfg.ValueRO.BulletLifetime),
                        Damage = firePlan.DamagePerBullet,
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

                    // --- Same-frame spawn collide (first-bullet tunnel fix) ---
                    // [TITAN-ORBIT] Without this, Phase B appends and the bullet idles until next tick —
                    // the first open-fire shot into a nose-touch rock often tunnels. Starblast: collide
                    // as soon as the projectile exists (point + first vel*dt segment).
                    float3 firstEnd = fireOrigin + bulletVel * dt;
                    bool pointHit = TryResolveBulletHit(
                        ref state, in spawn, fireOrigin, fireOrigin, mapW, mapH, moonElapsed, serverElapsed,
                        out float3 spawnHitPoint);
                    bool spawnHit = pointHit;
                    if (!spawnHit)
                    {
                        spawnHit = TryResolveBulletHit(
                            ref state, in spawn, fireOrigin, firstEnd, mapW, mapH, moonElapsed, serverElapsed,
                            out spawnHitPoint);
                    }

                    if (spawnHit)
                    {
                        BulletNetNotify.SendHit(ref ecb, spawn, spawnHitPoint);
                        // Do not add to the live buffer — bullet resolved this frame.
                    }
                    else
                    {
                        bullets.Add(spawn);
                    }
                }

                // Energy spend matches plan (full volley or N drip shares) + fire-rate cooldown.
                shipState.ValueRW.CurrentEnergy = math.max(0f, shipState.ValueRO.CurrentEnergy - firePlan.EnergySpend);
                weaponState.ValueRW.FireCooldown = firePlan.CooldownSeconds;
                weaponState.ValueRW.NextMountIndex = firePlan.NextMountIndexAfter;
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }

        /// <summary>
        /// Swept segment hit test + damage for planets, moons, ships, asteroids, transports.
        /// Shared by advance substeps and same-frame spawn collide.
        /// </summary>
        /// <param name="state">Server system state (vitals write / transport destroy).</param>
        /// <param name="b">Bullet dealing damage (OwnerTeam / Damage).</param>
        /// <param name="from">Segment start (unbounded XZ).</param>
        /// <param name="to">Segment end (unbounded XZ). Equal to <paramref name="from"/> = point test.</param>
        /// <param name="mapW">Toroidal map width.</param>
        /// <param name="mapH">Toroidal map height.</param>
        /// <param name="moonElapsed">ServerTick seconds for gem-moon orbit phase.</param>
        /// <param name="serverElapsed">World elapsed for shield hit timestamps.</param>
        /// <param name="hitPoint">First contact along the segment when true.</param>
        /// <returns>True when this segment scored a hit and applied damage.</returns>
        bool TryResolveBulletHit(
            ref SystemState state,
            in BulletElement b,
            float3 from,
            float3 to,
            float mapW,
            float mapH,
            double moonElapsed,
            double serverElapsed,
            out float3 hitPoint)
        {
            hitPoint = to;

            // --- Planets + gem-moon shields ---
            foreach (var (planetState, planetTransform, moonState) in SystemAPI
                         .Query<RefRO<PlanetState>, RefRO<LocalTransform>, RefRW<PlanetGemMoonState>>()
                         .WithAll<PlanetTag>())
            {
                float planetSize = math.max(0.25f, planetTransform.ValueRO.Scale);
                float3 planetPos = planetTransform.ValueRO.Position;

                if (BulletCollision.SegmentHitsPlanetToroidal(
                        from, to, planetPos, planetSize, mapW, mapH, out hitPoint))
                    return true;

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
                        planetState.ValueRO.IsHomePlanet, hitRadius, mapW, mapH, out hitPoint))
                {
                    PlanetGemMoonCombatLogic.ApplyBulletDamage(
                        ref moonState.ValueRW,
                        b.Damage,
                        attackerTeam,
                        planetState.ValueRO.Ownership,
                        serverElapsed);
                    return true;
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
                        from, to, shipTransform.ValueRO.Position, shipRadius, mapW, mapH, out hitPoint))
                    continue;

                var writable = SystemAPI.GetComponentRW<ShipState>(shipEntity);
                writable.ValueRW.Health -= b.Damage;
                if (writable.ValueRW.Health <= 0f)
                    writable.ValueRW.IsDead = true;
                if (state.EntityManager.HasComponent<ShipVitalsState>(shipEntity))
                {
                    var vitals = state.EntityManager.GetComponentData<ShipVitalsState>(shipEntity);
                    vitals.LastHullDamageTime = SystemAPI.Time.ElapsedTime;
                    state.EntityManager.SetComponentData(shipEntity, vitals);
                }

                return true;
            }

            // --- Asteroids ---
            foreach (var (asteroidState, asteroidTransform) in SystemAPI
                         .Query<RefRW<AsteroidState>, RefRO<LocalTransform>>()
                         .WithAll<AsteroidTag>())
            {
                if (asteroidState.ValueRO.IsDestroyed)
                    continue;

                float hitRadius = BulletCollision.AsteroidHitRadius(asteroidTransform.ValueRO.Scale);
                if (!BulletCollision.SegmentHitsSphereToroidal(
                        from, to, asteroidTransform.ValueRO.Position, hitRadius, mapW, mapH, out hitPoint))
                    continue;

                var asteroid = asteroidState.ValueRO;
                asteroid.Health -= b.Damage;
                if (asteroid.Health <= 0f)
                {
                    asteroid.Health = 0f;
                    asteroid.IsDestroyed = true;
                }

                asteroidState.ValueRW = asteroid;
                return true;
            }

            // --- Enemy people transports ---
            foreach (var (transport, transform, transportEntity) in SystemAPI
                         .Query<RefRW<PeopleTransportState>, RefRO<LocalTransform>>()
                         .WithAll<PeopleTransportTag>()
                         .WithEntityAccess())
            {
                ref var t = ref transport.ValueRW;
                if (t.Amount <= 0f || t.Health <= 0f)
                    continue;

                var sourceTeam = (TeamId)t.Team;
                var ownerTeam = (TeamId)b.OwnerTeam;
                if (sourceTeam == TeamId.None || sourceTeam == ownerTeam)
                    continue;

                float hitRadius = PeopleTransportMath.GetBulletHitRadius(transform.ValueRO.Scale);
                if (!BulletCollision.SegmentHitsSphereToroidal(
                        from, to, transform.ValueRO.Position, hitRadius, mapW, mapH, out hitPoint))
                    continue;

                t.Health -= b.Damage;
                if (t.Health <= 0f)
                    PeopleTransportSimulationSystem.DestroyFromBulletDamage(ref state, transportEntity, t);

                return true;
            }

            return false;
        }
    }
}
