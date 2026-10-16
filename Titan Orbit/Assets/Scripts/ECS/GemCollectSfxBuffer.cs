using System.Collections.Generic;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Client presentation queue: one entry per scooped crystal, drained by
    /// <c>EcsFloatingCountPresenter</c> into per-tint collect sounds.
    /// Server pickup writes the tint on <see cref="GemConsumedRpc"/>; this buffer
    /// only holds events the local client should hear. Expiry never arrives here.
    /// </summary>
    public static class GemCollectSfxBuffer
    {
        /// <summary>One scooped crystal waiting for its collect sting.</summary>
        public struct Event
        {
            public int CollectorNetworkId;
            public byte Tint;
            public float Amount;
        }

        const int MaxPending = 64;

        static readonly List<Event> Pending = new List<Event>(16);

        /// <summary>
        /// Queues a pickup sting. No-ops for expiry (<paramref name="collectorNetworkId"/> 0)
        /// and empty takes.
        /// </summary>
        public static void Push(int collectorNetworkId, byte tint, float amount)
        {
            if (collectorNetworkId == 0 || amount <= 0.001f)
                return;

            if (Pending.Count >= MaxPending)
                Pending.RemoveAt(0);

            Pending.Add(new Event
            {
                CollectorNetworkId = collectorNetworkId,
                Tint = tint,
                Amount = amount,
            });
        }

        /// <summary>Moves queued scoops into <paramref name="dst"/> and clears the buffer.</summary>
        public static void CopyAndClear(List<Event> dst)
        {
            dst.Clear();
            for (int i = 0; i < Pending.Count; i++)
                dst.Add(Pending[i]);
            Pending.Clear();
        }

        /// <summary>Drops queued scoops (leave match / disconnect).</summary>
        public static void Clear()
        {
            Pending.Clear();
        }
    }
}
