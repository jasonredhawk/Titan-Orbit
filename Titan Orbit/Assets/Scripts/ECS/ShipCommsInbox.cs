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
        /// <summary>One 1–5 keyword sentence from a networked player, plus an optional ping.</summary>
        public struct Callout
        {
            /// <summary>[NETCODE] GhostOwner.NetworkId of the speaking ship.</summary>
            public int NetworkId;

            /// <summary>How many keyword bytes are live (1–5).</summary>
            public byte Count;

            /// <summary>First keyword index into <c>ShipCommsKeywordCatalog</c>.</summary>
            public byte K0;

            /// <summary>Second keyword index. Ignored when <see cref="Count"/> is 1.</summary>
            public byte K1;

            /// <summary>Third keyword index. Ignored when <see cref="Count"/> is under 3.</summary>
            public byte K2;

            /// <summary>Fourth keyword index. Ignored when <see cref="Count"/> is under 4.</summary>
            public byte K3;

            /// <summary>Fifth keyword index. Ignored when <see cref="Count"/> is under 5.</summary>
            public byte K4;

            /// <summary>
            /// Audience the server accepted: 0 = All, 1 = Team, 2 = Commander.
            /// Presentation-only here — delivery already happened on the server.
            /// </summary>
            public byte TeamOnly;

            /// <summary>1 when <see cref="WaypointX"/> / <see cref="WaypointZ"/> is valid.</summary>
            public byte HasWaypoint;

            /// <summary>World X of the optional minimap ping.</summary>
            public float WaypointX;

            /// <summary>World Z of the optional minimap ping.</summary>
            public float WaypointZ;

            /// <summary>
            /// 0 = none, 1 = minimap ping, 2 = asteroid, 3 = planet, 4 = moon,
            /// 5 = pad, 6 = turret, 7 = gem. Sender-resolved so every viewer draws the same pointer.
            /// </summary>
            public byte FocusKind;

            /// <summary>Closest ship locked by "You". 0 when unused.</summary>
            public int YouNetworkId;

            /// <summary>Closest planet, or the parent planet of a locked moon. 0 when unused.</summary>
            public int PlanetId;

            /// <summary>1 when the sentence fans out to every teammate (Everyone / Team).</summary>
            public byte Everyone;

            /// <summary>Nearby teammate locked by "Us" (speaker is implied). 0 when unused.</summary>
            public int Us0;

            /// <summary>Second nearby "Us" teammate.</summary>
            public int Us1;

            /// <summary>Third nearby "Us" teammate.</summary>
            public int Us2;

            /// <summary>Fourth nearby "Us" teammate.</summary>
            public int Us3;

            /// <summary>Speaker XZ fallback if the live hull is gone.</summary>
            public float MeX;

            /// <summary>Speaker Z fallback if the live hull is gone.</summary>
            public float MeZ;

            /// <summary>"You" XZ fallback if that hull is gone.</summary>
            public float YouX;

            /// <summary>"You" Z fallback if that hull is gone.</summary>
            public float YouZ;

            /// <summary>How many Us / Everyone fallback seats are stored (0–8).</summary>
            public byte GroupCount;

            public float G0X; public float G0Z;
            public float G1X; public float G1Z;
            public float G2X; public float G2Z;
            public float G3X; public float G3Z;
            public float G4X; public float G4Z;
            public float G5X; public float G5Z;
            public float G6X; public float G6Z;
            public float G7X; public float G7Z;
        }

        /// <summary>Wire values for <see cref="Callout.FocusKind"/>.</summary>
        public static class FocusKind
        {
            public const byte None = 0;
            public const byte MapPing = 1;
            public const byte Asteroid = 2;
            public const byte Planet = 3;
            public const byte Moon = 4;
            public const byte Pad = 5;
            public const byte Turret = 6;
            public const byte Gem = 7;
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
        /// Enqueues one callout. Ignores NetworkId ≤ 0, an empty count, or a speaker
        /// this client muted on the leaderboard (<see cref="CommsMuteList"/>).
        /// Channel is presentation-only here — the server already filtered who receives the RPC.
        /// </summary>
        public static void Enqueue(in Callout callout)
        {
            if (callout.NetworkId <= 0 || callout.Count < 1)
                return;

            // [TITAN-ORBIT] Local mute — server still sent the RPC. Drop here so the
            // presenter never paints chips, commander echoes, or path pings.
            // The local player is never added to the mute list, so our own optimistic
            // preview (ShipCommsPanel) still reaches the queue.
            if (CommsMuteList.IsMuted(callout.NetworkId))
                return;

            s_Pending.Enqueue(callout);
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
