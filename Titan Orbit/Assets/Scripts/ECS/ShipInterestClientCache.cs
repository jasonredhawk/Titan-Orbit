using UnityEngine;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [NETCODE] Latest minimap positions and scoreboard rows received by this client.
    /// The minimap builds a blip for ships that have no ghost (they are off the camera).
    /// The leaderboard reads the roster even when the full map is closed.
    /// Storage is a fixed array so applying a packet does not allocate.
    /// </summary>
    public static class ShipInterestClientCache
    {
        /// <summary>Hard cap. Extra ships in a packet are dropped.</summary>
        public const int Capacity = 128;

        /// <summary>Position rows from the latest complete blip flush.</summary>
        public static readonly ShipWireBlip[] Blips = new ShipWireBlip[Capacity];

        /// <summary>How many <see cref="Blips"/> slots are live.</summary>
        public static int BlipCount;

        /// <summary>Scoreboard rows from the latest complete roster flush.</summary>
        public static readonly ShipWireRoster[] Roster = new ShipWireRoster[Capacity];

        /// <summary>How many <see cref="Roster"/> slots are live.</summary>
        public static int RosterCount;

        static readonly ShipWireBlip[] s_BlipBuild = new ShipWireBlip[Capacity];
        static int s_BlipBuildCount;
        static int s_BlipGeneration = -1;
        static int s_BlipPacketsLeft;

        static readonly ShipWireRoster[] s_RosterBuild = new ShipWireRoster[Capacity];
        static int s_RosterBuildCount;
        static int s_RosterGeneration = -1;
        static int s_RosterPacketsLeft;

        /// <summary>[UNITY] Domain reload off: static arrays would keep the previous match.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => Clear();

        /// <summary>Drops both tables (session leave).</summary>
        public static void Clear()
        {
            BlipCount = 0;
            RosterCount = 0;
            s_BlipBuildCount = 0;
            s_RosterBuildCount = 0;
            s_BlipGeneration = -1;
            s_RosterGeneration = -1;
            s_BlipPacketsLeft = 0;
            s_RosterPacketsLeft = 0;
        }

        /// <summary>
        /// Folds one blip packet into the in-progress flush. When the last packet of that
        /// generation arrives, it replaces <see cref="Blips"/>.
        /// </summary>
        public static void PushBlipPacket(in ShipBlipSnapshotRpc rpc)
        {
            if (rpc.PacketIndex == 0 || rpc.Generation != s_BlipGeneration)
            {
                s_BlipGeneration = rpc.Generation;
                s_BlipBuildCount = 0;
                s_BlipPacketsLeft = rpc.PacketCount;
            }

            int count = mathMin(rpc.Count, ViewInterestTuning.BlipsPerPacket);
            for (int i = 0; i < count && s_BlipBuildCount < Capacity; i++)
                s_BlipBuild[s_BlipBuildCount++] = ShipInterestCodec.ReadBlip(rpc, i);

            s_BlipPacketsLeft--;
            if (s_BlipPacketsLeft > 0)
                return;

            BlipCount = s_BlipBuildCount;
            for (int i = 0; i < BlipCount; i++)
                Blips[i] = s_BlipBuild[i];
        }

        /// <summary>
        /// Folds one roster packet into the in-progress flush. When the last packet arrives,
        /// it replaces <see cref="Roster"/>.
        /// </summary>
        public static void PushRosterPacket(in ShipRosterSnapshotRpc rpc)
        {
            if (rpc.PacketIndex == 0 || rpc.Generation != s_RosterGeneration)
            {
                s_RosterGeneration = rpc.Generation;
                s_RosterBuildCount = 0;
                s_RosterPacketsLeft = rpc.PacketCount;
            }

            int count = mathMin(rpc.Count, ViewInterestTuning.RosterPerPacket);
            for (int i = 0; i < count && s_RosterBuildCount < Capacity; i++)
                s_RosterBuild[s_RosterBuildCount++] = ShipInterestCodec.ReadRoster(rpc, i);

            s_RosterPacketsLeft--;
            if (s_RosterPacketsLeft > 0)
                return;

            RosterCount = s_RosterBuildCount;
            for (int i = 0; i < RosterCount; i++)
                Roster[i] = s_RosterBuild[i];
        }

        /// <summary>Looks up a position row by network id.</summary>
        public static bool TryGetBlip(int networkId, out ShipWireBlip row)
        {
            for (int i = 0; i < BlipCount; i++)
            {
                if (Blips[i].NetworkId != networkId)
                    continue;
                row = Blips[i];
                return true;
            }

            row = default;
            return false;
        }

        /// <summary>Looks up a scoreboard row by network id.</summary>
        public static bool TryGetRoster(int networkId, out ShipWireRoster row)
        {
            for (int i = 0; i < RosterCount; i++)
            {
                if (Roster[i].NetworkId != networkId)
                    continue;
                row = Roster[i];
                return true;
            }

            row = default;
            return false;
        }

        static int mathMin(int a, int b) => a < b ? a : b;
    }
}
