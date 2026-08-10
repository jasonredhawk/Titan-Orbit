using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [TITAN-ORBIT] Safety net for seed-hydrated / locally Instantiated map bodies that still carry
    /// <see cref="GhostInstance"/>. Failed mid-query strips (or root-only strips that left
    /// LinkedEntityGroup children) produce ghostId==0 entities GhostUpdateSystem rejects every frame.
    /// <para>
    /// Only gathers entities tagged <see cref="ClientSeedHydratedMapBody"/> — never a full
    /// GhostInstance / asteroid ToEntityArray (join-crash rule).
    /// </para>
    /// World: ClientSimulation.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(ClientMapHydrateSystem))]
    public partial struct ClientSeedHydratedGhostStripSystem : ISystem
    {
        EntityQuery _hydratedBadGhostQuery;

        /// <summary>Caches the tagged leftover-GhostInstance query.</summary>
        public void OnCreate(ref SystemState state)
        {
            // --- Only seed-hydrated locals (root + LinkedEntityGroup members are tagged) ---
            _hydratedBadGhostQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<ClientSeedHydratedMapBody>(),
                ComponentType.ReadOnly<GhostInstance>());
            state.RequireForUpdate(_hydratedBadGhostQuery);
        }

        /// <summary>Strips leftover ghost identity from hydrated map bodies (budgeted).</summary>
        public void OnUpdate(ref SystemState state)
        {
            const int budget = 48;
            using var entities = _hydratedBadGhostQuery.ToEntityArray(Allocator.Temp);
            int n = math.min(budget, entities.Length);
            if (n <= 0)
                return;

            var em = state.EntityManager;
            for (int i = 0; i < n; i++)
                ClientLocalMapBodySpawn.StripGhostNetworking(em, entities[i]);

            Debug.Log(
                "[ClientMapHydrate] Stripped GhostInstance from " + n +
                " seed-hydrated entit(y/ies) (invalid ghostId cleanup).");
        }
    }
}
