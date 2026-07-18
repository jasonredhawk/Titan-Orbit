using System.Collections.Concurrent;
using Unity.Mathematics;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// In-process spawn queue from ServerWorld → ClientWorld for local host people-transport VFX.
    /// Dedicated online clients use <see cref="PeopleTransportSpawnRpc"/> instead (this queue stays
    /// empty on those machines). Deduped with RPC by <see cref="SpawnRequest.Sequence"/>.
    /// </summary>
    public static class PeopleTransportVfxBridge
    {
        /// <summary>One cosmetic flight spawn request.</summary>
        public struct SpawnRequest
        {
            public uint Sequence;
            public float3 SpawnPosition;
            public float3 Velocity;
            public float CruiseSpeed;
            public float Amount;
            public int TargetShipNetworkId;
            public int SourcePlanetId;
            public int TargetPlanetId;
            public byte IsLoad;
            public byte Team;
        }

        /// <summary>Thread-safe queue — server systems enqueue; client presentation drains.</summary>
        static readonly ConcurrentQueue<SpawnRequest> Queue = new ConcurrentQueue<SpawnRequest>();

        /// <summary>Monotonic sequence assigned on the server before enqueue/RPC.</summary>
        static uint s_NextSequence = 1;

        /// <summary>Allocates the next spawn sequence id (server only).</summary>
        public static uint NextSequence() => s_NextSequence++;

        /// <summary>Server: queue a spawn for the local ClientWorld (listen-server / Editor host).</summary>
        public static void Enqueue(in SpawnRequest request) => Queue.Enqueue(request);

        /// <summary>Client: try to take one queued spawn (host path).</summary>
        public static bool TryDequeue(out SpawnRequest request) => Queue.TryDequeue(out request);

        /// <summary>Clears pending spawns when leaving a match.</summary>
        public static void Clear()
        {
            while (Queue.TryDequeue(out _)) { }
        }
    }
}
