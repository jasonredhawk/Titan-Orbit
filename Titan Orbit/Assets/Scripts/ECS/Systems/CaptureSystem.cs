using TitanOrbit.Core;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(GemDepositSystem))]
    public partial struct CaptureSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            foreach (var (planet, planetTransform) in SystemAPI.Query<RefRW<PlanetState>, RefRO<LocalTransform>>().WithAll<PlanetTag>())
            {
                if (planet.ValueRO.Ownership != TeamId.None)
                    continue;

                foreach (var (shipState, shipTransform) in SystemAPI.Query<RefRO<ShipState>, RefRO<LocalTransform>>().WithAll<ShipTag>())
                {
                    if (shipState.ValueRO.Team == TeamId.None)
                        continue;
                    if (math.distance(planetTransform.ValueRO.Position, shipTransform.ValueRO.Position) >
                        GemEconomyConstants.PlanetInteractionRange)
                        continue;

                    planet.ValueRW.Ownership = shipState.ValueRO.Team;
                    planet.ValueRW.Population += 1;
                }
            }
        }
    }
}
