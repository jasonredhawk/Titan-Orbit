using TitanOrbit.ECS;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// [NETCODE] Restricts ghost replication so asteroids are <b>not</b> streamed.
    /// Clients hydrate asteroids from the match seed (<see cref="ClientMapHydrateSystem"/>).
    /// Planets remain always relevant (small count) so ownership / population / moon shield
    /// GhostFields keep working. Ships are not in this query — they replicate only when
    /// <see cref="TitanOrbitGemGhostRelevancySystem"/> adds them (owner, or inside the camera view).
    /// <para>
    /// Uses <see cref="GhostRelevancyMode.SetIsRelevant"/> with
    /// <see cref="GhostRelevancy.DefaultRelevancyQuery"/> = Planet.
    /// Ghosts that match the query go to every connection. Everyone else needs the relevancy set.
    /// Gems are event-hydrated RPCs. Asteroids are never in this query — clients seed-hydrate them.
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
                "[TitanOrbitGhostRelevancy] SetIsRelevant — Planet always; " +
                "ships only for the owner and inside that player's camera view; " +
                "gems are event-hydrated RPCs (not ghosts); " +
                "asteroids use client seed hydrate + occupancy catch-up; " +
                "people transports are SpawnRpc (not ghosts).");
        }
    }
}
