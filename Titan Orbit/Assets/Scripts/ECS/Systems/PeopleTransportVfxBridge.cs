using System.Collections.Concurrent;
using System.Collections.Generic;
using Unity.Mathematics;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Cross-world people-transport VFX spawn queue.
    /// <para>
    /// Server enqueues on dispatch (local host). Client RPC handler enqueues for dedicated clients.
    /// <see cref="Game.PeopleTransportVfxDriver"/> is the sole consumer — it Instantiates GameObjects
    /// and animates them. Sequence dedupe prevents host double-spawn (queue + RPC).
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
            /// <summary>Load source planet — used when the ship leaves orbit and the sphere flies home.</summary>
            public int SourcePlanetId;
            /// <summary>Unload destination planet id (0 for load).</summary>
            public int TargetPlanetId;
            public byte IsLoad;
            public byte Team;
        }

        static readonly ConcurrentQueue<SpawnRequest> Queue = new ConcurrentQueue<SpawnRequest>();
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
            Queue.Enqueue(request);
            return true;
        }

        /// <summary>Driver: take next pending spawn.</summary>
        public static bool TryDequeue(out SpawnRequest request) => Queue.TryDequeue(out request);

        /// <summary>Clears pending spawns when leaving a match.</summary>
        public static void Clear()
        {
            while (Queue.TryDequeue(out _)) { }
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
