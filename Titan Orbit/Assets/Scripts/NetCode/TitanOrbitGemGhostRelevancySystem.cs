using TitanOrbit;
using TitanOrbit.Diagnostics;
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
    /// [NETCODE] Official SetIsRelevant contract before <see cref="GhostSendSystem"/>.
    /// <para>
    /// Ships and planets are always-relevant: <see cref="GhostRelevancy.DefaultRelevancyQuery"/>
    /// (Any Ship|Planet, Unity test shape) <b>and</b> each assigned ghost id is written into
    /// <see cref="GhostRelevancy.GhostRelevancySet"/>. Nearby gems use the same set.
    /// Debug  db511d: snapshots arrived empty (snapRecv climbed, GhostCountOnServer=0) when
    /// planets were omitted from the set and only the query was used.
    /// </para>
    /// World: ServerSimulation. Group: SimulationSystemGroup, before <see cref="GhostSendSystem"/>.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(GhostSendSystem))]
    public partial struct TitanOrbitGemGhostRelevancySystem : ISystem
    {
        public const float RelevancyRadius = 40f;
        const float RelevancyLogIntervalSeconds = 5f;

        EntityQuery _defaultRelevantQuery;
        EntityQuery _alwaysGhostQuery;
        EntityQuery _planetTagQuery;
        EntityQuery _planetGhostQuery;
        EntityQuery _gemQuery;
        EntityQuery _connectionQuery;
        EntityQuery _shipQuery;
        double _lastRelevancyLogElapsed;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GhostRelevancy>();
            // Unity RelevancyTests shape: EntityQueryBuilder.WithAny, built for this world.
            _defaultRelevantQuery = new EntityQueryBuilder(Allocator.Temp)
                .WithAny<ShipTag, PlanetTag>()
                .Build(ref state);
            _alwaysGhostQuery = new EntityQueryBuilder(Allocator.Temp)
                .WithAny<ShipTag, PlanetTag>()
                .WithAll<GhostInstance>()
                .Build(ref state);
            _planetTagQuery = state.GetEntityQuery(ComponentType.ReadOnly<PlanetTag>());
            _planetGhostQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<PlanetTag>(),
                ComponentType.ReadOnly<GhostInstance>());
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

        public void OnUpdate(ref SystemState state)
        {
            ref var relevancy = ref SystemAPI.GetSingletonRW<GhostRelevancy>().ValueRW;
            relevancy.GhostRelevancyMode = GhostRelevancyMode.SetIsRelevant;
            relevancy.DefaultRelevancyQuery = _defaultRelevantQuery;

            var set = relevancy.GhostRelevancySet;
            set.Clear();

            double now = SystemAPI.Time.ElapsedTime;
            int connCount = _connectionQuery.CalculateEntityCount();
            int alwaysCount = _defaultRelevantQuery.CalculateEntityCount();
            if (connCount <= 0)
            {
                LogIfDue(ref state, now, 0, 0, 0, alwaysCount, 0, 0, 0, 0);
                return;
            }

            var connEntities = _connectionQuery.ToEntityArray(Allocator.Temp);
            var connIds = _connectionQuery.ToComponentDataArray<NetworkId>(Allocator.Temp);
            var alwaysGhosts = _alwaysGhostQuery.ToComponentDataArray<GhostInstance>(Allocator.Temp);
            int alwaysGhostCount = alwaysGhosts.Length;
            int alwaysIds = 0;
            for (int i = 0; i < alwaysGhostCount; i++)
            {
                if (alwaysGhosts[i].ghostId != 0)
                    alwaysIds++;
            }

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
            var em = state.EntityManager;
            NativeArray<LocalTransform> shipTransforms = default;
            int shipCount = _shipQuery.CalculateEntityCount();
            bool needGemHash = gemCount > 0 && haveMap && shipCount > 0;
            if (needGemHash)
            {
                gemEntities = _gemQuery.ToEntityArray(Allocator.Temp);
                gemTransforms = _gemQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
                gemGhosts = _gemQuery.ToComponentDataArray<GhostInstance>(Allocator.Temp);
                gemMotions = _gemQuery.ToComponentDataArray<GemMotionState>(Allocator.Temp);
                hash = GemSpatialHash.Build(
                    gemEntities, gemTransforms, gemGhosts, gemMotions, mapW, mapH, Allocator.Temp);
            }

            if (shipCount > 0)
                shipTransforms = _shipQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);

            var nearby = new NativeList<int>(64, Allocator.Temp);
            var seenScratch = new NativeHashSet<int>(64, Allocator.Temp);
            int gemInserts = 0;
            int alwaysInserts = 0;

            for (int ci = 0; ci < connCount; ci++)
            {
                int connectionId = connIds[ci].Value;
                if (connectionId <= 0)
                    continue;

                for (int ai = 0; ai < alwaysGhostCount; ai++)
                {
                    int ghostId = alwaysGhosts[ai].ghostId;
                    if (ghostId == 0)
                        continue;
                    if (set.TryAdd(new RelevantGhostForConnection(connectionId, ghostId), 1))
                        alwaysInserts++;
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

            LogIfDue(
                ref state,
                now,
                gemCount,
                gemInserts,
                connCount,
                alwaysCount,
                shipCount,
                alwaysInserts,
                alwaysIds,
                set.Count());

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

        void LogIfDue(
            ref SystemState state,
            double now,
            int gemCount,
            int gemInserts,
            int connCount,
            int alwaysCount,
            int shipCount,
            int alwaysInserts,
            int alwaysIds,
            int setCount)
        {
            if (now - _lastRelevancyLogElapsed < RelevancyLogIntervalSeconds)
                return;

            _lastRelevancyLogElapsed = now;
            int planetTags = _planetTagQuery.CalculateEntityCount();
            int planetGhosts = _planetGhostQuery.CalculateEntityCount();
            int planetIds = 0;
            if (planetGhosts > 0)
            {
                var ghosts = _planetGhostQuery.ToComponentDataArray<GhostInstance>(Allocator.Temp);
                for (int i = 0; i < ghosts.Length; i++)
                {
                    if (ghosts[i].ghostId != 0)
                        planetIds++;
                }

                ghosts.Dispose();
            }

            int collectionPrefabs = 0;
            if (SystemAPI.TryGetSingletonBuffer<GhostCollectionPrefab>(out var prefabs))
                collectionPrefabs = prefabs.Length;

            byte queryAssigned = _defaultRelevantQuery != default ? (byte)1 : (byte)0;
            DedicatedServerFileLog.Append(
                "relevancy",
                "planetTags=" + planetTags +
                " planetGhosts=" + planetGhosts +
                " planetIds=" + planetIds +
                " alwaysQuery=" + alwaysCount +
                " alwaysIds=" + alwaysIds +
                " alwaysInserts=" + alwaysInserts +
                " set=" + setCount +
                " collection=" + collectionPrefabs +
                " conns=" + connCount);

            // #region agent log
            TitanOrbitDebugSessionLog.Write(
                "F",
                "TitanOrbitGemGhostRelevancySystem.LogIfDue",
                "relevancy",
                "{\"planetTags\":" + planetTags +
                ",\"planetGhosts\":" + planetGhosts +
                ",\"planetIds\":" + planetIds +
                ",\"alwaysCount\":" + alwaysCount +
                ",\"alwaysIds\":" + alwaysIds +
                ",\"alwaysInserts\":" + alwaysInserts +
                ",\"setCount\":" + setCount +
                ",\"collection\":" + collectionPrefabs +
                ",\"ships\":" + shipCount +
                ",\"gems\":" + gemCount +
                ",\"gemInserts\":" + gemInserts +
                ",\"conns\":" + connCount + "}");
            // #endregion

            Debug.Log(
                "[GemRelevancy] planetTags=" + planetTags +
                " planetGhosts=" + planetGhosts +
                " planetIds=" + planetIds +
                " alwaysInserts=" + alwaysInserts +
                " set=" + setCount +
                " collection=" + collectionPrefabs +
                " connections=" + connCount);

            if (connCount <= 0)
                return;

            var rpc = new TitanOrbitGhostStreamDebugRpc
            {
                PlanetTags = planetTags,
                PlanetGhosts = planetGhosts,
                PlanetIds = planetIds,
                AlwaysInserts = alwaysInserts,
                SetCount = setCount,
                CollectionPrefabs = collectionPrefabs,
                RelevancyMode = (byte)GhostRelevancyMode.SetIsRelevant,
                QueryAssigned = queryAssigned,
            };
            var conns = _connectionQuery.ToEntityArray(Allocator.Temp);
            var ecb = new EntityCommandBuffer(Allocator.Temp);
            for (int i = 0; i < conns.Length; i++)
            {
                Entity req = ecb.CreateEntity();
                ecb.AddComponent(req, rpc);
                ecb.AddComponent(req, new SendRpcCommandRequest { TargetConnection = conns[i] });
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
            conns.Dispose();
        }
    }

    /// <summary>
    /// Client: latch server relevancy diagnostics onto the debug session (RPC path already works).
    /// World: ClientSimulation.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct TitanOrbitGhostStreamDebugClientSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            var ecb = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (rpc, entity) in SystemAPI.Query<RefRO<TitanOrbitGhostStreamDebugRpc>>()
                         .WithAll<ReceiveRpcCommandRequest>().WithEntityAccess())
            {
                var v = rpc.ValueRO;
                // #region agent log
                TitanOrbitDebugSessionLog.Write(
                    "F",
                    "TitanOrbitGhostStreamDebugClientSystem.OnUpdate",
                    "server-relevancy",
                    "{\"planetTags\":" + v.PlanetTags +
                    ",\"planetGhosts\":" + v.PlanetGhosts +
                    ",\"planetIds\":" + v.PlanetIds +
                    ",\"alwaysInserts\":" + v.AlwaysInserts +
                    ",\"setCount\":" + v.SetCount +
                    ",\"collection\":" + v.CollectionPrefabs +
                    ",\"mode\":" + v.RelevancyMode +
                    ",\"queryAssigned\":" + v.QueryAssigned + "}");
                // #endregion
                Debug.Log(
                    "[GhostStreamDebug] server planetTags=" + v.PlanetTags +
                    " planetGhosts=" + v.PlanetGhosts +
                    " planetIds=" + v.PlanetIds +
                    " alwaysInserts=" + v.AlwaysInserts +
                    " set=" + v.SetCount +
                    " collection=" + v.CollectionPrefabs);
                ecb.DestroyEntity(entity);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }
}
