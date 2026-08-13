using TitanOrbit;
using TitanOrbit.ECS;
using TitanOrbit.Generation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// [NETCODE] Rebuilds <see cref="GhostRelevancy.GhostRelevancySet"/> each tick under
    /// <see cref="GhostRelevancyMode.SetIsRelevant"/>.
    /// <para>
    /// <see cref="TitanOrbitDynamicGhostRelevancySystem"/> sets DefaultRelevancyQuery to
    /// Ship / Planet. This system still writes those ghosts into the set every tick.
    /// Gems are nearby the command-target hull, or nearby <b>any live ship</b> while that
    /// connection has no hull yet (late-join window). Never "all gems on the map".
    /// </para>
    /// <para>
    /// World: ServerSimulation. Group: SimulationSystemGroup, before <see cref="GhostSendSystem"/>.
    /// </para>
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(GhostSendSystem))]
    public partial struct TitanOrbitGemGhostRelevancySystem : ISystem
    {
        /// <summary>
        /// Toroidal XZ radius (world units) around each ship. Wider than tractor search (~3–4.5)
        /// so a burst gem does not pop out of existence at the beam edge. 40 ≈ hear-range band.
        /// </summary>
        public const float RelevancyRadius = 40f;

        EntityQuery _alwaysRelevantQuery;
        EntityQuery _gemQuery;
        EntityQuery _connectionQuery;
        EntityQuery _shipQuery;

        /// <summary>Caches always-relevant, gem, and in-game connection queries.</summary>
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
            _gemQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<GemTag>(),
                ComponentType.ReadOnly<LocalTransform>(),
                ComponentType.ReadOnly<GhostInstance>());
            _connectionQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<NetworkStreamInGame>(),
                ComponentType.ReadOnly<NetworkId>());
            _shipQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<ShipTag>(),
                ComponentType.ReadOnly<LocalTransform>(),
                ComponentType.ReadOnly<GhostInstance>());
        }

        /// <summary>
        /// Rebuilds the relevancy set: always-relevant ghosts for every connection, plus gems
        /// near the command-target hull (or near any live ship during the join window).
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            ref var relevancy = ref SystemAPI.GetSingletonRW<GhostRelevancy>().ValueRW;
            var set = relevancy.GhostRelevancySet;
            set.Clear();

            int connCount = _connectionQuery.CalculateEntityCount();
            if (connCount <= 0)
                return;

            var connEntities = _connectionQuery.ToEntityArray(Allocator.Temp);
            var connIds = _connectionQuery.ToComponentDataArray<NetworkId>(Allocator.Temp);
            var alwaysGhosts = _alwaysRelevantQuery.ToComponentDataArray<GhostInstance>(Allocator.Temp);
            int alwaysCount = alwaysGhosts.Length;

            int gemCount = _gemQuery.CalculateEntityCount();
            NativeArray<LocalTransform> gemTransforms = default;
            NativeArray<GhostInstance> gemGhosts = default;
            if (gemCount > 0)
            {
                gemTransforms = _gemQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
                gemGhosts = _gemQuery.ToComponentDataArray<GhostInstance>(Allocator.Temp);
            }

            bool haveMap = SystemAPI.TryGetSingleton<MapStateSingleton>(out var mapState) &&
                           ToroidalMapEcs.IsValidMapSize(mapState.MapWidth, mapState.MapHeight);
            float mapW = haveMap ? mapState.MapWidth : 0f;
            float mapH = haveMap ? mapState.MapHeight : 0f;

            var em = state.EntityManager;

            NativeArray<LocalTransform> shipTransforms = default;
            int shipCount = _shipQuery.CalculateEntityCount();
            if (shipCount > 0)
                shipTransforms = _shipQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);

            for (int ci = 0; ci < connCount; ci++)
            {
                int connectionId = connIds[ci].Value;
                if (connectionId <= 0)
                    continue;

                // --- Always-relevant: ships / planets ---
                for (int ai = 0; ai < alwaysCount; ai++)
                {
                    int ghostId = alwaysGhosts[ai].ghostId;
                    if (ghostId == 0)
                        continue;
                    set.TryAdd(new RelevantGhostForConnection(connectionId, ghostId), 1);
                }

                if (gemCount <= 0 || !haveMap)
                    continue;

                bool haveOwnHull = false;
                float3 ownPos = float3.zero;
                if (em.HasComponent<CommandTarget>(connEntities[ci]))
                {
                    Entity ship = em.GetComponentData<CommandTarget>(connEntities[ci]).targetEntity;
                    if (ship != Entity.Null &&
                        em.Exists(ship) &&
                        em.HasComponent<LocalTransform>(ship))
                    {
                        ownPos = em.GetComponentData<LocalTransform>(ship).Position;
                        haveOwnHull = true;
                    }
                }

                for (int gi = 0; gi < gemCount; gi++)
                {
                    int ghostId = gemGhosts[gi].ghostId;
                    if (ghostId == 0)
                        continue;

                    float3 gemPos = gemTransforms[gi].Position;
                    bool nearby = false;
                    if (haveOwnHull)
                    {
                        nearby = ToroidalMapEcs.ToroidalDistance(ownPos, gemPos, mapW, mapH) <= RelevancyRadius;
                    }
                    else
                    {
                        for (int s = 0; s < shipCount; s++)
                        {
                            if (ToroidalMapEcs.ToroidalDistance(
                                    shipTransforms[s].Position, gemPos, mapW, mapH) <= RelevancyRadius)
                            {
                                nearby = true;
                                break;
                            }
                        }
                    }

                    if (!nearby)
                        continue;

                    set.TryAdd(new RelevantGhostForConnection(connectionId, ghostId), 1);
                }
            }

            alwaysGhosts.Dispose();
            connEntities.Dispose();
            connIds.Dispose();
            if (gemTransforms.IsCreated)
                gemTransforms.Dispose();
            if (gemGhosts.IsCreated)
                gemGhosts.Dispose();
            if (shipTransforms.IsCreated)
                shipTransforms.Dispose();
        }
    }
}
