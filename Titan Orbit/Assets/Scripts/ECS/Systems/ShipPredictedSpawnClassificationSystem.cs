using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using Unity.NetCode.LowLevel;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Owner-based classifier for a client predicted <see cref="ShipTag"/> (if one exists).
    /// <para>
    /// [TITAN-ORBIT] Join Team no longer Instantiates a ClientWorld predicted hull
    /// (2026-08-13). GhostReceive delivers the server ship. This system is a no-op unless
    /// something else predicted-spawns a ship (it should not). Kept so a leftover predicted
    /// ship list entry can still match by GhostOwner instead of a tight tick window.
    /// </para>
    /// World: ClientSimulation. Group: GhostSpawnClassificationSystemGroup.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(GhostSpawnClassificationSystemGroup))]
    [UpdateAfter(typeof(GhostSpawnClassificationSystem))]
    [CreateAfter(typeof(GhostCollectionSystem))]
    [CreateAfter(typeof(GhostReceiveSystem))]
    [BurstCompile]
    public partial struct ShipPredictedSpawnClassificationSystem : ISystem
    {
        /// <summary>Helper that reads GhostOwner out of the incoming snapshot payload.</summary>
        SnapshotDataLookupHelper _snapshotLookup;

        /// <summary>PredictedGhostSpawn list on the PredictedGhostSpawnList singleton.</summary>
        BufferLookup<PredictedGhostSpawn> _predictedSpawnLookup;

        /// <summary>GhostOwner on predicted hulls we Instantiated locally.</summary>
        ComponentLookup<GhostOwner> _ghostOwnerLookup;

        /// <summary>ShipTag — only classify player hulls, never bullets or other predicted ghosts.</summary>
        ComponentLookup<ShipTag> _shipTagLookup;

        /// <summary>
        /// Caches snapshot lookup + component lookups. Requires GhostCollection and the spawn map
        /// because SnapshotDataLookupHelper reads those singletons in its constructor.
        /// </summary>
        public void OnCreate(ref SystemState state)
        {
            // --- Host world has no client predicted-spawn pipeline ---
            // [NETCODE] Same early-out as GhostSpawnClassificationSystem. Titan Orbit uses separate
            // ClientWorld + ServerWorld (not a combined Host world), but keep the guard.
            if (state.WorldUnmanaged.IsHost())
            {
                state.Enabled = false;
                return;
            }

            _snapshotLookup = new SnapshotDataLookupHelper(
                ref state,
                SystemAPI.GetSingletonEntity<GhostCollection>(),
                SystemAPI.GetSingletonEntity<SpawnedGhostEntityMap>());
            _predictedSpawnLookup = state.GetBufferLookup<PredictedGhostSpawn>();
            _ghostOwnerLookup = state.GetComponentLookup<GhostOwner>(true);
            _shipTagLookup = state.GetComponentLookup<ShipTag>(true);

            state.RequireForUpdate<NetworkId>();
            state.RequireForUpdate<GhostCollection>();
            state.RequireForUpdate<GhostSpawnQueue>();
            state.RequireForUpdate<PredictedGhostSpawnList>();
        }

        /// <summary>
        /// Each GhostReceive frame: for unclassified OwnerPredicted ship snapshots whose owner is
        /// this client, adopt the matching predicted hull instead of Instantiates a second one.
        /// </summary>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            _snapshotLookup.Update(ref state);
            _predictedSpawnLookup.Update(ref state);
            _ghostOwnerLookup.Update(ref state);
            _shipTagLookup.Update(ref state);

            var job = new ClassifyOwnedShipJob
            {
                SnapshotLookupHelper = _snapshotLookup,
                PredictedSpawnListEntity = SystemAPI.GetSingletonEntity<PredictedGhostSpawnList>(),
                PredictedSpawnLookup = _predictedSpawnLookup,
                GhostOwnerLookup = _ghostOwnerLookup,
                ShipTagLookup = _shipTagLookup,
                LocalNetworkId = SystemAPI.GetSingleton<NetworkId>().Value,
            };
            state.Dependency = job.Schedule(state.Dependency);
        }

        /// <summary>
        /// Walks GhostSpawnBuffer + PredictedGhostSpawn and pairs this client's predicted hull
        /// with the server snapshot that has the same GhostOwner NetworkId.
        /// </summary>
        [WithAll(typeof(GhostSpawnQueue))]
        [BurstCompile]
        partial struct ClassifyOwnedShipJob : IJobEntity
        {
            /// <summary>Builds a snapshot reader for GhostOwner on the incoming spawn.</summary>
            public SnapshotDataLookupHelper SnapshotLookupHelper;

            /// <summary>Entity that holds the PredictedGhostSpawn buffer.</summary>
            public Entity PredictedSpawnListEntity;

            /// <summary>Client predicted spawns waiting for a server snapshot.</summary>
            public BufferLookup<PredictedGhostSpawn> PredictedSpawnLookup;

            /// <summary>GhostOwner on predicted hull entities.</summary>
            [ReadOnly] public ComponentLookup<GhostOwner> GhostOwnerLookup;

            /// <summary>ShipTag — ignore non-ship predicted spawns (projectiles, etc.).</summary>
            [ReadOnly] public ComponentLookup<ShipTag> ShipTagLookup;

            /// <summary>This client's NetCode id (must match GhostOwner on both sides).</summary>
            public int LocalNetworkId;

            /// <summary>
            /// One GhostSpawnQueue singleton. <paramref name="ghosts"/> is the pending spawn list;
            /// <paramref name="snapshotData"/> is the packed first snapshot for GetGhostOwner.
            /// </summary>
            public void Execute(
                DynamicBuffer<GhostSpawnBuffer> ghosts,
                in DynamicBuffer<SnapshotDataBuffer> snapshotData)
            {
                // --- Nothing to match ---
                if (LocalNetworkId <= 0)
                    return;

                var predictedList = PredictedSpawnLookup[PredictedSpawnListEntity];
                if (predictedList.Length == 0)
                    return;

                var snapshotLookup = SnapshotLookupHelper.CreateSnapshotBufferLookup();

                for (int i = 0; i < ghosts.Length; i++)
                {
                    ref var incoming = ref ghosts.ElementAt(i);

                    // --- Already classified, or not a predicted spawn ---
                    // [NETCODE] GhostSpawnClassificationSystem sets SpawnType = Predicted for the
                    // local owner's hull. Interpolated remotes must not steal our predicted entity.
                    if (incoming.HasClassifiedPredictedSpawn ||
                        incoming.PredictedSpawnEntity != Entity.Null ||
                        incoming.SpawnType != GhostSpawnBuffer.Type.Predicted)
                        continue;

                    // --- Incoming snapshot must be our NetworkId ---
                    if (!snapshotLookup.HasGhostOwner(incoming))
                        continue;
                    if (snapshotLookup.GetGhostOwner(incoming, snapshotData) != LocalNetworkId)
                        continue;

                    // --- Find the predicted hull we Instantiated for this owner ---
                    for (int p = 0; p < predictedList.Length; p++)
                    {
                        Entity predicted = predictedList[p].entity;
                        if (predicted == Entity.Null)
                            continue;
                        if (!ShipTagLookup.HasComponent(predicted))
                            continue;
                        if (!GhostOwnerLookup.HasComponent(predicted))
                            continue;
                        if (GhostOwnerLookup[predicted].NetworkId != LocalNetworkId)
                            continue;

                        // Same ghost type (ship prefab) — adopt this entity for the snapshot.
                        if (incoming.GhostType != predictedList[p].ghostType)
                            continue;

                        incoming.PredictedSpawnEntity = predicted;
                        incoming.HasClassifiedPredictedSpawn = true;
                        predictedList.RemoveAtSwapBack(p);
                        break;
                    }
                }
            }
        }
    }
}
