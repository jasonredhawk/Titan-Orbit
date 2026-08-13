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
    /// Ship / PeopleTransport / Planet. This system still writes those ghosts into the set
    /// every tick — SetIsRelevant defaults chunks to irrelevant, and <c>set.Clear()</c> used
    /// to leave only gems. After send-grace (~3s) gem tiles then starved the owner hull:
    /// snapshots stopped, prediction reconciled to a stale pose, ship hung with no movement.
    /// </para>
    /// Gems stay nearby-only (or all gems when map / hull is missing) so distant idle crystals
    /// do not consume snapshot budget.
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

        /// <summary>Caches always-relevant, gem, and in-game connection queries.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GhostRelevancy>();
            _alwaysRelevantQuery = state.GetEntityQuery(new EntityQueryDesc
            {
                Any = new[]
                {
                    ComponentType.ReadOnly<ShipTag>(),
                    ComponentType.ReadOnly<PeopleTransportTag>(),
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
        }

        /// <summary>
        /// Rebuilds the relevancy set: always-relevant ghosts for every connection, plus nearby
        /// gems (or all gems when we cannot decide).
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

            for (int ci = 0; ci < connCount; ci++)
            {
                int connectionId = connIds[ci].Value;
                if (connectionId <= 0)
                    continue;

                // --- Always-relevant: ships / transports / planets ---
                // [NETCODE] SetIsRelevant + an empty set would drop these even when
                // DefaultRelevancyQuery is configured — do not leave that as the only path.
                for (int ai = 0; ai < alwaysCount; ai++)
                {
                    int ghostId = alwaysGhosts[ai].ghostId;
                    if (ghostId == 0)
                        continue;
                    set.TryAdd(new RelevantGhostForConnection(connectionId, ghostId), 1);
                }

                if (gemCount <= 0)
                    continue;

                bool addAllGems = !haveMap;
                float3 shipPos = float3.zero;

                if (!addAllGems &&
                    em.HasComponent<CommandTarget>(connEntities[ci]))
                {
                    Entity ship = em.GetComponentData<CommandTarget>(connEntities[ci]).targetEntity;
                    if (ship != Entity.Null &&
                        em.Exists(ship) &&
                        em.HasComponent<LocalTransform>(ship))
                    {
                        shipPos = em.GetComponentData<LocalTransform>(ship).Position;
                    }
                    else
                    {
                        addAllGems = true;
                    }
                }
                else if (!addAllGems)
                {
                    addAllGems = true;
                }

                for (int gi = 0; gi < gemCount; gi++)
                {
                    int ghostId = gemGhosts[gi].ghostId;
                    if (ghostId == 0)
                        continue;

                    if (!addAllGems)
                    {
                        float dist = ToroidalMapEcs.ToroidalDistance(
                            shipPos, gemTransforms[gi].Position, mapW, mapH);
                        if (dist > RelevancyRadius)
                            continue;
                    }

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
        }
    }
}
