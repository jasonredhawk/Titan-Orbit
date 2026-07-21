using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.Diagnostics;
using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using Random = Unity.Mathematics.Random;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Tunable constants for gem mining, pickup, deposit, and asteroid hit radii.
    /// Shared by <see cref="MiningSystem"/>, <see cref="GemPickupSystem"/>, and bullet collision.
    /// </summary>
    public static class GemEconomyConstants
    {
        /// <summary>World units — ship must be within this toroidal distance to mine an asteroid.</summary>
        public const float MiningRange = 6f;

        /// <summary>Gem value mined per second while in range.</summary>
        public const float MiningRate = 5f;

        /// <summary>Hull-center pickup radius when ship has no wing tractor buffers.</summary>
        public const float GemPickupRange = 2.5f;

        /// <summary>Collect gems at the wing when tractor-pulled (legacy Gem.collectRadius ~0.6).</summary>
        public const float GemWingCollectRadius = 0.65f;

        /// <summary>Legacy planet interaction radius (deposit uses moon dock instead).</summary>
        public const float PlanetInteractionRange = 20f;

        /// <summary>Multiplier on moon dock zone relative to moon visual size.</summary>
        public const float MoonDockRangeMultiplier = 2.2f;

        /// <summary>Landing progress threshold — 1.0 means fully docked on the gem moon.</summary>
        public const float MoonLandingCompleteThreshold = 0.999f;

        /// <summary>Stillness time required before moon landing progress begins or resumes.</summary>
        public const float MoonLandingApproachDelaySeconds = 0.5f;

        /// <summary>Gems deposited per second scales with ship level × this factor.</summary>
        public const float DepositRatePerShipLevel = 2f;

        /// <summary>Smallest gem chunk worth spawning as an entity.</summary>
        public const float MinGemSpawnValue = 0.25f;

        /// <summary>
        /// Fallback explosion speed when <see cref="GemExplosionSettings"/> is missing.
        /// Prefer the ScriptableObject (Assets/Data/GemExplosionSettings.asset).
        /// </summary>
        public const float AsteroidExplosionSpeed = GemExplosionMath.DefaultExplosionSpeed;

        /// <summary>Fallback spawn offset radius — prefer GemExplosionSettings.</summary>
        public const float AsteroidExplosionRadius = GemExplosionMath.DefaultExplosionRadius;

        /// <summary>
        /// Fallback linear damping — prefer GemExplosionSettings.LinearDamping (original 0.5).
        /// </summary>
        public const float GemDragPerSecond = GemExplosionMath.DefaultLinearDamping;

        /// <summary>SgtPlanet base radius on <c>Asteroid.prefab</c>.</summary>
        public const float AsteroidMeshBaseRadius = 0.5f;

        /// <summary>Padding over mesh radius for displacement and slight aim forgiveness.</summary>
        public const float AsteroidHitRadiusScale = 1.1f;

        /// <summary>Floor for bullet segment tests against small asteroids.</summary>
        public const float MinAsteroidHitRadius = 0.15f;
    }

    /// <summary>
    /// Server: ships near asteroids mine gems over time, spawning gem entities when chunks break off.
    /// Destroys asteroids when RemainingGems reaches zero.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct MiningSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GamePrefabs>();
        }

        public void OnUpdate(ref SystemState state)
        {
            if (!SystemAPI.TryGetSingleton<GamePrefabs>(out var prefabs) || prefabs.Gem == Entity.Null)
                return;

            float dt = SystemAPI.Time.DeltaTime;
            // [NETCODE] ServerTick seconds — matches client shrink / despawn (not World.Time).
            float spawnServerTime = PlanetGemMoonOrbitClock.GetElapsedSecondsOrFallback(
                state.EntityManager, SystemAPI.Time.ElapsedTime);
            float mapW = ToroidalMapEcs.MapWidth;
            float mapH = ToroidalMapEcs.MapHeight;
            if (SystemAPI.TryGetSingleton<MapStateSingleton>(out var mapState))
            {
                mapW = mapState.MapWidth;
                mapH = mapState.MapHeight;
            }

            var ecb = new EntityCommandBuffer(Allocator.Temp);

            // --- Each ship mines every asteroid in range this tick ---
            foreach (var (shipTransform, shipState, shipEntity) in SystemAPI
                         .Query<RefRO<LocalTransform>, RefRO<ShipState>>()
                         .WithAll<ShipTag>()
                         .WithEntityAccess())
            {
                if (shipState.ValueRO.IsDead || shipState.ValueRO.AwaitingTeamSelection)
                    continue;

                foreach (var (asteroidState, asteroidTransform, asteroidEntity) in SystemAPI
                             .Query<RefRW<AsteroidState>, RefRO<LocalTransform>>()
                             .WithAll<AsteroidTag>()
                             .WithEntityAccess())
                {
                    if (asteroidState.ValueRO.IsDestroyed)
                        continue;

                    if (ToroidalMapEcs.ToroidalDistance(
                            shipTransform.ValueRO.Position,
                            asteroidTransform.ValueRO.Position,
                            mapW,
                            mapH) > GemEconomyConstants.MiningRange)
                        continue;

                    var a = asteroidState.ValueRO;
                    float mined = GemEconomyConstants.MiningRate * dt;
                    mined = math.min(mined, a.RemainingGems);
                    if (mined < GemEconomyConstants.MinGemSpawnValue)
                        continue;

                    a.RemainingGems -= mined;
                    if (a.RemainingGems <= 0f)
                    {
                        a.RemainingGems = 0f;
                        a.IsDestroyed = true;
                    }

                    asteroidState.ValueRW = a;
                    GemSpawning.Spawn(
                        ecb,
                        prefabs.Gem,
                        asteroidTransform.ValueRO.Position,
                        mined,
                        (uint)asteroidEntity.Index,
                        burst: false,
                        spawnServerTime);
                }
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }

    /// <summary>
    /// Server: applies original-style linear/angular damping and integrates gem pose from
    /// <see cref="GemKinematics"/>. Gems are scripted movers — not Unity Physics bodies.
    /// Tunables: <see cref="GemExplosionSettings"/> (Editor).
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(MiningSystem))]
    public partial struct GemMotionSystem : ISystem
    {
        /// <summary>Integrates velocity + tumble with PhysX-like damping (unbounded XZ).</summary>
        public void OnUpdate(ref SystemState state)
        {
            float dt = SystemAPI.Time.DeltaTime;
            var settings = GemExplosionSettingsCache.ResolveOrDefault();
            float linearDamping = settings.LinearDamping;
            float angularDamping = settings.AngularDamping;
            float stopSpeed = settings.StopSpeedThreshold;

            foreach (var (kinematics, transform) in SystemAPI
                         .Query<RefRW<GemKinematics>, RefRW<LocalTransform>>()
                         .WithAll<GemTag>())
            {
                var kin = kinematics.ValueRO;
                float3 vel = GemExplosionMath.IntegrateLinearVelocity(
                    kin.Velocity, linearDamping, stopSpeed, dt);
                float3 ang = GemExplosionMath.IntegrateAngularVelocity(
                    kin.AngularVelocity, angularDamping, dt);

                // --- Integrate in unbounded space (same as ships); toroidal math is for reach only ---
                var lt = transform.ValueRO;
                lt.Position += vel * dt;
                if (math.lengthsq(ang) > 0.0001f)
                {
                    // AngularVelocity is rad/s — quaternion integrate in world space.
                    float angle = math.length(ang) * dt;
                    float3 axis = math.normalizesafe(ang, new float3(0f, 1f, 0f));
                    lt.Rotation = math.mul(quaternion.AxisAngle(axis, angle), lt.Rotation);
                }

                transform.ValueRW = lt;
                kinematics.ValueRW = new GemKinematics { Velocity = vel, AngularVelocity = ang };
            }
        }
    }

    /// <summary>
    /// Server: despawns loose gems after their lifetime (original Gem.lifetimeSeconds = 20).
    /// Runs after motion so expired gems are removed before pickup that same tick is irrelevant.
    /// Shrink is presentation-only on clients via <see cref="GemState.SpawnServerTime"/>.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(GemMotionSystem))]
    [UpdateAfter(typeof(AsteroidDestructionSystem))]
    public partial struct GemLifetimeDespawnSystem : ISystem
    {
        /// <summary>Destroys gems whose elapsed life exceeds <see cref="GemExplosionSettings.GemLifetimeSeconds"/>.</summary>
        public void OnUpdate(ref SystemState state)
        {
            var settings = GemExplosionSettingsCache.ResolveOrDefault();
            settings.ClampCounts();
            float lifetime = settings.GemLifetimeSeconds;
            // [NETCODE] Same ServerTick timeline stamped at spawn (late-join safe).
            float now = PlanetGemMoonOrbitClock.GetElapsedSecondsOrFallback(
                state.EntityManager, SystemAPI.Time.ElapsedTime);

            var ecb = new EntityCommandBuffer(Allocator.Temp);

            // --- Expire uncollected gems ---
            // [TITAN-ORBIT] Matches NGO Gem.FixedUpdate: elapsed >= lifetimeSeconds → Despawn.
            foreach (var (gemState, gemEntity) in SystemAPI
                         .Query<RefRO<GemState>>()
                         .WithAll<GemTag>()
                         .WithEntityAccess())
            {
                float spawnTime = gemState.ValueRO.SpawnServerTime;
                // Baked prefab default is 0 until spawn overwrites — skip unset stamps.
                if (spawnTime <= 0f)
                    continue;

                if (now - spawnTime < lifetime)
                    continue;

                ecb.DestroyEntity(gemEntity);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }

    /// <summary>
    /// Server: collects gems into ship cargo when within hull or wing tractor pickup radius.
    /// Runs after <see cref="GemTractorBeamSystem"/> so pulled gems can be collected at wings.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(AsteroidDestructionSystem))]
    [UpdateAfter(typeof(GemLifetimeDespawnSystem))]
    public partial struct GemPickupSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            var ecb = new EntityCommandBuffer(Allocator.Temp);
            float mapW = ToroidalMapEcs.MapWidth;
            float mapH = ToroidalMapEcs.MapHeight;

            foreach (var (shipTransform, shipState, shipEntity) in SystemAPI
                         .Query<RefRO<LocalTransform>, RefRW<ShipState>>()
                         .WithAll<ShipTag>()
                         .WithEntityAccess())
            {
                if (shipState.ValueRO.IsDead || shipState.ValueRO.AwaitingTeamSelection)
                    continue;

                float capacityLeft = shipState.ValueRO.GemCapacity - shipState.ValueRO.CurrentGems;
                if (capacityLeft <= 0.001f)
                    continue;

                bool hasWings = state.EntityManager.HasBuffer<ShipWingTractorBeamElement>(shipEntity) &&
                                state.EntityManager.GetBuffer<ShipWingTractorBeamElement>(shipEntity).Length > 0;

                foreach (var (gemState, gemTransform, gemEntity) in SystemAPI
                             .Query<RefRO<GemState>, RefRO<LocalTransform>>()
                             .WithAll<GemTag>()
                             .WithEntityAccess())
                {
                    if (!IsWithinPickupRange(
                            state.EntityManager,
                            shipEntity,
                            shipTransform.ValueRO,
                            gemTransform.ValueRO,
                            gemState.ValueRO,
                            hasWings,
                            mapW,
                            mapH))
                        continue;

                    float take = math.min(gemState.ValueRO.Value, capacityLeft);
                    if (take <= 0.001f)
                        continue;

                    var ship = shipState.ValueRO;
                    ship.CurrentGems += take;
                    shipState.ValueRW = ship;
                    capacityLeft -= take;

                    float remainder = gemState.ValueRO.Value - take;
                    if (remainder > 0.001f)
                    {
                        var gem = gemState.ValueRO;
                        gem.Value = remainder;
                        float scale = math.clamp(math.sqrt(remainder) * 0.2f, 0.2f, 0.5f);
                        gem.Size = scale;
                        ecb.SetComponent(gemEntity, gem);
                        ecb.SetComponent(gemEntity, LocalTransform.FromPositionRotationScale(
                            gemTransform.ValueRO.Position,
                            gemTransform.ValueRO.Rotation,
                            scale));
                    }
                    else
                    {
                        ecb.DestroyEntity(gemEntity);
                    }

                    if (capacityLeft <= 0.001f)
                        break;
                }
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }

        static bool IsWithinPickupRange(
            EntityManager em,
            Entity shipEntity,
            in LocalTransform shipTransform,
            in LocalTransform gemTransform,
            in GemState gemState,
            bool hasWings,
            float mapW,
            float mapH)
        {
            float3 gemPos = gemTransform.Position;

            if (hasWings)
            {
                var wings = em.GetBuffer<ShipWingTractorBeamElement>(shipEntity);
                float collectRadius = GemEconomyConstants.GemWingCollectRadius + gemState.Size * 0.25f;
                for (int wi = 0; wi < wings.Length; wi++)
                {
                    float3 wingPos = ShipWingTractorBeamPose.GetWorldPosition(shipTransform, wings[wi]);
                    if (GemTractorBeamMath.ToroidalDistance(gemPos, wingPos, mapW, mapH) <= collectRadius)
                        return true;
                }

                return false;
            }

            return GemTractorBeamMath.ToroidalDistance(gemPos, shipTransform.Position, mapW, mapH) <=
                   GemEconomyConstants.GemPickupRange;
        }
    }

    /// <summary>
    /// Server: deposits ship cargo gems into friendly planets while docked at the gem moon.
    /// Planet treasury levels up via <see cref="PlanetEconomyMath"/>; the player's spendable
    /// Bank (contributed gems) is always credited on the team's <b>home</b> planet ledger so
    /// orbit-store purchases work after depositing at any friendly moon.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(GemPickupSystem))]
    public partial struct GemDepositSystem : ISystem
    {
        /// <summary>
        /// Fixed-step deposit pass: for each docked ship with deposit intent and cargo gems,
        /// transfer into the docked planet and credit the home contributed-gems Bank.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // --- Fixed timestep ---
            float dt = SystemAPI.Time.DeltaTime;

            foreach (var (shipState, shipInput, moonDock, shipEntity) in SystemAPI
                         .Query<RefRW<ShipState>, RefRO<ShipInput>, RefRO<ShipMoonDockState>>()
                         .WithAll<ShipTag>()
                         .WithEntityAccess())
            {
                // --- Skip ships that cannot deposit ---
                if (shipState.ValueRO.IsDead || shipState.ValueRO.AwaitingTeamSelection)
                    continue;
                if (shipState.ValueRO.Team == TeamId.None || shipState.ValueRO.CurrentGems <= 0f)
                    continue;

                // --- Deposit intent: prefer ShipDepositIntent (survives prediction rollback) ---
                bool wantDeposit = shipInput.ValueRO.WantDepositGems;
                if (state.EntityManager.HasComponent<ShipDepositIntent>(shipEntity))
                    wantDeposit = state.EntityManager.GetComponentData<ShipDepositIntent>(shipEntity).WantDepositGems;

                // --- GhostOwner.NetworkId keys the contributed-gems Bank row ---
                int ownerNetworkId = 0;
                if (state.EntityManager.HasComponent<GhostOwner>(shipEntity))
                    ownerNetworkId = state.EntityManager.GetComponentData<GhostOwner>(shipEntity).NetworkId;

                foreach (var (planetState, _, planetEntity) in SystemAPI
                             .Query<RefRW<PlanetState>, RefRO<LocalTransform>>()
                             .WithAll<PlanetTag>()
                             .WithEntityAccess())
                {
                    if (planetState.ValueRO.Ownership != shipState.ValueRO.Team)
                        continue;

                    if (!CanDepositAtPlanet(
                            shipInput.ValueRO,
                            wantDeposit,
                            moonDock.ValueRO,
                            planetState.ValueRO))
                        continue;

                    // --- Rate-limited transfer this tick ---
                    float gemValue = math.max(1f, shipState.ValueRO.ShipLevel);
                    float rate = gemValue * GemEconomyConstants.DepositRatePerShipLevel * dt;
                    float amount = math.min(rate, shipState.ValueRO.CurrentGems);
                    if (amount <= 0.001f)
                        continue;

                    // --- Ship cargo ↓ ---
                    var ship = shipState.ValueRO;
                    ship.CurrentGems -= amount;
                    shipState.ValueRW = ship;

                    // --- Planet treasury ↑ (level-up math) ---
                    var planet = planetState.ValueRO;
                    int level = planet.PlanetLevel;
                    float gems = planet.CurrentGems;
                    PlanetEconomyMath.DepositGems(ref level, ref gems, amount);
                    planet.PlanetLevel = level;
                    planet.CurrentGems = gems;
                    planetState.ValueRW = planet;

                    // --- Personal Bank ↑ on the team's HOME ledger (store spend currency) ---
                    // [TITAN-ORBIT] Do not gate on planet.IsHomePlanet alone — captured moons must
                    // still credit Bank. MoonOrbitStoreSystem always spends from the home buffer.
                    if (ownerNetworkId > 0 &&
                        TryFindHomePlanetEntity(
                            state.EntityManager,
                            shipState.ValueRO.Team,
                            planet.IsHomePlanet ? planetEntity : Entity.Null,
                            out Entity homeEntity))
                    {
                        ContributedGemsLogic.Add(state.EntityManager, homeEntity, ownerNetworkId, amount);
                    }
                }
            }
        }

        /// <summary>
        /// True when the ship is fully landed on this planet's moon, wants to deposit, and is not thrusting.
        /// </summary>
        static bool CanDepositAtPlanet(
            in ShipInput input,
            bool wantDepositGems,
            in ShipMoonDockState moonDock,
            in PlanetState planet)
        {
            if (input.Thrust || !wantDepositGems)
                return false;

            if (moonDock.MoonPlanetId != planet.PlanetId || moonDock.MoonPlanetId == 0)
                return false;

            return moonDock.LandingProgress >= GemEconomyConstants.MoonLandingCompleteThreshold;
        }

        /// <summary>
        /// Resolves the team's home planet entity for contributed-gems credit.
        /// When <paramref name="dockedIfHome"/> is already the home entity, uses it; otherwise
        /// searches <see cref="HomePlanetTag"/> on the server world.
        /// </summary>
        static bool TryFindHomePlanetEntity(
            EntityManager em,
            TeamId team,
            Entity dockedIfHome,
            out Entity homeEntity)
        {
            homeEntity = Entity.Null;
            if (team == TeamId.None)
                return false;

            // Fast path: deposit moon is already the home capital.
            if (dockedIfHome != Entity.Null)
            {
                homeEntity = dockedIfHome;
                return true;
            }

            // Server-only tag — safe here (this system is ServerSimulation).
            using var query = em.CreateEntityQuery(typeof(HomePlanetTag), typeof(PlanetState));
            using var states = query.ToComponentDataArray<PlanetState>(Allocator.Temp);
            using var entities = query.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < states.Length; i++)
            {
                if (states[i].Ownership != team)
                    continue;
                homeEntity = entities[i];
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Server: when an asteroid is destroyed, spawns N gem entities (Editor min–max on
    /// <see cref="GemExplosionSettings"/>) that sum to leftover <see cref="AsteroidState.RemainingGems"/>,
    /// with original NGO explosion speed, damping, and tumble. Schedules a timed respawn
    /// (<see cref="AsteroidSpawning.ScheduleRespawn"/>) then destroys the entity.
    /// Clients also play an immediate local burst via <c>ClientGemBurstPresenter</c>.
    /// <para>
    /// [TITAN-ORBIT] Despawn triggers on <see cref="AsteroidState.IsDestroyed"/> <b>or</b>
    /// <c>Health &lt;= 0</c> (belt-and-suspenders for bullet kills). Missing Gem prefab must not
    /// leave 0-HP zombies — we still destroy the entity and only skip the gem burst.
    /// </para>
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(BulletSimulationSystem))]
    [UpdateAfter(typeof(MiningSystem))]
    public partial struct AsteroidDestructionSystem : ISystem
    {
        /// <summary>Requires gem prefab and ensures the respawn queue singleton exists.</summary>
        public void OnCreate(ref SystemState state)
        {
            AsteroidSpawning.EnsureRespawnQueue(state.EntityManager);
            state.RequireForUpdate<GamePrefabs>();
            state.RequireForUpdate<AsteroidRespawnQueueTag>();
        }

        /// <summary>
        /// For each destroyed asteroid: burst leftover gems, enqueue respawn, destroy entity.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // [TITAN-ORBIT] Gem prefab is optional for despawn — never early-out the whole system
            // when Gem is null (that left Health=0 / IsDestroyed rocks alive forever).
            if (!SystemAPI.TryGetSingleton<GamePrefabs>(out var prefabs))
                return;

            bool canSpawnGems = prefabs.Gem != Entity.Null;
            var settings = GemExplosionSettingsCache.ResolveOrDefault();
            settings.ClampCounts();
            float spawnTime = PlanetGemMoonOrbitClock.GetElapsedSecondsOrFallback(
                state.EntityManager, SystemAPI.Time.ElapsedTime);
            // Respawn queue is server-only — World.Time is fine (not replicated to clients).
            double now = SystemAPI.Time.ElapsedTime;
            var respawnBuffer = SystemAPI.GetSingletonBuffer<PendingAsteroidRespawnElement>();

            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (asteroidState, asteroidTransform, entity) in SystemAPI
                         .Query<RefRO<AsteroidState>, RefRO<LocalTransform>>()
                         .WithAll<AsteroidTag>()
                         .WithEntityAccess())
            {
                var a = asteroidState.ValueRO;
                // Bullet path sets IsDestroyed with Health=0; also accept Health<=0 alone.
                bool shouldDestroy = a.IsDestroyed || a.Health <= 0f;
                if (!shouldDestroy)
                    continue;

                float3 pos = asteroidTransform.ValueRO.Position;
                float remaining = a.RemainingGems;
                if (canSpawnGems && remaining >= GemEconomyConstants.MinGemSpawnValue)
                {
                    // Deterministic seed so client immediate burst can match count/feel closely.
                    uint seed = math.hash(new uint2((uint)entity.Index, math.hash(pos)));
                    SpawnAsteroidDestructionGems(
                        ecb, prefabs.Gem, pos, remaining, seed, settings, spawnTime);
                }

                // --- Schedule respawn (original AsteroidRespawnManager.ScheduleRespawn) ---
                // Prefer MaxGems. If unset: mining leaves Health full / RemainingGems 0; bullet
                // kills leave Health 0 / RemainingGems full — max() covers both.
                float restoreGems = a.MaxGems;
                if (restoreGems < GemEconomyConstants.MinGemSpawnValue)
                    restoreGems = math.max(a.Health, remaining);
                if (restoreGems < GemEconomyConstants.MinGemSpawnValue)
                    restoreGems = 1f;

                AsteroidSpawning.ScheduleRespawn(
                    respawnBuffer,
                    pos,
                    asteroidTransform.ValueRO.Scale,
                    restoreGems,
                    now,
                    settings.AsteroidRespawnDelaySeconds);

                // #region agent log
                AgentDebugSessionLog.Write(
                    "B",
                    "AsteroidDestructionSystem.OnUpdate",
                    "server_asteroid_destroy_entity",
                    "{\"entityIndex\":" + entity.Index +
                    ",\"isDestroyed\":" + (a.IsDestroyed ? "true" : "false") +
                    ",\"health\":" + a.Health.ToString("R") +
                    ",\"remainingGems\":" + remaining.ToString("R") +
                    ",\"maxGems\":" + a.MaxGems.ToString("R") +
                    ",\"canSpawnGems\":" + (canSpawnGems ? "true" : "false") +
                    ",\"restoreGems\":" + restoreGems.ToString("R") +
                    ",\"posX\":" + pos.x.ToString("R") +
                    ",\"posZ\":" + pos.z.ToString("R") + "}");
                // #endregion

                ecb.DestroyEntity(entity);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }

        /// <summary>
        /// Spawns min–max gems (clamped by remaining value) whose values sum to <paramref name="remaining"/>.
        /// </summary>
        static void SpawnAsteroidDestructionGems(
            EntityCommandBuffer ecb,
            Entity gemPrefab,
            float3 pos,
            float remaining,
            uint seed,
            GemExplosionSettings settings,
            float spawnServerTime)
        {
            var rng = Random.CreateFromIndex(seed);
            int count = GemExplosionMath.ResolveGemCount(
                remaining, settings.MinGemCount, settings.MaxGemCount, ref rng);

            for (int i = 0; i < count; i++)
            {
                float value = GemExplosionMath.ValuePerGem(remaining, count, i);
                if (value < GemEconomyConstants.MinGemSpawnValue)
                    continue;
                GemSpawning.Spawn(
                    ecb,
                    gemPrefab,
                    pos,
                    value,
                    seed + (uint)(i + 1) * 97u,
                    burst: true,
                    spawnServerTime,
                    settings);
            }
        }
    }

    /// <summary>Shared gem entity spawn helper for mining and asteroid destruction bursts.</summary>
    public static class GemSpawning
    {
        /// <summary>
        /// Instantiates a gem prefab with value, optional burst velocity/tumble, offset, and lifetime stamp.
        /// </summary>
        /// <param name="spawnServerTime">ServerTick seconds — drives lifetime despawn and client shrink.</param>
        public static void Spawn(
            EntityCommandBuffer ecb,
            Entity gemPrefab,
            float3 position,
            float value,
            uint salt,
            bool burst,
            float spawnServerTime,
            GemExplosionSettings settings = null)
        {
            if (value <= 0f)
                return;

            settings ??= GemExplosionSettingsCache.ResolveOrDefault();
            var rng = Random.CreateFromIndex(math.hash(position) + salt + 17u);
            float3 spawnDir = GemExplosionMath.RandomUnitXZ(ref rng);

            float radius = burst ? settings.AsteroidExplosionRadius : 0.8f;
            float3 offset = spawnDir * radius * rng.NextFloat(0.3f, 1f);
            float scale = math.clamp(math.sqrt(value) * 0.2f, 0.2f, 0.5f);

            Entity gem = ecb.Instantiate(gemPrefab);
            ecb.SetComponent(gem, LocalTransform.FromPositionRotationScale(position + offset, quaternion.identity, scale));
            ecb.SetComponent(gem, new GemState
            {
                Value = value,
                Size = scale,
                DepositTeam = TeamId.None,
                SpawnServerTime = spawnServerTime,
            });

            if (burst)
            {
                // --- Original NGO GemSpawner launch + tumble ---
                float3 vel = GemExplosionMath.BurstVelocity(
                    spawnDir,
                    settings.AsteroidExplosionSpeed,
                    settings.SpeedRandomMin,
                    settings.SpeedRandomMax,
                    ref rng);
                float3 ang = GemExplosionMath.BurstAngularVelocity(settings.AngularSpeedMax, ref rng);
                ecb.SetComponent(gem, new GemKinematics { Velocity = vel, AngularVelocity = ang });
            }
            else
            {
                // Small outward nudge so mined gems are not stuck inside the asteroid mesh.
                float speed = rng.NextFloat(settings.MiningNudgeSpeedMin, settings.MiningNudgeSpeedMax);
                float3 ang = GemExplosionMath.BurstAngularVelocity(settings.AngularSpeedMax * 0.35f, ref rng);
                ecb.SetComponent(gem, new GemKinematics { Velocity = spawnDir * speed, AngularVelocity = ang });
            }
        }
    }
}
