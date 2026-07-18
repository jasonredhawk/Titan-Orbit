
using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Assertions;
using Object = UnityEngine.Object;

namespace Unity.NetCode
{
    /// <summary>
    /// [TITAN-ORBIT] Cheap join-load counters written by <see cref="GhostSpawnSystem"/>.
    /// Loading UI can show Instantiates progress without scanning asteroid queries
    /// (<c>ToEntityArray</c> / chunk gathers Crash!!! on Windows late-join).
    /// Reset when leaving a session via <see cref="Reset"/>.
    /// </summary>
    public static class TitanOrbitJoinLoadCounters
    {
        /// <summary>CreateEntity placeholders created this join (all ghost types).</summary>
        public static int PlaceholdersCreatedSession { get; private set; }

        /// <summary>Successful delayed Instantiates this join (1/frame drain).</summary>
        public static int InstantiatesSession { get; private set; }

        /// <summary>Call when NetworkStreamInGame ends / join gate clears.</summary>
        public static void Reset()
        {
            PlaceholdersCreatedSession = 0;
            InstantiatesSession = 0;
        }

        /// <summary>Adds placeholder CreateEntity count for this frame.</summary>
        public static void AddPlaceholders(int count)
        {
            if (count > 0)
                PlaceholdersCreatedSession += count;
        }

        /// <summary>Adds successful Instantiates count for this frame.</summary>
        public static void AddInstantiates(int count)
        {
            if (count > 0)
                InstantiatesSession += count;
        }
    }

