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
    /// Any(Ship, PeopleTransport, Planet). <see cref="TitanOrbitGemGhostRelevancySystem"/>
    /// also writes those ghosts into <see cref="GhostRelevancy.GhostRelevancySet"/> each tick
    /// (plus nearby gems). Asteroids are never in this query — clients seed-hydrate them.
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
                "[TitanOrbitGhostRelevancy] SetIsRelevant — Ship/PeopleTransport/Planet always; " +
                "gems via TitanOrbitGemGhostRelevancySystem (nearby); " +
                "asteroids use client seed hydrate (no GhostSpawn Instantiates flood).");
        }
    }
}
