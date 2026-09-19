using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Cross-world asteroid death VFX queue.
    /// <para>
    /// Client <see cref="AsteroidDestroyedRpcClientSystem"/> enqueues the wrapped sim pose
    /// and uniform scale. <c>AsteroidDeathVfxDriver</c> is the sole consumer — it rents
    /// Fire/V1 Modular or team FireImpact from the one-shot pool (no per-kill Instantiates).
    /// </para>
    /// </summary>
    public static class AsteroidDeathVfxBridge
    {
        /// <summary>One cosmetic rock burst (server-authoritative destroy RPC).</summary>
        public struct Request
        {
            public float3 Position;
            public float Scale;
        }

        /// <summary>Drop oldest if a thin client or missing driver never drains.</summary>
        const int MaxPending = 24;

        static readonly List<Request> s_pending = new List<Request>(8);

        /// <summary>[UNITY] Domain reload / enter Play without Domain Reload.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            s_pending.Clear();
        }

        /// <summary>Client RPC handler: queue a burst at the wrapped destroy pose.</summary>
        public static void Enqueue(float3 position, float scale)
        {
            if (s_pending.Count >= MaxPending)
                s_pending.RemoveAt(0);

            s_pending.Add(new Request
            {
                Position = position,
                Scale = scale > 0.01f ? scale : 1f,
            });
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
    }
}
