using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>Ensures planets have growth state (subscene / runtime spawns).</summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(PlanetPopulationGrowthSystem))]
    [UpdateBefore(typeof(GemDepositSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    public partial struct PlanetEnsureComponentsSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            var ecb = new EntityCommandBuffer(Unity.Collections.Allocator.Temp);

            foreach (var (planet, entity) in SystemAPI.Query<RefRO<PlanetState>>()
                         .WithAll<PlanetTag>()
                         .WithNone<PlanetGrowthState>()
                         .WithEntityAccess())
            {
                ecb.AddComponent(entity, new PlanetGrowthState
                {
                    FractionalPopulation = math.max(0f, planet.ValueRO.Population),
                });
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }

    /// <summary>Grows planet population toward max cap over time (legacy Planet server Update).</summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(PeopleTransportSimulationSystem))]
    public partial struct PlanetPopulationGrowthSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            float dt = SystemAPI.Time.DeltaTime;
            float now = (float)SystemAPI.Time.ElapsedTime;

            foreach (var (planetState, growthState, transform) in SystemAPI
                         .Query<RefRW<PlanetState>, RefRW<PlanetGrowthState>, RefRO<LocalTransform>>()
                         .WithAll<PlanetTag>())
            {
                ref var planet = ref planetState.ValueRW;
                ref var growth = ref growthState.ValueRW;

                float planetSize = math.max(0.5f, transform.ValueRO.Scale);
                int maxPop = PlanetPopulationMath.GetMaxPopulation(planetSize, planet.PlanetLevel);
                float maxPopF = maxPop;

                SyncFractionalPopulation(ref planet, ref growth);

                if (growth.FractionalPopulation >= maxPopF - 0.0001f)
                {
                    growth.FractionalPopulation = maxPopF;
                    planet.Population = maxPop;
                    continue;
                }

                if (now < growth.LastHostilePopulationImpactServerTime +
                    PlanetPopulationMath.PopulationGrowthPauseAfterAttackSeconds)
                    continue;

                float rate = PlanetPopulationMath.GetGrowthRatePerSecond(planet.PlanetLevel);
                if (rate <= 0f)
                    continue;

                growth.FractionalPopulation = math.min(maxPopF, growth.FractionalPopulation + rate * dt);
                planet.Population = PlanetPopulationMath.FractionalToDisplayPopulation(growth.FractionalPopulation, maxPop);
            }
        }

        static void SyncFractionalPopulation(ref PlanetState planet, ref PlanetGrowthState growth)
        {
            int rounded = PlanetPopulationMath.FractionalToDisplayPopulation(growth.FractionalPopulation, int.MaxValue);
            if (planet.Population != rounded)
                growth.FractionalPopulation = math.max(0f, planet.Population);
        }
    }
}
