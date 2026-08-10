using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server bootstrap pass that adds <see cref="PlanetGrowthState"/> to any planet ghost missing it.
    /// Runs before population growth and gem deposit so fractional population math has storage.
    /// World: ServerSimulation. Group: SimulationSystemGroup, before PlanetPopulationGrowthSystem.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(PlanetPopulationGrowthSystem))]
    [UpdateBefore(typeof(GemDepositSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    public partial struct PlanetEnsureComponentsSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            // [ECS/DOTS] ECB defers structural changes until after the query loop.
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

    /// <summary>
    /// Server-authoritative passive population growth on planets toward level-based caps.
    /// Rate is a fixed fraction of the effective max (empty → full in
    /// <see cref="PlanetPopulationMath.FullRefillSeconds"/>). Uses fractional accumulator in
    /// <see cref="PlanetGrowthState"/> for smooth growth; replicates integer
    /// <see cref="PlanetState.Population"/> to clients. Pauses briefly after hostile population
    /// events (attacks). Runs after people transport sim updates orbit counts.
    /// World: ServerSimulation.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(PeopleTransportSimulationSystem))]
    public partial struct PlanetPopulationGrowthSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            // --- Timestep ---
            float dt = SystemAPI.Time.DeltaTime;
            float now = (float)SystemAPI.Time.ElapsedTime;

            foreach (var (planetState, growthState, transform) in SystemAPI
                         .Query<RefRW<PlanetState>, RefRW<PlanetGrowthState>, RefRO<LocalTransform>>()
                         .WithAll<PlanetTag>())
            {
                ref var planet = ref planetState.ValueRW;
                ref var growth = ref growthState.ValueRW;

                // [TITAN-ORBIT] Max population scales with planet visual size and upgrade level.
                // ConnectionBonusFraction stacks triangle corner bonuses (original SetConnectionBonuses).
                // Shared helper keeps server growth and planet world labels on the same formula.
                float planetSize = math.max(0.5f, transform.ValueRO.Scale);
                int maxPop = PlanetPopulationMath.GetEffectiveMaxPopulation(
                    planetSize, planet.PlanetLevel, growth.ConnectionBonusFraction);
                float maxPopF = maxPop;

                SyncFractionalPopulation(ref planet, ref growth);

                // --- At cap — snap and skip growth ---
                if (growth.FractionalPopulation >= maxPopF - 0.0001f)
                {
                    growth.FractionalPopulation = maxPopF;
                    planet.Population = maxPop;
                    continue;
                }

                // [TITAN-ORBIT] Growth pauses after attacks for a designer-tuned cooldown window.
                if (now < growth.LastHostilePopulationImpactServerTime +
                    PlanetPopulationMath.PopulationGrowthPauseAfterAttackSeconds)
                    continue;

                // [TITAN-ORBIT] Rate from effective max (bonus already baked into maxPop) — do not
                // multiply by (1+bonus) again, or territory planets would refill faster than FullRefillSeconds.
                float rate = PlanetPopulationMath.GetGrowthRatePerSecond(maxPop);
                if (rate <= 0f)
                    continue;

                growth.FractionalPopulation = math.min(maxPopF, growth.FractionalPopulation + rate * dt);
                planet.Population = PlanetPopulationMath.FractionalToDisplayPopulation(growth.FractionalPopulation, maxPop);
            }
        }

        /// <summary>
        /// Reconciles fractional accumulator when replicated integer Population was changed externally.
        /// </summary>
        static void SyncFractionalPopulation(ref PlanetState planet, ref PlanetGrowthState growth)
        {
            int rounded = PlanetPopulationMath.FractionalToDisplayPopulation(growth.FractionalPopulation, int.MaxValue);
            if (planet.Population != rounded)
                growth.FractionalPopulation = math.max(0f, planet.Population);
        }
    }
}
