using TitanOrbit.ECS;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// Rebuilds <see cref="GhostRelevancy.GhostRelevancySet"/> each tick under
    /// <see cref="GhostRelevancyMode.SetIsRelevant"/>.
    /// <para>
    /// Ships and planets stay always-relevant (written into the set every tick).
    /// Gems are event-hydrated (spawn / burst RPCs) and are not added to this set.
    /// </para>
    /// World: ServerSimulation. Group: SimulationSystemGroup, before <see cref="GhostSendSystem"/>.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(GhostSendSystem))]
    public partial struct TitanOrbitGemGhostRelevancySystem : ISystem
    {
        /// <summary>Kept for hear-range / visualizer comments (no longer a ghost radius).</summary>
        public const float RelevancyRadius = 40f;

        EntityQuery _alwaysRelevantQuery;
        EntityQuery _connectionQuery;

        /// <summary>Caches always-relevant ships/planets and in-game connections.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GhostRelevancy>();
            _alwaysRelevantQuery = state.GetEntityQuery(new EntityQueryDesc
            {
                Any = new[]
                {
                    ComponentType.ReadOnly<ShipTag>(),
                    ComponentType.ReadOnly<PlanetTag>(),
                },
                All = new[]
                {
                    ComponentType.ReadOnly<GhostInstance>(),
                },
            });
            _connectionQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<NetworkStreamInGame>(),
                ComponentType.ReadOnly<NetworkId>());
        }

        /// <summary>Rebuilds the relevancy set: ships and planets for every connection.</summary>
        public void OnUpdate(ref SystemState state)
        {
            ref var relevancy = ref SystemAPI.GetSingletonRW<GhostRelevancy>().ValueRW;
            var set = relevancy.GhostRelevancySet;
            set.Clear();

            int connCount = _connectionQuery.CalculateEntityCount();
            if (connCount <= 0)
                return;

            var connIds = _connectionQuery.ToComponentDataArray<NetworkId>(Allocator.Temp);
            var alwaysGhosts = _alwaysRelevantQuery.ToComponentDataArray<GhostInstance>(Allocator.Temp);
            int alwaysCount = alwaysGhosts.Length;

            for (int ci = 0; ci < connCount; ci++)
            {
                int connectionId = connIds[ci].Value;
                if (connectionId <= 0)
                    continue;

                for (int ai = 0; ai < alwaysCount; ai++)
                {
                    int ghostId = alwaysGhosts[ai].ghostId;
                    if (ghostId == 0)
                        continue;
                    set.TryAdd(new RelevantGhostForConnection(connectionId, ghostId), 1);
                }
            }

            alwaysGhosts.Dispose();
            connIds.Dispose();
        }
    }
}
