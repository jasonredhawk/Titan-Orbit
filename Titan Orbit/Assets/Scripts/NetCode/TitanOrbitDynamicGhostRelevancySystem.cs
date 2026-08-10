using TitanOrbit.ECS;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// [NETCODE] Restricts ghost replication so asteroids are <b>not</b> streamed.
    /// Clients hydrate asteroids from the match seed (<see cref="ClientMapHydrateSystem"/>).
    /// Planets remain relevant (small count) so ownership / population / moon shield GhostFields
    /// keep working without a full sparse-sync rewrite.
    /// <para>
    /// Uses <see cref="GhostRelevancyMode.SetIsRelevant"/> with
    /// <see cref="GhostRelevancy.DefaultRelevancyQuery"/> =
    /// Any(Ship, Gem, PeopleTransport, Planet). Asteroids default to not replicated.
    /// </para>
    /// World: ServerSimulation. Initialization — runs once after GhostRelevancy exists.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial struct TitanOrbitDynamicGhostRelevancySystem : ISystem
    {
        bool _configured;
        EntityQuery _dynamicGhostQuery;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GhostRelevancy>();
            _dynamicGhostQuery = state.GetEntityQuery(new EntityQueryDesc
            {
                Any = new[]
                {
                    ComponentType.ReadOnly<ShipTag>(),
                    ComponentType.ReadOnly<GemTag>(),
                    ComponentType.ReadOnly<PeopleTransportTag>(),
                    ComponentType.ReadOnly<PlanetTag>(),
                },
            });
        }

        public void OnUpdate(ref SystemState state)
        {
            if (_configured)
                return;

            ref var relevancy = ref SystemAPI.GetSingletonRW<GhostRelevancy>().ValueRW;
            relevancy.GhostRelevancyMode = GhostRelevancyMode.SetIsRelevant;
            relevancy.DefaultRelevancyQuery = _dynamicGhostQuery;
            _configured = true;

            Debug.Log(
                "[TitanOrbitGhostRelevancy] SetIsRelevant — Ship/Gem/PeopleTransport/Planet stream; " +
                "asteroids use client seed hydrate (no GhostSpawn Instantiates flood).");
        }
    }
}
