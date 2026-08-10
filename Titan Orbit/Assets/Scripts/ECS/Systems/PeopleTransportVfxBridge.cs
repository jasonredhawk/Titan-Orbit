using System.Collections.Concurrent;
using System.Collections.Generic;
using Unity.Mathematics;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Cross-world people-transport VFX queues (spawn + authoritative pose/end).
    /// <para>
    /// Server enqueues on dispatch / pose sync (local host). Client RPC handlers enqueue for
    /// dedicated clients. <see cref="Game.PeopleTransportVfxDriver"/> is the sole consumer.
    /// Sequence dedupe prevents host double-spawn (queue + RPC). Pose updates are not deduped —
    /// latest wins.
    /// </para>
    /// </summary>
    public static class PeopleTransportVfxBridge
    {
        /// <summary>One cosmetic flight spawn request (server-baked endpoints).</summary>
        public struct SpawnRequest
        {
            public uint Sequence;
            public float3 SpawnPosition;
            public float3 TargetPosition;
            public float3 Velocity;
            public float CruiseSpeed;
            public float Amount;
            public int TargetShipNetworkId;
            /// <summary>Load source planet — used for planet-avoidance floating text.</summary>
            public int SourcePlanetId;
            /// <summary>Unload destination planet id (0 for load).</summary>
            public int TargetPlanetId;
            public byte IsLoad;
            public byte Team;
        }

        /// <summary>Authoritative server pose or end-of-life for one sequenced flight.</summary>
        public struct PoseUpdate
        {
            public uint Sequence;
            public float3 Position;
            public float3 Velocity;
            public byte Status;
        }

        static readonly ConcurrentQueue<SpawnRequest> SpawnQueue = new ConcurrentQueue<SpawnRequest>();
        static readonly ConcurrentQueue<PoseUpdate> PoseQueue = new ConcurrentQueue<PoseUpdate>();
        static readonly HashSet<uint> SeenSequences = new HashSet<uint>();
        static readonly Queue<uint> SeenOrder = new Queue<uint>(64);
        static uint s_NextSequence = 1;
        const int MaxSeen = 256;

        /// <summary>Allocates the next spawn sequence id (server only).</summary>
        public static uint NextSequence() => s_NextSequence++;

        /// <summary>
        /// Enqueues a spawn if this sequence was not already queued/consumed.
        /// Returns false when duplicate (host queue + RPC).
        /// </summary>
        public static bool TryEnqueue(in SpawnRequest request)
        {
            if (request.Sequence != 0 && !RememberSequence(request.Sequence))
                return false;
            SpawnQueue.Enqueue(request);
            return true;
        }

        /// <summary>Driver: take next pending spawn.</summary>
        public static bool TryDequeue(out SpawnRequest request) => SpawnQueue.TryDequeue(out request);

        /// <summary>
        /// Enqueues a server pose / end update. Always accepted (no dedupe) so the latest
        /// authoritative position reaches the VFX driver.
        /// </summary>
        public static void EnqueuePose(in PoseUpdate update)
        {
            if (update.Sequence == 0)
                return;
            PoseQueue.Enqueue(update);
        }

        /// <summary>Driver: take next pending pose / end update.</summary>
        public static bool TryDequeuePose(out PoseUpdate update) => PoseQueue.TryDequeue(out update);

        /// <summary>Clears pending spawns/poses when leaving a match.</summary>
        public static void Clear()
        {
            while (SpawnQueue.TryDequeue(out _)) { }
            while (PoseQueue.TryDequeue(out _)) { }
            SeenSequences.Clear();
            SeenOrder.Clear();
        }

        static bool RememberSequence(uint sequence)
        {
            if (!SeenSequences.Add(sequence))
                return false;
            SeenOrder.Enqueue(sequence);
            while (SeenOrder.Count > MaxSeen)
                SeenSequences.Remove(SeenOrder.Dequeue());
            return true;
        }
    }
}
