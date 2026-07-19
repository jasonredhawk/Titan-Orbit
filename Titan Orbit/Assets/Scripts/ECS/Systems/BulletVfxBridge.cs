using System.Collections.Concurrent;
using System.Collections.Generic;
using Unity.Mathematics;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Cross-world bullet VFX queues (spawn + hit).
    /// <para>
    /// Server enqueues on fire / hit (local host). Client RPC handlers enqueue for dedicated
    /// clients. <see cref="Game.BulletVfxDriver"/> is the sole consumer — Instantiates muzzle /
    /// tracer / impact GameObjects. Sequence dedupe prevents host double-spawn (queue + RPC).
    /// Local anticipation uses <see cref="SpawnRequest.IsAnticipation"/> with Sequence 0 until
    /// a server spawn adopts it.
    /// </para>
    /// </summary>
    public static class BulletVfxBridge
    {
        /// <summary>One cosmetic tracer spawn (server-authoritative or local anticipation).</summary>
        public struct SpawnRequest
        {
            public uint Sequence;
            public float3 SpawnPosition;
            public float3 Velocity;
            public float Lifetime;
            public float MaxDistance;
            public float Damage;
            public byte OwnerTeam;
            public int OwnerNetworkId;
            public int BankIndex;
            public float ScaleMultiplier;
            /// <summary>True when client fired locally before the server RPC arrived.</summary>
            public bool IsAnticipation;
            /// <summary>True when positions are already in display/world space (skip toroidal unwrap).</summary>
            public bool IsDisplaySpace;
        }

        /// <summary>Authoritative impact — destroy matching tracer and play impact VFX.</summary>
        public struct HitRequest
        {
            public uint Sequence;
            public float3 HitPosition;
            public float Damage;
            public byte OwnerTeam;
            public int BankIndex;
            public float ScaleMultiplier;
        }

        static readonly ConcurrentQueue<SpawnRequest> SpawnQueue = new ConcurrentQueue<SpawnRequest>();
        static readonly ConcurrentQueue<HitRequest> HitQueue = new ConcurrentQueue<HitRequest>();
        static readonly HashSet<uint> SeenSequences = new HashSet<uint>();
        static readonly Queue<uint> SeenOrder = new Queue<uint>(128);
        static uint s_NextSequence = 1;
        const int MaxSeen = 512;

        /// <summary>Allocates the next shot sequence id (server only).</summary>
        public static uint NextSequence() => s_NextSequence++;

        /// <summary>
        /// Enqueues a spawn. Server sequences (non-zero) are deduped against host queue + RPC.
        /// Anticipation (Sequence 0) always enqueues.
        /// </summary>
        public static bool TryEnqueueSpawn(in SpawnRequest request)
        {
            if (request.Sequence != 0 && !RememberSequence(request.Sequence))
                return false;
            SpawnQueue.Enqueue(request);
            return true;
        }

        /// <summary>Driver: take next pending spawn.</summary>
        public static bool TryDequeueSpawn(out SpawnRequest request) => SpawnQueue.TryDequeue(out request);

        /// <summary>Enqueues an impact (no dedupe — each hit is unique).</summary>
        public static void EnqueueHit(in HitRequest request)
        {
            if (request.Sequence == 0)
                return;
            HitQueue.Enqueue(request);
        }

        /// <summary>Driver: take next pending hit.</summary>
        public static bool TryDequeueHit(out HitRequest request) => HitQueue.TryDequeue(out request);

        /// <summary>Clears pending queues when leaving a match.</summary>
        public static void Clear()
        {
            while (SpawnQueue.TryDequeue(out _)) { }
            while (HitQueue.TryDequeue(out _)) { }
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
