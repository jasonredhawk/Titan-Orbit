using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Client: when a planet ghost Instantiates, drop the seed-hydrated copy with the same
    /// <see cref="PlanetState.PlanetId"/> so Join Game does not keep a duplicate hull.
    /// World: ClientSimulation.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct ClientSeedPlanetGhostReconcileSystem : ISystem
    {
        EntityQuery _ghostPlanets;
        EntityQuery _seedPlanets;

        public void OnCreate(ref SystemState state)
        {
            _ghostPlanets = state.GetEntityQuery(
                ComponentType.ReadOnly<PlanetTag>(),
                ComponentType.ReadOnly<PlanetState>(),
                ComponentType.ReadOnly<GhostInstance>());
            _seedPlanets = state.GetEntityQuery(
                ComponentType.ReadOnly<PlanetTag>(),
                ComponentType.ReadOnly<PlanetState>(),
                ComponentType.ReadOnly<ClientSeedHydratedMapBody>(),
                ComponentType.Exclude<GhostInstance>());
        }

        public void OnUpdate(ref SystemState state)
        {
            if (_ghostPlanets.IsEmptyIgnoreFilter || _seedPlanets.IsEmptyIgnoreFilter)
                return;

            var ghostStates = _ghostPlanets.ToComponentDataArray<PlanetState>(Allocator.Temp);
            var seedEntities = _seedPlanets.ToEntityArray(Allocator.Temp);
            var seedStates = _seedPlanets.ToComponentDataArray<PlanetState>(Allocator.Temp);
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            for (int s = 0; s < seedEntities.Length; s++)
            {
                int planetId = seedStates[s].PlanetId;
                if (planetId <= 0)
                    continue;
                for (int g = 0; g < ghostStates.Length; g++)
                {
                    if (ghostStates[g].PlanetId != planetId)
                        continue;
                    ecb.DestroyEntity(seedEntities[s]);
                    break;
                }
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
            ghostStates.Dispose();
            seedEntities.Dispose();
            seedStates.Dispose();
        }
    }
}
