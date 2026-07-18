using TitanOrbit.Core;
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

        /// <summary>Initial outward speed when asteroid destruction bursts gems.</summary>
        public const float AsteroidExplosionSpeed = 2.2f;

        /// <summary>Random offset radius for gem burst spawn positions.</summary>
        public const float AsteroidExplosionRadius = 1.4f;

        /// <summary>Per-second velocity damping on free-floating gems.</summary>
        public const float GemDragPerSecond = 1.25f;

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
                    GemSpawning.Spawn(ecb, prefabs.Gem, asteroidTransform.ValueRO.Position, mined, (uint)asteroidEntity.Index, burst: false);
                }
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }

    /// <summary>
    /// Server: applies drag and integrates gem positions from <see cref="GemKinematics.Velocity"/>.
    /// Gems are scripted movers — not Unity Physics bodies. Positions stay unbounded like ships;
    /// tractor reach still uses toroidal distance.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(MiningSystem))]
    public partial struct GemMotionSystem : ISystem
    {
        /// <summary>Integrates velocity with drag (unbounded XZ).</summary>
        public void OnUpdate(ref SystemState state)
        {
            float dt = SystemAPI.Time.DeltaTime;
            // [TITAN-ORBIT] Gems slow down over time so bursts from mining settle near asteroids.
            float drag = math.saturate(GemEconomyConstants.GemDragPerSecond * dt);

            foreach (var (kinematics, transform) in SystemAPI
                         .Query<RefRW<GemKinematics>, RefRW<LocalTransform>>()
                         .WithAll<GemTag>())
            {
                var vel = kinematics.ValueRO.Velocity;
                vel *= 1f - drag;
                if (math.lengthsq(vel) < 0.0004f)
                    vel = float3.zero;

                // --- Integrate in unbounded space (same as ships); toroidal math is for reach only ---
                var lt = transform.ValueRO;
                lt.Position += vel * dt;
                transform.ValueRW = lt;
                kinematics.ValueRW = new GemKinematics { Velocity = vel };
            }
        }
    }

    /// <summary>
    /// Server: collects gems into ship cargo when within hull or wing tractor pickup radius.
    /// Runs after <see cref="GemTractorBeamSystem"/> so pulled gems can be collected at wings.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(AsteroidDestructionSystem))]
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
    /// Credits home-planet contributed gems for store purchases.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(GemPickupSystem))]
    public partial struct GemDepositSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            float dt = SystemAPI.Time.DeltaTime;

            foreach (var (shipState, shipInput, moonDock, shipEntity) in SystemAPI
                         .Query<RefRW<ShipState>, RefRO<ShipInput>, RefRO<ShipMoonDockState>>()
                         .WithAll<ShipTag>()
                         .WithEntityAccess())
            {
                if (shipState.ValueRO.IsDead || shipState.ValueRO.AwaitingTeamSelection)
                    continue;
                if (shipState.ValueRO.Team == TeamId.None || shipState.ValueRO.CurrentGems <= 0f)
                    continue;

                bool wantDeposit = shipInput.ValueRO.WantDepositGems;
                if (state.EntityManager.HasComponent<ShipDepositIntent>(shipEntity))
                    wantDeposit = state.EntityManager.GetComponentData<ShipDepositIntent>(shipEntity).WantDepositGems;

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

                    float gemValue = math.max(1f, shipState.ValueRO.ShipLevel);
                    float rate = gemValue * GemEconomyConstants.DepositRatePerShipLevel * dt;
                    float amount = math.min(rate, shipState.ValueRO.CurrentGems);
                    if (amount <= 0.001f)
                        continue;

                    var ship = shipState.ValueRO;
                    ship.CurrentGems -= amount;
                    shipState.ValueRW = ship;

                    var planet = planetState.ValueRO;
                    int level = planet.PlanetLevel;
                    float gems = planet.CurrentGems;
                    PlanetEconomyMath.DepositGems(ref level, ref gems, amount);
                    planet.PlanetLevel = level;
                    planet.CurrentGems = gems;
                    planetState.ValueRW = planet;

                    if (planet.IsHomePlanet && ownerNetworkId > 0)
                        ContributedGemsLogic.Add(state.EntityManager, planetEntity, ownerNetworkId, amount);
                }
            }
        }

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
    }

    /// <summary>
    /// Server: when an asteroid is destroyed, spawns a burst of gem entities with explosion velocity.
    /// Runs after bullets and mining may have marked asteroids IsDestroyed.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(BulletSimulationSystem))]
    [UpdateAfter(typeof(MiningSystem))]
    public partial struct AsteroidDestructionSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GamePrefabs>();
        }

        public void OnUpdate(ref SystemState state)
        {
            if (!SystemAPI.TryGetSingleton<GamePrefabs>(out var prefabs) || prefabs.Gem == Entity.Null)
                return;

            var ecb = new EntityCommandBuffer(Allocator.Temp);
            var rng = Random.CreateFromIndex((uint)(SystemAPI.Time.ElapsedTime * 1000f) + 91u);

            foreach (var (asteroidState, asteroidTransform, entity) in SystemAPI
                         .Query<RefRO<AsteroidState>, RefRO<LocalTransform>>()
                         .WithAll<AsteroidTag>()
                         .WithEntityAccess())
            {
                if (!asteroidState.ValueRO.IsDestroyed)
                    continue;

                float remaining = asteroidState.ValueRO.RemainingGems;
                if (remaining >= GemEconomyConstants.MinGemSpawnValue)
                {
                    float3 pos = asteroidTransform.ValueRO.Position;
                    while (remaining >= GemEconomyConstants.MinGemSpawnValue)
                    {
                        float chunk = math.min(remaining, rng.NextFloat(6f, 14f));
                        GemSpawning.Spawn(ecb, prefabs.Gem, pos, chunk, (uint)entity.Index + (uint)(chunk * 100f), burst: true);
                        remaining -= chunk;
                    }
                }

                ecb.DestroyEntity(entity);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }

    /// <summary>Shared gem entity spawn helper for mining and asteroid destruction bursts.</summary>
    static class GemSpawning
    {
        /// <summary>
        /// Instantiates a gem prefab with value, optional burst velocity, and toroidal-safe offset.
        /// </summary>
        public static void Spawn(EntityCommandBuffer ecb, Entity gemPrefab, float3 position, float value, uint salt, bool burst)
        {
            if (value <= 0f)
                return;

            var rng = Random.CreateFromIndex(math.hash(position) + salt + 17u);
            float3 spawnDir = math.normalize(new float3(rng.NextFloat(-1f, 1f), 0f, rng.NextFloat(-1f, 1f)));
            if (math.lengthsq(spawnDir) < 0.01f)
                spawnDir = new float3(0f, 0f, 1f);

            float radius = burst ? GemEconomyConstants.AsteroidExplosionRadius : 0.8f;
            float3 offset = spawnDir * radius * rng.NextFloat(0.3f, 1f);
            float scale = math.clamp(math.sqrt(value) * 0.2f, 0.2f, 0.5f);

            Entity gem = ecb.Instantiate(gemPrefab);
            ecb.SetComponent(gem, LocalTransform.FromPositionRotationScale(position + offset, quaternion.identity, scale));
            ecb.SetComponent(gem, new GemState
            {
                Value = value,
                Size = scale,
                DepositTeam = TeamId.None,
            });

            if (burst)
            {
                float speed = GemEconomyConstants.AsteroidExplosionSpeed * rng.NextFloat(0.45f, 1f);
                ecb.SetComponent(gem, new GemKinematics { Velocity = spawnDir * speed });
            }
        }
    }
}