    /// <summary>
    /// <para>
    /// System responsible for spawning all the ghost entities for the client world.
    /// </para>
    /// <para>
    /// When a ghost snapshot is received from the server, the <see cref="GhostReceiveSystem"/> add a spawning request to the <see cref="GhostSpawnBuffer"/>.
    /// After the spawning requests has been classified (see <see cref="GhostSpawnClassificationSystem"/>),
    /// the <see cref="GhostSpawnSystem"/> start processing the spawning queue.
    /// </para>
    /// <para>
    /// Based on the spawning (<see cref="GhostSpawnBuffer.Type"/>), the requests are handled quite differently.
    /// </para>
    /// <para>When the mode is set to <see cref="GhostSpawnBuffer.Type.Interpolated"/>, the ghost creation is delayed
    /// until the <see cref="NetworkTime.InterpolationTick"/> match (or is greater) the actual spawning tick on the server.
    /// A temporary entity, holding the spawning information, the received snapshot data from the server, and tagged with the <see cref="PendingSpawnPlaceholder"/>
    /// is created. The entity will exists until the real ghost instance is spawned (or a de-spawn request has been received),
    /// and its sole purpose of receiving new incoming snapshots (even though they are not applied to the entity, since it is not a real ghost).
    /// </para>
    /// <para>
    /// When the mode is set to <see cref="GhostSpawnBuffer.Type.Predicted"/>, a new ghost instance in spawned immediately if the
    /// current simulated <see cref="NetworkTime.ServerTick"/> is greater or equals the spawning tick reported by the server.
    /// This condition is usually the norm, since the client timeline (the current simulated tick) should be ahead of the server.
    /// </para>
    /// <para>
    /// Otherwise, the ghost creation is delayed until the the <see cref="NetworkTime.ServerTick"/> is greater or equals the required spawning tick.
    /// Like to interpolated ghost, a temporary placeholder entity is created to hold spawning information and for holding new received snapshots.
    /// </para>
    /// </summary>
    [BurstCompile]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(GhostSpawnSystemGroup))]
    public partial struct GhostSpawnSystem : ISystem
    {
        // TITAN-ORBIT: static readonly (not const) so the marker string survives in player DLLs
        // for build verification.
        // v11 = CreateEntity-all (ghost map safe) + Instantiates 1/frame. Companion: client uses
        // managed LocalToWorld (Burst LTW disabled) — see TitanOrbitClientSafeLocalToWorldSystem.
        // v10 CreateEntity-cap+requeue → "baseline for a ghost we do not have" + Burst Crash!!! (2026-07-18).
        // v7 requeue without map registration had the same baseline failure mode.
        public static readonly string TitanOrbitGhostSpawnPatchId = "TO_GhostSpawn_v12_joinLoadCounters";

        // Touched in OnCreate so the linker cannot strip the marker.
        static char s_PatchIdTouch;

        struct DelayedSpawnGhost
        {
            public int ghostId;
            public int ghostType;
            public NetworkTick clientSpawnTick;
            public NetworkTick serverSpawnTick;
            public Entity oldEntity;
            public Entity predictedSpawnEntity;
        }
        NativeQueue<DelayedSpawnGhost> m_DelayedInterpolatedGhostSpawnQueue;
        NativeQueue<DelayedSpawnGhost> m_DelayedPredictedGhostSpawnQueue;

        EntityQuery m_InGameGroup;
        EntityQuery m_NetworkIdQuery;
        EntityQuery m_InstanceCount;

        /// <inheritdoc/>
        public void OnCreate(ref SystemState state)
        {
            if (state.WorldUnmanaged.IsHost())
            {
                state.Enabled = false;
                return;
            }

            // TITAN-ORBIT: Touch patch id so the string stays in the player Managed DLL for build checks.
            s_PatchIdTouch = TitanOrbitGhostSpawnPatchId[0];

            m_DelayedInterpolatedGhostSpawnQueue = new NativeQueue<DelayedSpawnGhost>(Allocator.Persistent);
            m_DelayedPredictedGhostSpawnQueue = new NativeQueue<DelayedSpawnGhost>(Allocator.Persistent);
            m_InGameGroup = state.GetEntityQuery(ComponentType.ReadOnly<NetworkStreamInGame>());
            m_NetworkIdQuery = state.GetEntityQuery(ComponentType.ReadOnly<NetworkId>(), ComponentType.Exclude<NetworkStreamRequestDisconnect>());
            m_InstanceCount = state.GetEntityQuery(ComponentType.ReadOnly<GhostInstance>(), ComponentType.ReadWrite<Simulate>(), ComponentType.Exclude<PendingSpawnPlaceholder>());

            var ent = state.EntityManager.CreateEntity();
            state.EntityManager.SetName(ent, "GhostSpawnQueue");
            state.EntityManager.AddComponentData(ent, default(GhostSpawnQueue));
            state.EntityManager.AddBuffer<GhostSpawnBuffer>(ent);
            state.EntityManager.AddBuffer<SnapshotDataBuffer>(ent);
            state.RequireForUpdate<GhostCollection>();
            state.RequireForUpdate<GhostSpawnQueue>();
        }

        /// <inheritdoc/>
        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
            state.CompleteDependency();
            m_DelayedPredictedGhostSpawnQueue.Dispose();
            m_DelayedInterpolatedGhostSpawnQueue.Dispose();
        }

        Entity SpawnGhost(ref SystemState state, Entity predictedEntity, int type, NativeArray<GhostCollectionPrefab> prefabs)
        {
            if (predictedEntity != Entity.Null && state.EntityManager.Exists(predictedEntity))
                return predictedEntity;
            var spawnedEntity = state.EntityManager.Instantiate(prefabs[type].GhostPrefab);
            return spawnedEntity;
        }
        /// <inheritdoc/>
        // TITAN-ORBIT: Intentionally NOT [BurstCompile].
        // Burst Instantiates of a large late-join map backlog hard-crashed the Windows player.
        // Spreading Instantiates across frames broke NetCode's ghost map (Editor: ObjectDisposedException,
        // "Ghost ID already added", "baseline for a ghost we do not have"). Managed Instantiates keep
        // stock spawn timing while avoiding the Burst native crash.
        public unsafe void OnUpdate(ref SystemState state)
        {
            state.Dependency.Complete(); // For ghost map access
            if (state.WorldUnmanaged.IsThinClient())
                return;
            var stateEntityManager = state.EntityManager;
            var networkTime = SystemAPI.GetSingleton<NetworkTime>();
            var interpolationTargetTick = networkTime.InterpolationTick;
            if (networkTime.InterpolationTickFraction < 1 && interpolationTargetTick.IsValid)
                interpolationTargetTick.Decrement();
            var predictionTargetTick = networkTime.ServerTick;
            var prefabsEntity = SystemAPI.GetSingletonEntity<GhostCollection>();
            var prefabs = stateEntityManager.GetBuffer<GhostCollectionPrefab>(prefabsEntity).ToNativeArray(Allocator.Temp);

            ref var ghostCount = ref SystemAPI.GetSingletonRW<GhostCount>().ValueRW;
            var ghostSpawnEntity = SystemAPI.GetSingletonEntity<GhostSpawnQueue>();
            var ghostSpawnBufferComponent = stateEntityManager.GetBuffer<GhostSpawnBuffer>(ghostSpawnEntity);
            var snapshotDataBufferComponent = stateEntityManager.GetBuffer<SnapshotDataBuffer>(ghostSpawnEntity);

            //Avoid adding new ghost if the stream is not in game
            if (m_InGameGroup.IsEmptyIgnoreFilter)
            {
                ghostSpawnBufferComponent.ResizeUninitialized(0);
                snapshotDataBufferComponent.ResizeUninitialized(0);
                m_DelayedPredictedGhostSpawnQueue.Clear();
                m_DelayedInterpolatedGhostSpawnQueue.Clear();
                return;
            }

            var ghostSpawnBuffer = ghostSpawnBufferComponent.ToNativeArray(Allocator.Temp);
            var snapshotDataBuffer = snapshotDataBufferComponent.ToNativeArray(Allocator.Temp);
            ghostSpawnBufferComponent.ResizeUninitialized(0);
            snapshotDataBufferComponent.ResizeUninitialized(0);

            var spawnedGhosts = new NativeList<SpawnedGhostMapping>(16, Allocator.Temp);
            var nonSpawnedGhosts = new NativeList<NonSpawnedGhostMapping>(16, Allocator.Temp);
            var ghostCollectionSingleton = SystemAPI.GetSingletonEntity<GhostCollection>();

            // TITAN-ORBIT v11: CreateEntity ALL placeholders this frame (stock / ghost-map safe).
            // Deferring CreateEntity left GhostIDs out of SpawnedGhostEntityMap → GhostReceive
            // "baseline for a ghost we do not have" → Burst Crash!!! (Player.log 2026-07-18 13:06).
            // Instantiates stay capped at 1/frame below. Burst LocalToWorld is disabled on clients
            // (TitanOrbitClientSafeLocalToWorldSystem) so CreateEntity/Instantiates floods do not AV.
            int placeholdersThisFrame = 0;

            for (int i = 0; i < ghostSpawnBuffer.Length; ++i)
            {
                var ghost = ghostSpawnBuffer[i];
                Entity entity = Entity.Null;

                // TITAN-ORBIT / stock NetCode: Instantiate + CreateEntity invalidate DynamicBuffer
                // handles. Re-fetch every iteration — caching GhostCollectionPrefabSerializer across
                // Instantiates throws ObjectDisposedException and can corrupt the spawned-ghost map.
                var ghostTypeCollection = stateEntityManager.GetBuffer<GhostCollectionPrefabSerializer>(ghostCollectionSingleton);

                // TITAN-ORBIT: guard invalid GhostType (hash mismatch / stale collection).
                if (ghost.GhostType < 0 || ghost.GhostType >= ghostTypeCollection.Length || ghost.GhostType >= prefabs.Length)
                {
                    ReportMissingPrefab(ref stateEntityManager);
                    continue;
                }

                if (ghost.SpawnType == GhostSpawnBuffer.Type.Interpolated)
                {
                    entity = AddToDelayedSpawnQueue(ref stateEntityManager, m_DelayedInterpolatedGhostSpawnQueue, ghost, ref snapshotDataBuffer, ghostTypeCollection);
                    placeholdersThisFrame++;
                    nonSpawnedGhosts.Add(new NonSpawnedGhostMapping { ghostId = ghost.GhostID, entity = entity });
                }
                else if (ghost.SpawnType == GhostSpawnBuffer.Type.Predicted)
                {
                    // TITAN-ORBIT: Always delay predicted Instantiates (placeholder + delayed Instantiates).
                    // CreateEntity the placeholder THIS frame so the ghost id is in SpawnedGhostEntityMap.
                    entity = AddToDelayedSpawnQueue(ref stateEntityManager, m_DelayedPredictedGhostSpawnQueue, ghost, ref snapshotDataBuffer, ghostTypeCollection);
                    placeholdersThisFrame++;
                    nonSpawnedGhosts.Add(new NonSpawnedGhostMapping { ghostId = ghost.GhostID, entity = entity });
                }
            }

            if (placeholdersThisFrame > 0)
                TitanOrbitJoinLoadCounters.AddPlaceholders(placeholdersThisFrame);

            if (placeholdersThisFrame >= 16)
            {
                UnityEngine.Debug.Log(
                    "[TO_GhostSpawn] Created " + placeholdersThisFrame +
                    " placeholders this frame (Instantiates capped at 1/frame; client uses managed LTW).");
            }

            var netDebug = SystemAPI.GetSingleton<NetDebug>();
            ref var ghostEntityMap = ref SystemAPI.GetSingletonRW<SpawnedGhostEntityMap>().ValueRW;
            ghostEntityMap.AddClientNonSpawnedGhosts(nonSpawnedGhosts.AsArray(), netDebug);
            ghostEntityMap.AddClientSpawnedGhosts(spawnedGhosts.AsArray(), netDebug);

            spawnedGhosts.Clear();
            // TITAN-ORBIT: Cap delayed Instantiates per frame.
            // Late-join creates many PendingSpawnPlaceholder entities over several snapshots, but once
            // InterpolationTick catches their spawn ticks, stock NetCode drains the entire queue in
            // ONE frame. That Instantiates flood hard-crashes the Windows player (Crash!!! in
            // TrySpawnFromDelayedQueue). Placeholders stay valid and keep receiving snapshots — we
            // only defer the real Instantiate. Break without Dequeue so nothing is dropped.
            // Keep this at 1: even 4 Instantiates/frame still tripped Burst LocalToWorldSystem
            // (ComputeWorldSpaceLocalToWorldJob) on Windows late-join with ~500 asteroids.
            const int k_MaxDelayedInstantiatesPerFrame = 1;
            int delayedInstantiatesThisFrame = 0;
            int successfulInstantiatesThisFrame = 0;

            while (delayedInstantiatesThisFrame < k_MaxDelayedInstantiatesPerFrame &&
                   m_DelayedInterpolatedGhostSpawnQueue.Count > 0 &&
                   !m_DelayedInterpolatedGhostSpawnQueue.Peek().clientSpawnTick.IsNewerThan(interpolationTargetTick))
            {
                var ghost = m_DelayedInterpolatedGhostSpawnQueue.Dequeue();
                if (TrySpawnFromDelayedQueue(ref state, ghost, GhostSpawnBuffer.Type.Interpolated, prefabs, ghostCollectionSingleton, out var entity))
                {
                    spawnedGhosts.Add(new SpawnedGhostMapping { ghost = new SpawnedGhost { ghostId = ghost.ghostId, spawnTick = ghost.serverSpawnTick }, entity = entity, previousEntity = ghost.oldEntity });
                    successfulInstantiatesThisFrame++;
                }
                delayedInstantiatesThisFrame++;
            }
            while (delayedInstantiatesThisFrame < k_MaxDelayedInstantiatesPerFrame &&
                   m_DelayedPredictedGhostSpawnQueue.Count > 0 &&
                   !m_DelayedPredictedGhostSpawnQueue.Peek().clientSpawnTick.IsNewerThan(predictionTargetTick))
            {
                var ghost = m_DelayedPredictedGhostSpawnQueue.Dequeue();
                if (TrySpawnFromDelayedQueue(ref state, ghost, GhostSpawnBuffer.Type.Predicted, prefabs, ghostCollectionSingleton, out var entity))
                {
                    spawnedGhosts.Add(new SpawnedGhostMapping { ghost = new SpawnedGhost { ghostId = ghost.ghostId, spawnTick = ghost.serverSpawnTick }, entity = entity, previousEntity = ghost.oldEntity });
                    successfulInstantiatesThisFrame++;
                }
                delayedInstantiatesThisFrame++;
            }

            if (successfulInstantiatesThisFrame > 0)
                TitanOrbitJoinLoadCounters.AddInstantiates(successfulInstantiatesThisFrame);

            ghostEntityMap.UpdateClientSpawnedGhosts(spawnedGhosts.AsArray(), netDebug);

            ghostCount.m_GhostCompletionCount[2] = m_InstanceCount.CalculateEntityCountWithoutFiltering();
        }

        /// <summary>
        /// TITAN-ORBIT: Write unprocessed GhostSpawnBuffer entries (and their snapshot bytes) back
        /// onto the GhostSpawnQueue singleton so the next frame can continue at the placeholder cap.
        /// GhostReceive appends new spawns; we remap <see cref="GhostSpawnBuffer.DataOffset"/> to
        /// the compacted byte layout we write here.
        /// </summary>
        static unsafe void RequeueDeferredGhostSpawns(
            ref EntityManager entityManager,
            Entity ghostSpawnEntity,
            NativeArray<GhostSpawnBuffer> ghostSpawnBuffer,
            NativeArray<SnapshotDataBuffer> snapshotDataBuffer,
            int deferFromIndex,
            Entity ghostCollectionSingleton)
        {
            // --- Guards ---
            if (deferFromIndex < 0 || deferFromIndex >= ghostSpawnBuffer.Length)
                return;

            var ghostTypeCollection = entityManager.GetBuffer<GhostCollectionPrefabSerializer>(ghostCollectionSingleton);
            var outSpawns = entityManager.GetBuffer<GhostSpawnBuffer>(ghostSpawnEntity);
            var outSnapshots = entityManager.GetBuffer<SnapshotDataBuffer>(ghostSpawnEntity);

            // OnUpdate cleared both buffers before the spawn loop; we refill only deferred leftovers.
            // GhostReceive will Append more entries later this frame or next.
            byte* srcBase = (byte*)snapshotDataBuffer.GetUnsafeReadOnlyPtr();
            int srcBytes = snapshotDataBuffer.Length;

            for (int i = deferFromIndex; i < ghostSpawnBuffer.Length; ++i)
            {
                var ghost = ghostSpawnBuffer[i];

                // Skip corrupt GhostType — same as the main loop ReportMissingPrefab path.
                if (ghost.GhostType < 0 || ghost.GhostType >= ghostTypeCollection.Length)
                    continue;

                int snapshotSize = ghostTypeCollection[ghost.GhostType].SnapshotSize;
                // GhostReceive packs: SnapshotSize bytes + DynamicDataSize bytes at DataOffset.
                int byteCount = snapshotSize + (int)ghost.DynamicDataSize;
                if (byteCount < 0 || ghost.DataOffset < 0 || ghost.DataOffset + byteCount > srcBytes)
                    continue;

                int newOffset = outSnapshots.Length;
                outSnapshots.ResizeUninitialized(newOffset + byteCount);
                UnsafeUtility.MemCpy(
                    (byte*)outSnapshots.GetUnsafePtr() + newOffset,
                    srcBase + ghost.DataOffset,
                    byteCount);

                ghost.DataOffset = newOffset;
                outSpawns.Add(ghost);
            }
        }

        void ConfigurePrespawnGhost(ref EntityManager entityManager, Entity entity, in GhostSpawnBuffer ghost)
        {
            if(ghost.PrespawnIndex == -1)
                throw new InvalidOperationException("respawning a pre-spawned ghost requires a valid prespawn index");
            entityManager.AddComponentData(entity, new PreSpawnedGhostIndex {Value = ghost.PrespawnIndex});
            entityManager.AddSharedComponent(entity, new SceneSection
            {
                SceneGUID = ghost.SceneGUID,
                Section = ghost.SectionIndex
            });
        }

        void ReportMissingPrefab(ref EntityManager entityManager)
        {
            SystemAPI.GetSingleton<NetDebug>().LogError($"Trying to spawn with a prefab which is not loaded");

            // TODO: Use entityManager.AddComponentData(EntityQuery, T); when it's available.
            using var entities = m_NetworkIdQuery.ToEntityArray(Allocator.Temp);
            foreach (var entity in entities)
            {
                entityManager.AddComponentData(entity, new NetworkStreamRequestDisconnect {Reason = NetworkStreamDisconnectReason.BadProtocolVersion});
            }
        }

        unsafe Entity AddToDelayedSpawnQueue(ref EntityManager entityManager, NativeQueue<DelayedSpawnGhost> delayedSpawnQueue, in GhostSpawnBuffer ghost, ref NativeArray<SnapshotDataBuffer> snapshotDataBuffer, in DynamicBuffer<GhostCollectionPrefabSerializer> ghostTypeCollection)
        {
            var snapshotSize = ghostTypeCollection[ghost.GhostType].SnapshotSize;
            bool hasBuffers = ghostTypeCollection[ghost.GhostType].NumBuffers > 0;

            var entity = entityManager.CreateEntity();
#if !DOTS_DISABLE_DEBUG_NAMES
            entityManager.SetName(entity, $"GHOST-PLACEHOLDER-{ghost.GhostType}");
#endif
            entityManager.AddComponentData(entity, new GhostInstance { ghostId = ghost.GhostID, ghostType = ghost.GhostType, spawnTick = ghost.ServerSpawnTick });
            entityManager.AddComponent<PendingSpawnPlaceholder>(entity);
            if (PrespawnHelper.IsPrespawnGhostId(ghost.GhostID))
                ConfigurePrespawnGhost(ref entityManager, entity, ghost);

            var newBuffer = entityManager.AddBuffer<SnapshotDataBuffer>(entity);
            newBuffer.ResizeUninitialized(snapshotSize * GhostSystemConstants.SnapshotHistorySize);
            var snapshotData = (byte*)newBuffer.GetUnsafePtr();
            //Add also the SnapshotDynamicDataBuffer if the entity has buffers to copy the dynamic contents
            if (hasBuffers)
                entityManager.AddBuffer<SnapshotDynamicDataBuffer>(entity);
            entityManager.AddComponentData(entity, new SnapshotData { SnapshotSize = snapshotSize, LatestIndex = 0 });

            delayedSpawnQueue.Enqueue(new GhostSpawnSystem.DelayedSpawnGhost { ghostId = ghost.GhostID, ghostType = ghost.GhostType, clientSpawnTick = ghost.ClientSpawnTick, serverSpawnTick = ghost.ServerSpawnTick, oldEntity = entity, predictedSpawnEntity = ghost.PredictedSpawnEntity });

            UnsafeUtility.MemClear(snapshotData, snapshotSize * GhostSystemConstants.SnapshotHistorySize);
            UnsafeUtility.MemCpy(snapshotData, (byte*)snapshotDataBuffer.GetUnsafeReadOnlyPtr() + ghost.DataOffset, snapshotSize);
            if (hasBuffers)
            {
                //Resize and copy the associated dynamic buffer snapshot data
                var snapshotDynamicBuffer = entityManager.GetBuffer<SnapshotDynamicDataBuffer>(entity);
                var dynamicDataCapacity = SnapshotDynamicBuffersHelper.CalculateBufferCapacity(ghost.DynamicDataSize, out var _);
                snapshotDynamicBuffer.ResizeUninitialized((int)dynamicDataCapacity);
                var dynamicSnapshotData = (byte*)snapshotDynamicBuffer.GetUnsafePtr();
                if (dynamicSnapshotData == null)
                    throw new InvalidOperationException("snapshot dynamic data buffer not initialized but ghost has dynamic buffer contents");

                // Update the dynamic data header (uint[GhostSystemConstants.SnapshotHistorySize)]) by writing the used size for the current slot
                // (for new spawned entity is 0). Is un-necessary to initialize all the header slots to 0 since that information is only used
                // for sake of delta compression and, because that depend on the acked tick, only initialized and relevant slots are accessed in general.
                // For more information about the layout, see SnapshotData.cs.
                ((uint*)dynamicSnapshotData)[0] = ghost.DynamicDataSize;
                var headerSize = SnapshotDynamicBuffersHelper.GetHeaderSize();
                UnsafeUtility.MemCpy(dynamicSnapshotData + headerSize, (byte*)snapshotDataBuffer.GetUnsafeReadOnlyPtr() + ghost.DataOffset + snapshotSize, ghost.DynamicDataSize);
            }

            return entity;
        }

        unsafe bool TrySpawnFromDelayedQueue(ref SystemState state, in DelayedSpawnGhost ghost, GhostSpawnBuffer.Type spawnType, in NativeArray<GhostCollectionPrefab> prefabs, Entity ghostCollectionSingleton, out Entity entity)
        {
            entity = Entity.Null;
            var entityManager = state.EntityManager;

            // TITAN-ORBIT: bounds-check before Instantiates (OOB → native Crash!!! on Windows).
            if (ghost.ghostType < 0 || ghost.ghostType >= prefabs.Length)
            {
                ReportMissingPrefab(ref entityManager);
                return false;
            }

            // TODO: this could allow some time for the prefab to load before giving an error
            if (prefabs[ghost.ghostType].GhostPrefab == Entity.Null)
            {
                ReportMissingPrefab(ref entityManager);
                return false;
            }
            //Entity has been destroyed meawhile it was in the queue
            if (!entityManager.HasComponent<GhostInstance>(ghost.oldEntity))
                return false;

            // Spawn actual entity
            entity = SpawnGhost(ref state, ghost.predictedSpawnEntity, ghost.ghostType, prefabs);
            if (!entityManager.Exists(entity))
                return false;
            if(entityManager.HasComponent<PredictedGhostSpawnRequest>(entity))
                entityManager.RemoveComponent<PredictedGhostSpawnRequest>(entity);
            if (entityManager.HasComponent<GhostPrefabMetaData>(entity))
            {
                var meta = entityManager.GetComponentData<GhostPrefabMetaData>(entity);
                // TITAN-ORBIT: null/uncreated blob → skip disable lists (avoids native AV).
                if (meta.Value.IsCreated)
                {
                    ref var toRemove = ref meta.Value.Value.DisableOnInterpolatedClient;
                    if (spawnType == GhostSpawnBuffer.Type.Predicted)
                        toRemove = ref meta.Value.Value.DisableOnPredictedClient;
                    var linkedEntityGroup = entityManager.GetBuffer<LinkedEntityGroup>(entity).ToNativeArray(Allocator.Temp);
                    //Need copy because removing component will invalidate the buffer pointer, since introduce structural changes
                    for (int rm = 0; rm < toRemove.Length; ++rm)
                    {
                        int entityIndex = toRemove[rm].EntityIndex;
                        // TITAN-ORBIT: OOB EntityIndex → native crash on RemoveComponent.
                        if (entityIndex < 0 || entityIndex >= linkedEntityGroup.Length)
                            continue;
                        var compType = ComponentType.ReadWrite(TypeManager.GetTypeIndexFromStableTypeHash(toRemove[rm].StableHash));
                        entityManager.RemoveComponent(linkedEntityGroup[entityIndex].Value, compType);
                    }
                }
            }
            entityManager.SetComponentData(entity, entityManager.GetComponentData<SnapshotData>(ghost.oldEntity));
            if (PrespawnHelper.IsPrespawnGhostId(ghost.ghostId))
            {
                entityManager.AddComponentData(entity, entityManager.GetComponentData<PreSpawnedGhostIndex>(ghost.oldEntity));
                entityManager.AddSharedComponent(entity, entityManager.GetSharedComponent<SceneSection>(ghost.oldEntity));
            }
            var ghostComponentData = entityManager.GetComponentData<GhostInstance>(ghost.oldEntity);
            entityManager.SetComponentData(entity, ghostComponentData);

            // TITAN-ORBIT: Safe snapshot buffer copy.
            // Stock code calls ResizeUninitialized on the Instantiated prefab's SnapshotDataBuffer.
            // On Windows players that FreeTracked the Instantiated buffer's stale heap pointer and
            // hard-crashed (Crash!!! in TrySpawnFromDelayedQueue → BufferHeader.SetCapacity).
            // Rebuild the buffer component so capacity starts clean, then copy from the placeholder.
            if (!TryCopySnapshotBufferSafe(ref entityManager, ghost.oldEntity, entity))
            {
                entityManager.DestroyEntity(entity);
                return false;
            }

            // TITAN-ORBIT: re-fetch after Instantiates; also bounds-check collection length.
            var ghostTypeCollection = entityManager.GetBuffer<GhostCollectionPrefabSerializer>(ghostCollectionSingleton);
            if (ghost.ghostType < 0 || ghost.ghostType >= ghostTypeCollection.Length)
            {
                entityManager.DestroyEntity(ghost.oldEntity);
                return true;
            }
            bool hasBuffers = ghostTypeCollection[ghost.ghostType].NumBuffers > 0;
            if (hasBuffers)
            {
                if (!TryCopyDynamicSnapshotBufferSafe(ref entityManager, ghost.oldEntity, entity))
                {
                    entityManager.DestroyEntity(entity);
                    return false;
                }
            }
            entityManager.DestroyEntity(ghost.oldEntity);

            return true;
        }

        /// <summary>
        /// TITAN-ORBIT: Copy SnapshotDataBuffer from placeholder → real ghost without ResizeUninitialized
        /// on a possibly-stale Instantiated buffer header (FreeTracked crash).
        /// </summary>
        static unsafe bool TryCopySnapshotBufferSafe(ref EntityManager entityManager, Entity oldEntity, Entity newEntity)
        {
            if (!entityManager.HasBuffer<SnapshotDataBuffer>(oldEntity))
                return false;

            var oldBuffer = entityManager.GetBuffer<SnapshotDataBuffer>(oldEntity);
            int len = oldBuffer.Length;
            // Sanity: snapshot payloads are small; absurd lengths mean corrupt placeholder state.
            const int k_MaxSnapshotBytes = 1 << 20; // 1 MiB
            if (len < 0 || len > k_MaxSnapshotBytes)
                return false;

            var temp = len > 0 ? new NativeArray<byte>(len, Allocator.Temp) : default;
            if (len > 0)
                UnsafeUtility.MemCpy(temp.GetUnsafePtr(), oldBuffer.GetUnsafeReadOnlyPtr(), len);

            if (entityManager.HasBuffer<SnapshotDataBuffer>(newEntity))
                entityManager.RemoveComponent<SnapshotDataBuffer>(newEntity);
            var newBuffer = entityManager.AddBuffer<SnapshotDataBuffer>(newEntity);
            if (len > 0)
            {
                newBuffer.ResizeUninitialized(len);
                UnsafeUtility.MemCpy(newBuffer.GetUnsafePtr(), temp.GetUnsafeReadOnlyPtr(), len);
                temp.Dispose();
            }

            return true;
        }

        /// <summary>
        /// TITAN-ORBIT: Same safe rebuild path for SnapshotDynamicDataBuffer.
        /// </summary>
        static unsafe bool TryCopyDynamicSnapshotBufferSafe(ref EntityManager entityManager, Entity oldEntity, Entity newEntity)
        {
            if (!entityManager.HasBuffer<SnapshotDynamicDataBuffer>(oldEntity))
                return true; // nothing to copy

            var oldDynamicBuffer = entityManager.GetBuffer<SnapshotDynamicDataBuffer>(oldEntity);
            int len = oldDynamicBuffer.Length;
            const int k_MaxSnapshotBytes = 1 << 20;
            if (len < 0 || len > k_MaxSnapshotBytes)
                return false;

            var temp = len > 0 ? new NativeArray<byte>(len, Allocator.Temp) : default;
            if (len > 0)
                UnsafeUtility.MemCpy(temp.GetUnsafePtr(), oldDynamicBuffer.GetUnsafeReadOnlyPtr(), len);

            if (entityManager.HasBuffer<SnapshotDynamicDataBuffer>(newEntity))
                entityManager.RemoveComponent<SnapshotDynamicDataBuffer>(newEntity);
            var newDynamicBuffer = entityManager.AddBuffer<SnapshotDynamicDataBuffer>(newEntity);
            if (len > 0)
            {
                newDynamicBuffer.ResizeUninitialized(len);
                UnsafeUtility.MemCpy(newDynamicBuffer.GetUnsafePtr(), temp.GetUnsafeReadOnlyPtr(), len);
                temp.Dispose();
            }

            return true;
        }
    }

    internal struct PendingGameObjectSpawn : IComponentData
    {
        public bool ShouldBeActive;
    }

    /// <summary>
    /// In order to get ghost state accessible from GameObject's awake, we need the awake to execute after snapshot data has been deserialized in GhostUpdateSystem
    /// </summary>
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    [UpdateAfter(typeof(GhostUpdateSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)] // This isn't on thin clients since GhostUpdateSystem isn't on thin clients either.
    internal partial class GhostGameObjectSpawnSystem : SystemBase
    {
        EntityQuery m_PendingSpawnQuery;

        protected override void OnCreate()
        {
            if (this.World.Unmanaged.IsHost())
            {
                this.Enabled = false;
                return;
            }

            using var builder = new EntityQueryBuilder(Allocator.Temp);
            m_PendingSpawnQuery = this.EntityManager.CreateEntityQuery(builder.WithAll<PendingGameObjectSpawn, GhostInstance>());
            RequireForUpdate(m_PendingSpawnQuery);
        }

        protected override void OnUpdate()
        {

            // Design note: we could potentially move the burstable part of this system to the spawn system, with the entities spawn logic. But it'd make potential GO batching a bit harder and it'd also mean you'd get non-initialized GOs present for a few systems before they are initialized later in this system. If there's custom user systems introduced in between, this could be weird.
#if UNITY_6000_3_OR_NEWER // Required to use GameObject bridge with EntityID
            using var pendingEntities = m_PendingSpawnQuery.ToEntityArray(Allocator.Temp);
            using var ghostInstances = m_PendingSpawnQuery.ToComponentDataArray<GhostInstance>(Allocator.Temp);
            using var pendingSpawn = m_PendingSpawnQuery.ToComponentDataArray<PendingGameObjectSpawn>(Allocator.Temp);
            using NativeList<EntityId> objectsToReenable = new NativeList<EntityId>(pendingEntities.Length, Allocator.Temp);
            var prefabsEntity = SystemAPI.GetSingletonEntity<GhostCollection>();
            var prefabs = EntityManager.GetBuffer<GhostCollectionPrefab>(prefabsEntity).ToNativeArray(Allocator.Temp);
            var links = new NativeArray<GhostEntityMapping.EntityLink>(pendingEntities.Length, Allocator.Temp);
            var allGOs = new NativeArray<EntityId>(pendingEntities.Length, Allocator.Temp);

            using var instances = new NativeArray<EntityId>(1, Allocator.Temp);
            using var transformInstances = new NativeArray<EntityId>(1, Allocator.Temp);
            using var prefabInstancesDisposable = new NativeArray<EntityId>(1, Allocator.Temp);

            for (int i = 0; i < pendingEntities.Length; i++)
            {
                var spawnedEntity = pendingEntities[i];
                var type = ghostInstances[i].ghostType;
                var prefabEntity = prefabs[type].GhostPrefab;
                var goPrefab = EntityManager.GetComponentData<GhostGameObjectLink>(prefabEntity).AssociatedGameObject;
                var prefabId = prefabInstancesDisposable;
                prefabId[0] = goPrefab;
                // setting the prefab inactive in order to control when the GameObject's awake gets called. This way we can delay it until we've injected it with
                // the appropriate world and entity
                // TODO-release@potentialOptim batch these GameObject calls if we see this system is too heavy weight
                // TODO-release@potentialOptim we can also potentially burst this
                GameObject.SetGameObjectsActive(prefabId, false); // TODO-release@potentialOptim We can potentially apply the setactive only once per prefab (have a bool to mark them as "already disabled" and add them in a list to be reenabled at the end of the system OnUpdate). But the set active is already pretty quick, especially compared to the actual GameObject Instantiate.
                GameObject.InstantiateGameObjects(goPrefab, 1, instances, transformInstances);
                var shouldPrefabBeActive = EntityManager.GetComponentData<PendingGameObjectSpawn>(prefabEntity).ShouldBeActive;
                GameObject.SetGameObjectsActive(prefabId, shouldPrefabBeActive);

                var link = new GhostGameObjectLink(instances[0], transformInstances[0]);

                // Inject linked entity and world in GO and do initialization steps.
                // This also executes even if users don't plan to reactivate the GO, to make sure that netcode logic can still run on a valid entity
                // There's a symmetric release in the Despawn system
                var newLink = GhostEntityMapping.AcquireEntityReferenceGameObject(link.AssociatedGameObject, link.AssociatedTransform, goPrefab, autoWorld: this.World.Unmanaged, injectedEntity: pendingEntities[i]);
                links[i] = newLink;
                allGOs[i] = instances[0];

                EntityManager.SetComponentData(spawnedEntity, link);

                if (pendingSpawn[i].ShouldBeActive)
                {
                    objectsToReenable.Add(link.AssociatedGameObject);
                }
            }

            // managed
            {
                List<Object> allGOsAsObjects = new();
                Resources.EntityIdsToObjectList(allGOs, allGOsAsObjects);
                for (int i = 0; i < pendingEntities.Length; i++)
                {
                    var ghost = ((GameObject)allGOsAsObjects[i]).GetComponent<GhostAdapter>();
                    var ghostInfoComponent = EntityManager.GetComponentData<GhostGameObjectLink>(pendingEntities[i]);
                    ghostInfoComponent.GhostAdapterId = ghost.GetEntityId();
                    EntityManager.SetComponentData(pendingEntities[i], ghostInfoComponent);
                    ghost.InitializeRuntimeGhostBehaviours(links[i], withInitialValue: false); // withInitialValue=false since this is a spawn from the network, we already have values in ECS components.
                }
            }

            // This needs to execute in this system, since we want state to be accessible in Awake (and so this system needs to execute after GhostUpdateSystem)
            GameObject.SetGameObjectsActive(objectsToReenable.AsArray(), true); // Triggers the Awake

            EntityManager.RemoveComponent<PendingGameObjectSpawn>(m_PendingSpawnQuery);
#else
            throw new InvalidOperationException("Sanity check failed, GameObject instantiation isn't supported, shouldn't be here");
#endif
        }

        public static bool TryGetAutomaticWorld(out WorldUnmanaged world)
        {
            if (Netcode.IsClientRole && Netcode.Client.NetworkTime.IsInPredictionLoop)
            {
                Assert.IsTrue(ClientServerBootstrap.ClientWorld != null && ClientServerBootstrap.ClientWorld.IsCreated, "sanity check failed, trying to spawn a client ghost but with invalid client world");
                world = ClientServerBootstrap.ClientWorld.Unmanaged;
                return true;
            }

            if (ClientServerBootstrap.ServerWorld != null && ClientServerBootstrap.ServerWorld.IsCreated)
            {
                world = ClientServerBootstrap.ServerWorld.Unmanaged;
                return true;
            }
            world = default; // this might be a valid case if the world is already linked on the ghost
            return false;
        }
    }
}
