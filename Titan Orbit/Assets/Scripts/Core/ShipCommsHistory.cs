using System.Collections.Generic;
using UnityEngine;

namespace TitanOrbit.Core
{
    /// <summary>
    /// Client-only ring of the last ten sentences this player sent from the hold-S matrix.
    /// <c>ShipCommsPanel</c> paints them as quick-select chips. Not networked — each machine
    /// only remembers what <b>this</b> player composed.
    /// </summary>
    public static class ShipCommsHistory
    {
        /// <summary>How many recent sentences the compose panel shows.</summary>
        public const int MaxEntries = 10;

        /// <summary>One 1–5 keyword sentence stored as catalog indices.</summary>
        public struct Sentence
        {
            /// <summary>Live keyword count (1–5).</summary>
            public byte Count;

            /// <summary>First keyword index.</summary>
            public byte K0;

            /// <summary>Second keyword index. Ignored when <see cref="Count"/> is 1.</summary>
            public byte K1;

            /// <summary>Third keyword index. Ignored when <see cref="Count"/> is under 3.</summary>
            public byte K2;

            /// <summary>Fourth keyword index. Ignored when <see cref="Count"/> is under 4.</summary>
            public byte K3;

            /// <summary>Fifth keyword index. Ignored when <see cref="Count"/> is under 5.</summary>
            public byte K4;

            /// <summary>1 when this sentence locked a minimap / Here world point.</summary>
            public byte HasWaypoint;

            /// <summary>World X of the remembered ping.</summary>
            public float WaypointX;

            /// <summary>World Z of the remembered ping.</summary>
            public float WaypointZ;

            /// <summary>True when both sentences use the same indices and the same ping.</summary>
            public bool EqualsSentence(in Sentence other)
            {
                if (Count != other.Count
                    || K0 != other.K0
                    || K1 != other.K1
                    || K2 != other.K2
                    || K3 != other.K3
                    || K4 != other.K4
                    || HasWaypoint != other.HasWaypoint)
                    return false;

                if (HasWaypoint == 0)
                    return true;

                return Mathf.Abs(WaypointX - other.WaypointX) < 0.05f
                    && Mathf.Abs(WaypointZ - other.WaypointZ) < 0.05f;
            }
        }

        static readonly List<Sentence> s_Recent = new List<Sentence>(MaxEntries);

        /// <summary>
        /// [UNITY] Domain Reload off leaves statics sticky. Clear so the next Play does not
        /// show the previous session's quick-select row.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => s_Recent.Clear();

        /// <summary>Newest-first sentences. Count is 0–10.</summary>
        public static IReadOnlyList<Sentence> Recent => s_Recent;

        /// <summary>
        /// Pushes a sent sentence to the front. A duplicate is moved to slot 0 instead of
        /// growing the list. Drops the oldest when over <see cref="MaxEntries"/>.
        /// </summary>
        public static void Record(
            byte count, byte k0, byte k1, byte k2, byte k3, byte k4,
            byte hasWaypoint = 0, float waypointX = 0f, float waypointZ = 0f)
        {
            if (count < 1)
                return;

            var sentence = new Sentence
            {
                Count = count,
                K0 = k0,
                K1 = k1,
                K2 = k2,
                K3 = k3,
                K4 = k4,
                HasWaypoint = hasWaypoint != 0 ? (byte)1 : (byte)0,
                WaypointX = waypointX,
                WaypointZ = waypointZ,
            };
            for (int i = 0; i < s_Recent.Count; i++)
            {
                if (!s_Recent[i].EqualsSentence(sentence))
                    continue;

                s_Recent.RemoveAt(i);
                break;
            }

            s_Recent.Insert(0, sentence);
            while (s_Recent.Count > MaxEntries)
                s_Recent.RemoveAt(s_Recent.Count - 1);
        }

        /// <summary>Reads one history slot. Returns false when that slot is empty.</summary>
        public static bool TryGet(int index, out Sentence sentence)
        {
            if (index < 0 || index >= s_Recent.Count)
            {
                sentence = default;
                return false;
            }

            sentence = s_Recent[index];
            return true;
        }
    }
}
