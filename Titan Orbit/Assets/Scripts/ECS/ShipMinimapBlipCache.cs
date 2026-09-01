using System.Collections.Generic;
using TitanOrbit.Core;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Client cache of far-ship minimap blips from <see cref="ShipMinimapBlipRpc"/>.
    /// Nearby combat hulls still come from ship ghosts.
    /// </summary>
    public static class ShipMinimapBlipCache
    {
        public struct Entry
        {
            public int NetworkId;
            public float X;
            public float Z;
            public TeamId Team;
            public byte Level;
            public bool IsDead;
            public bool IsMega;
        }

        static readonly List<Entry> s_Entries = new List<Entry>(64);
        static readonly List<Entry> s_Building = new List<Entry>(64);
        static readonly Dictionary<int, int> s_IndexByNetworkId = new Dictionary<int, int>(64);
        static readonly HashSet<byte> s_ReceivedChunks = new HashSet<byte>();

        static uint s_BuildingSequence;

        public static uint Sequence { get; private set; }
        public static IReadOnlyList<Entry> Entries => s_Entries;

        public static void ApplyChunk(uint sequence, byte chunkIndex, byte chunkCount, List<Entry> chunk)
        {
            if (sequence != s_BuildingSequence)
            {
                s_BuildingSequence = sequence;
                s_Building.Clear();
                s_ReceivedChunks.Clear();
            }

            if (!s_ReceivedChunks.Add(chunkIndex))
                return;
            if (chunk != null)
            {
                for (int i = 0; i < chunk.Count; i++)
                {
                    if (chunk[i].NetworkId > 0)
                        s_Building.Add(chunk[i]);
                }
            }

            if (chunkCount <= 0 || s_ReceivedChunks.Count < chunkCount)
                return;
            Replace(sequence, s_Building);
        }

        public static void Replace(uint sequence, List<Entry> next)
        {
            Sequence = sequence;
            s_Entries.Clear();
            s_IndexByNetworkId.Clear();
            if (next == null)
                return;
            for (int i = 0; i < next.Count; i++)
            {
                var e = next[i];
                if (e.NetworkId <= 0)
                    continue;
                s_IndexByNetworkId[e.NetworkId] = s_Entries.Count;
                s_Entries.Add(e);
            }
        }

        public static bool TryGet(int networkId, out Entry entry)
        {
            if (s_IndexByNetworkId.TryGetValue(networkId, out int i))
            {
                entry = s_Entries[i];
                return true;
            }

            entry = default;
            return false;
        }
    }
}
