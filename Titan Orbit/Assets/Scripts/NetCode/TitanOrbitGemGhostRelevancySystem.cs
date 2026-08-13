using TitanOrbit;
using TitanOrbit.ECS;
using TitanOrbit.Generation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// [NETCODE] Rebuilds <see cref="GhostRelevancy.GhostRelevancySet"/> each tick under
    /// <see cref="GhostRelevancyMode.SetIsRelevant"/>.
    /// <para>
    /// Ships and planets stay always-relevant (written into the set every tick).
    /// Gems Instantiates only in a spatial subset: 40u around the command-target hull,
    /// or around every live ship while that connection has no hull (late-join window).
    /// Gems this connection is already tractoring stay relevant even outside 40u (pin)
    /// so a mid-pull crystal cannot freeze or despawn on the scooping client.
    /// Never all-map gems — Unity advises against tens of thousands relevant to one connection.
    /// GhostSpawn Instantiates of that subset is required (16/frame budget).
    /// </para>
    /// World: ServerSimulation. Group: SimulationSystemGroup, before <see cref="GhostSendSystem"/>.
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

        /// <summary>How often we print [GemRelevancy] live vs inserted counts (seconds).</summary>
        const float RelevancyLogIntervalSeconds = 5f;

        EntityQuery _alwaysRelevantQuery;
        EntityQuery _gemQuery;
        EntityQuery _connectionQuery;
        EntityQuery _shipQuery;
        double _lastRelevancyLogElapsed;

        /// <summary>Caches always-relevant, gem, ship, and in-game connection queries.</summary>
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
                ComponentType.ReadOnly<GhostInstance>(),
                ComponentType.ReadOnly<GemMotionState>());
            _connectionQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<NetworkStreamInGame>(),
                ComponentType.ReadOnly<NetworkId>());
            _shipQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<ShipTag>(),
                ComponentType.ReadOnly<LocalTransform>(),
                ComponentType.ReadOnly<GhostInstance>());
        }

        /// <summary>
        /// Rebuilds the relevancy set: ships/planets for every connection, plus nearby gems
        /// from the spatial hash and tractor-pinned gems for the scooping connection.
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

            bool haveMap = SystemAPI.TryGetSingleton<MapStateSingleton>(out var mapState) &&
                           ToroidalMapEcs.IsValidMapSize(mapState.MapWidth, mapState.MapHeight);
            float mapW = haveMap ? mapState.MapWidth : 0f;
            float mapH = haveMap ? mapState.MapHeight : 0f;

            int gemCount = _gemQuery.CalculateEntityCount();
            NativeArray<Entity> gemEntities = default;
            NativeArray<LocalTransform> gemTransforms = default;
            NativeArray<GhostInstance> gemGhosts = default;
            NativeArray<GemMotionState> gemMotions = default;
            GemSpatialHash hash = default;
            if (gemCount > 0 && haveMap)
            {
                gemEntities = _gemQuery.ToEntityArray(Allocator.Temp);
                gemTransforms = _gemQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
                gemGhosts = _gemQuery.ToComponentDataArray<GhostInstance>(Allocator.Temp);
                gemMotions = _gemQuery.ToComponentDataArray<GemMotionState>(Allocator.Temp);
                hash = GemSpatialHash.Build(
                    gemEntities, gemTransforms, gemGhosts, gemMotions, mapW, mapH, Allocator.Temp);
            }

            var em = state.EntityManager;
            NativeArray<LocalTransform> shipTransforms = default;
            int shipCount = _shipQuery.CalculateEntityCount();
            if (shipCount > 0)
                shipTransforms = _shipQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);

            var nearby = new NativeList<int>(64, Allocator.Temp);
            var seenScratch = new NativeHashSet<int>(64, Allocator.Temp);
            int gemInserts = 0;

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

                if (!hash.IsCreated || gemCount <= 0)
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

                nearby.Clear();
                seenScratch.Clear();
                if (haveOwnHull)
                {
                    hash.GatherNearby(ownPos, RelevancyRadius, nearby, seenScratch);
                    hash.AppendPinnedToShip(connectionId, nearby, seenScratch);
                }
                else
                {
                    // --- Join window: gems near any live ship, still not all-map ---
                    for (int s = 0; s < shipCount; s++)
                    {
                        hash.GatherNearby(
                            shipTransforms[s].Position,
                            RelevancyRadius,
                            nearby,
                            seenScratch,
                            clearDst: s == 0);
                    }
                }

                for (int n = 0; n < nearby.Length; n++)
                {
                    int ghostId = hash.Entries[nearby[n]].GhostId;
                    if (ghostId == 0)
                        continue;
                    if (set.TryAdd(new RelevantGhostForConnection(connectionId, ghostId), 1))
                        gemInserts++;
                }
            }

            double now = SystemAPI.Time.ElapsedTime;
            if (gemCount > 0 && now - _lastRelevancyLogElapsed >= RelevancyLogIntervalSeconds)
            {
                _lastRelevancyLogElapsed = now;
                Debug.Log(
                    "[GemRelevancy] live=" + gemCount +
                    " gemInsertsThisTick=" + gemInserts +
                    " connections=" + connCount +
                    " (spatial + tractor pin, not all-map)");
            }

            nearby.Dispose();
            seenScratch.Dispose();
            alwaysGhosts.Dispose();
            connEntities.Dispose();
            connIds.Dispose();
            if (hash.IsCreated)
                hash.Dispose();
            if (gemEntities.IsCreated)
                gemEntities.Dispose();
            if (gemTransforms.IsCreated)
                gemTransforms.Dispose();
            if (gemGhosts.IsCreated)
                gemGhosts.Dispose();
            if (gemMotions.IsCreated)
                gemMotions.Dispose();
            if (shipTransforms.IsCreated)
                shipTransforms.Dispose();
        }
    }
}
