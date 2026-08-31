using TitanOrbit.ECS;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// [NETCODE] Asteroids are not streamed. Clients hydrate them from the match seed
    /// (<see cref="ClientMapHydrateSystem"/>). Ships and planets stay always-relevant via
    /// <see cref="GhostRelevancy.DefaultRelevancyQuery"/> so a brand-new hull
    /// (<c>GhostInstance.ghostId == 0</c>) can still leave on the first GhostSend.
    /// <para>
    /// <see cref="GhostRelevancyMode.SetIsRelevant"/> + DefaultRelevancyQuery = Any(Ship, Planet).
    /// Nearby gems go in <see cref="GhostRelevancy.GhostRelevancySet"/> only
    /// (<see cref="TitanOrbitGemGhostRelevancySystem"/>). Re-applies every frame so a
    /// recreated <see cref="GhostRelevancy"/> singleton cannot drop the query.
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
            _dynamicGhostQuery = new EntityQueryBuilder(Allocator.Temp)
                .WithAny<ShipTag, PlanetTag>()
                .Build(ref state);
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
                "[TitanOrbitGhostRelevancy] SetIsRelevant — DefaultRelevancyQuery=Ship|Planet " +
                "(re-applied every frame, includes ghostId=0); " +
                "gems via TitanOrbitGemGhostRelevancySystem set only; " +
                "asteroids use client seed hydrate + occupancy catch-up; " +
                "people transports are SpawnRpc (not ghosts).");
        }
    }
}
