using System.Collections.Generic;
using Unity.Mathematics;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Cross-world mine explosion VFX queue.
    /// <para>
    /// Server enqueues on detonate (local host). Client RPC handlers enqueue for dedicated
    /// clients. <c>MineVisualDriver</c> is the sole consumer — Instantiates the FireballsV2
    /// (or catalog) impact at the mine pose. Sequence dedupe prevents host double-burst
    /// (in-process bridge + RPC).
    /// </para>
    /// </summary>
    public static class MineExplosionBridge
    {
        /// <summary>One cosmetic mine burst (server-authoritative).</summary>
        public struct Request
        {
            public uint Sequence;
            public float3 Position;
            public byte OwnerTeam;
            public int ItemLevel;
            public float VisualScale;
            public float ExplosionVfxScale;
            public float Damage;
        }

        static readonly List<Request> s_pending = new List<Request>(16);
        static readonly HashSet<uint> s_seen = new HashSet<uint>();

        /// <summary>Server / RPC handler: queue a burst. Duplicate Sequence is ignored.</summary>
        public static void Enqueue(in Request request)
        {
            if (request.Sequence != 0 && !s_seen.Add(request.Sequence))
                return;
            s_pending.Add(request);
        }

        /// <summary>True when the visual driver has a burst waiting.</summary>
        public static bool TryDequeue(out Request request)
        {
            if (s_pending.Count == 0)
            {
                request = default;
                return false;
            }

            request = s_pending[0];
            s_pending.RemoveAt(0);
            return true;
        }

        /// <summary>Forget Sequence ids so a long match does not grow the set forever.</summary>
        public static void ForgetSequence(uint sequence)
        {
            if (sequence != 0)
                s_seen.Remove(sequence);
        }
    }
}
