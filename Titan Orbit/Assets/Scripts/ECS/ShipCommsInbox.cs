using System.Collections.Concurrent;
using UnityEngine;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Process-wide queue of ship comms callouts for the GameObject presenter.
    /// <para>
    /// [NETCODE] <see cref="ShipCommsRpcClientSystem"/> (ClientWorld) drains
    /// <see cref="ShipCommsRpc"/> entities and pushes rows here. The ECS assembly cannot
    /// reference <c>TitanOrbit.Game</c>, so this static inbox is the bridge — same idea as
    /// <see cref="BulletVfxBridge"/> and <see cref="PlayerNameRosterCache"/>.
    /// </para>
    /// <c>ShipCommsBubblePresenter</c> dequeues on the main thread in LateUpdate and paints
    /// chips above that player's hull. ConcurrentQueue so a system thread and the presenter
    /// cannot tear a row.
    /// </summary>
    public static class ShipCommsInbox
    {
        /// <summary>One 1–3 keyword sentence from a networked player.</summary>
        public struct Callout
        {
            /// <summary>[NETCODE] GhostOwner.NetworkId of the speaking ship.</summary>
            public int NetworkId;

            /// <summary>How many keyword bytes are live (1–3).</summary>
            public byte Count;

            /// <summary>First keyword index into <c>ShipCommsKeywordCatalog</c>.</summary>
            public byte K0;

            /// <summary>Second keyword index. Ignored when <see cref="Count"/> is 1.</summary>
            public byte K1;

            /// <summary>Third keyword index. Ignored when <see cref="Count"/> is under 3.</summary>
            public byte K2;
        }

        /// <summary>Pending callouts. Capacity is not reserved — comms are rare.</summary>
        static readonly ConcurrentQueue<Callout> s_Pending = new ConcurrentQueue<Callout>();

        /// <summary>
        /// [UNITY] Domain Reload off: queues survive Play Mode. Clear so the next Play
        /// does not flash the previous match's chips.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStaticsForPlayMode() => Clear();

        /// <summary>Drops every queued callout (session leave / Play Mode reset).</summary>
        public static void Clear()
        {
            while (s_Pending.TryDequeue(out _))
            {
            }
        }

        /// <summary>
        /// Enqueues one broadcast callout. Ignores NetworkId ≤ 0 or an empty count.
        /// </summary>
        public static void Enqueue(int networkId, byte count, byte k0, byte k1, byte k2)
        {
            if (networkId <= 0 || count < 1)
                return;

            s_Pending.Enqueue(new Callout
            {
                NetworkId = networkId,
                Count = count,
                K0 = k0,
                K1 = k1,
                K2 = k2,
            });
        }

        /// <summary>
        /// Pops the next callout for presentation. Returns false when the queue is empty.
        /// </summary>
        public static bool TryDequeue(out Callout callout)
        {
            return s_Pending.TryDequeue(out callout);
        }
    }
}
