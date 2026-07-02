using TitanOrbit.Core;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using Random = Unity.Mathematics.Random;

namespace TitanOrbit.ECS
{
    public static class GemEconomyConstants
    {
        public const float MiningRange = 6f;
        public const float MiningRate = 5f;
        public const float GemPickupRange = 2.5f;
        public const float PlanetInteractionRange = 20f;
        public const float MoonDockRangeMultiplier = 2.2f;
        public const float MoonLandingCompleteThreshold = 0.999f;
        public const float DepositRatePerShipLevel = 2f;
        public const float MinGemSpawnValue = 0.25f;
        public const float AsteroidExplosionSpeed = 2.2f;
        public const float AsteroidExplosionRadius = 1.4f;
        public const float GemDragPerSecond = 1.25f;
        public const float AsteroidHitRadiusScale = 0.85f;
        public const float MinAsteroidHitRadius = 2.5f;
    }

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
            var ecb = new EntityCommandBuffer(Allocator.Temp);

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

                    if (math.distance(shipTransform.ValueRO.Position, asteroidTransform.ValueRO.Position) >
                        GemEconomyConstants.MiningRange)
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

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(MiningSystem))]
    public partial struct GemMotionSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            float dt = SystemAPI.Time.DeltaTime;
            float drag = math.saturate(GemEconomyConstants.GemDragPerSecond * dt);

            foreach (var (kinematics, transform) in SystemAPI
                         .Query<RefRW<GemKinematics>, RefRW<LocalTransform>>()
                         .WithAll<GemTag>())
            {
                var vel = kinematics.ValueRO.Velocity;
                vel *= 1f - drag;
                if (math.lengthsq(vel) < 0.0004f)
                    vel = float3.zero;

                var lt = transform.ValueRO;
                lt.Position += vel * dt;
                transform.ValueRW = lt;
                kinematics.ValueRW = new GemKinematics { Velocity = vel };
            }
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(AsteroidDestructionSystem))]
    public partial struct GemPickupSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            var ecb = new EntityCommandBuffer(Allocator.Temp);

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

                foreach (var (gemState, gemTransform, gemEntity) in SystemAPI
                             .Query<RefRO<GemState>, RefRO<LocalTransform>>()
                             .WithAll<GemTag>()
                             .WithEntityAccess())
                {
                    if (math.distance(shipTransform.ValueRO.Position, gemTransform.ValueRO.Position) >
                        GemEconomyConstants.GemPickupRange)
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
    }

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

                foreach (var (planetState, _) in SystemAPI
                             .Query<RefRW<PlanetState>, RefRO<LocalTransform>>()
                             .WithAll<PlanetTag>())
                {
                    if (planetState.ValueRO.Ownership != shipState.ValueRO.Team)
                        continue;

                    if (!CanDepositAtPlanet(
                            shipInput.ValueRO,
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
                }
            }
        }

        static bool CanDepositAtPlanet(
            in ShipInput input,
            in ShipMoonDockState moonDock,
            in PlanetState planet)
        {
            if (input.Thrust)
                return false;

            if (moonDock.MoonPlanetId != planet.PlanetId || moonDock.MoonPlanetId == 0)
                return false;

            return moonDock.LandingProgress >= GemEconomyConstants.MoonLandingCompleteThreshold;
        }
    }

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

    static class GemSpawning
    {
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
