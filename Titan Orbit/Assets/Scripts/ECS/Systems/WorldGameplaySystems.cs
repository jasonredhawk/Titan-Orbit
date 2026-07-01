using TitanOrbit.Core;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct MiningSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            foreach (var (shipTransform, shipState, shipEntity) in SystemAPI
                         .Query<RefRO<LocalTransform>, RefRO<ShipState>>()
                         .WithAll<ShipTag>()
                         .WithEntityAccess())
            {
                if (shipState.ValueRO.IsDead) continue;
                foreach (var (asteroidState, asteroidTransform, asteroidEntity) in SystemAPI
                             .Query<RefRW<AsteroidState>, RefRO<LocalTransform>>()
                             .WithAll<AsteroidTag>()
                             .WithEntityAccess())
                {
                    if (asteroidState.ValueRO.IsDestroyed) continue;
                    if (math.distance(shipTransform.ValueRO.Position, asteroidTransform.ValueRO.Position) > 6f)
                        continue;
                    var a = asteroidState.ValueRO;
                    a.RemainingGems = math.max(0f, a.RemainingGems - 5f * SystemAPI.Time.DeltaTime);
                    if (a.RemainingGems <= 0f)
                        a.IsDestroyed = true;
                    asteroidState.ValueRW = a;
                }
            }
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct GemSpawnSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            if (!SystemAPI.TryGetSingleton<GamePrefabs>(out var prefabs) || prefabs.Gem == Entity.Null)
                return;

            foreach (var (asteroidState, transform) in SystemAPI.Query<RefRO<AsteroidState>, RefRO<LocalTransform>>().WithAll<AsteroidTag>())
            {
                if (!asteroidState.ValueRO.IsDestroyed) continue;
            }
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct CaptureSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            foreach (var (planet, planetTransform) in SystemAPI.Query<RefRW<PlanetState>, RefRO<LocalTransform>>().WithAll<PlanetTag>())
            {
                if (planet.ValueRO.Ownership != TeamId.None) continue;
                foreach (var (shipState, shipTransform) in SystemAPI.Query<RefRO<ShipState>, RefRO<LocalTransform>>().WithAll<ShipTag>())
                {
                    if (shipState.ValueRO.Team == TeamId.None) continue;
                    if (math.distance(planetTransform.ValueRO.Position, shipTransform.ValueRO.Position) > 20f)
                        continue;
                    planet.ValueRW.Ownership = shipState.ValueRO.Team;
                    planet.ValueRW.Population += 1;
                }
            }
        }
    }
}
