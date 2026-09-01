using TitanOrbit.ECS;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// [NETCODE] Asteroids are not streamed. Clients hydrate them from the match seed
    /// (<see cref="ClientMapHydrateSystem"/>). Planets stay always-relevant via
    /// <see cref="GhostRelevancy.DefaultRelevancyQuery"/> so a brand-new planet
    /// (<c>GhostInstance.ghostId == 0</c>) can still leave on the first GhostSend.
    /// Nearby ships + gems are added in <see cref="TitanOrbitGemGhostRelevancySystem"/>.
    /// <para>
    /// <see cref="GhostRelevancyMode.SetIsRelevant"/> + DefaultRelevancyQuery = Planet only.
    /// Re-applies every frame so a recreated <see cref="GhostRelevancy"/> singleton cannot drop the query.
    /// </para>
    /// World: ServerSimulation. Initialization — before simulation GhostSend.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial struct TitanOrbitDynamicGhostRelevancySystem : ISystem
    {
        bool _loggedOnce;
        EntityQuery _dynamicGhostQuery;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GhostRelevancy>();
            // EntityManager query so GhostSend GetEntityQueryMask matches planet archetypes
            // on IL2CPP (ISystem EntityQueryBuilder masks can no-op in the dedicated binary).
            _dynamicGhostQuery = state.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<PlanetTag>());
        }

        public void OnDestroy(ref SystemState state)
        {
            if (_dynamicGhostQuery != default)
                _dynamicGhostQuery.Dispose();
        }

        public void OnUpdate(ref SystemState state)
        {
            ref var relevancy = ref SystemAPI.GetSingletonRW<GhostRelevancy>().ValueRW;
            relevancy.GhostRelevancyMode = GhostRelevancyMode.SetIsRelevant;
            relevancy.DefaultRelevancyQuery = _dynamicGhostQuery;

            if (_loggedOnce)
                return;

            _loggedOnce = true;
            Debug.Log(
                "[TitanOrbitGhostRelevancy] SetIsRelevant — DefaultRelevancyQuery=Planet " +
                "(re-applied every frame, includes ghostId=0); " +
                "nearby ships + gems via TitanOrbitGemGhostRelevancySystem set; " +
                "asteroids use client seed hydrate + occupancy catch-up; " +
                "people transports are SpawnRpc (not ghosts).");
        }
    }
}
