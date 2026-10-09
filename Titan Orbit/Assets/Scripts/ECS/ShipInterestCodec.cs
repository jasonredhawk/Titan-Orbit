namespace TitanOrbit.ECS
{
    /// <summary>
    /// One ship row inside a minimap position packet. Not sent on its own — packed into
    /// <see cref="ShipBlipSnapshotRpc"/>.
    /// </summary>
    public struct ShipWireBlip
    {
        /// <summary>Owner network id.</summary>
        public int NetworkId;

        /// <summary>World X.</summary>
        public float X;

        /// <summary>World Z.</summary>
        public float Z;

        /// <summary>Team as a byte.</summary>
        public byte Team;

        /// <summary>Ship level clamped to a byte.</summary>
        public byte Level;

        /// <summary><see cref="ViewInterestTuning"/> flag bits.</summary>
        public byte Flags;
    }

    /// <summary>
    /// One scoreboard row. No position. Packed into <see cref="ShipRosterSnapshotRpc"/>.
    /// </summary>
    public struct ShipWireRoster
    {
        /// <summary>Owner network id.</summary>
        public int NetworkId;

        /// <summary>Team as a byte.</summary>
        public byte Team;

        /// <summary>Ship level clamped to a byte.</summary>
        public byte Level;

        /// <summary><see cref="ViewInterestTuning"/> flag bits.</summary>
        public byte Flags;

        /// <summary>Match kills.</summary>
        public int Kills;

        /// <summary>Gems deposited this match.</summary>
        public int GemsDeposited;

        /// <summary>People delivered this match.</summary>
        public int PeopleDelivered;

        /// <summary>
        /// Ghosted match score. Sent on its own so a death that only cuts this value
        /// (kill / gem / troop counts unchanged) still updates off-screen leaderboards.
        /// </summary>
        public int Score;
    }

    /// <summary>
    /// [NETCODE] Packs and unpacks the flattened RPC slots. IRpcCommand cannot carry an array,
    /// so each ship is a numbered field. Slots past <c>Count</c> are ignored.
    /// </summary>
    public static class ShipInterestCodec
    {
        /// <summary>Writes one blip into slot <paramref name="index"/> (0–7).</summary>
        public static void WriteBlip(ref ShipBlipSnapshotRpc rpc, int index, in ShipWireBlip row)
        {
            switch (index)
            {
                case 0: rpc.N0 = row.NetworkId; rpc.X0 = row.X; rpc.Z0 = row.Z; rpc.Team0 = row.Team; rpc.Level0 = row.Level; rpc.Flags0 = row.Flags; break;
                case 1: rpc.N1 = row.NetworkId; rpc.X1 = row.X; rpc.Z1 = row.Z; rpc.Team1 = row.Team; rpc.Level1 = row.Level; rpc.Flags1 = row.Flags; break;
                case 2: rpc.N2 = row.NetworkId; rpc.X2 = row.X; rpc.Z2 = row.Z; rpc.Team2 = row.Team; rpc.Level2 = row.Level; rpc.Flags2 = row.Flags; break;
                case 3: rpc.N3 = row.NetworkId; rpc.X3 = row.X; rpc.Z3 = row.Z; rpc.Team3 = row.Team; rpc.Level3 = row.Level; rpc.Flags3 = row.Flags; break;
                case 4: rpc.N4 = row.NetworkId; rpc.X4 = row.X; rpc.Z4 = row.Z; rpc.Team4 = row.Team; rpc.Level4 = row.Level; rpc.Flags4 = row.Flags; break;
                case 5: rpc.N5 = row.NetworkId; rpc.X5 = row.X; rpc.Z5 = row.Z; rpc.Team5 = row.Team; rpc.Level5 = row.Level; rpc.Flags5 = row.Flags; break;
                case 6: rpc.N6 = row.NetworkId; rpc.X6 = row.X; rpc.Z6 = row.Z; rpc.Team6 = row.Team; rpc.Level6 = row.Level; rpc.Flags6 = row.Flags; break;
                default: rpc.N7 = row.NetworkId; rpc.X7 = row.X; rpc.Z7 = row.Z; rpc.Team7 = row.Team; rpc.Level7 = row.Level; rpc.Flags7 = row.Flags; break;
            }
        }

        /// <summary>Reads blip slot <paramref name="index"/> (0–7).</summary>
        public static ShipWireBlip ReadBlip(in ShipBlipSnapshotRpc rpc, int index)
        {
            switch (index)
            {
                case 0: return new ShipWireBlip { NetworkId = rpc.N0, X = rpc.X0, Z = rpc.Z0, Team = rpc.Team0, Level = rpc.Level0, Flags = rpc.Flags0 };
                case 1: return new ShipWireBlip { NetworkId = rpc.N1, X = rpc.X1, Z = rpc.Z1, Team = rpc.Team1, Level = rpc.Level1, Flags = rpc.Flags1 };
                case 2: return new ShipWireBlip { NetworkId = rpc.N2, X = rpc.X2, Z = rpc.Z2, Team = rpc.Team2, Level = rpc.Level2, Flags = rpc.Flags2 };
                case 3: return new ShipWireBlip { NetworkId = rpc.N3, X = rpc.X3, Z = rpc.Z3, Team = rpc.Team3, Level = rpc.Level3, Flags = rpc.Flags3 };
                case 4: return new ShipWireBlip { NetworkId = rpc.N4, X = rpc.X4, Z = rpc.Z4, Team = rpc.Team4, Level = rpc.Level4, Flags = rpc.Flags4 };
                case 5: return new ShipWireBlip { NetworkId = rpc.N5, X = rpc.X5, Z = rpc.Z5, Team = rpc.Team5, Level = rpc.Level5, Flags = rpc.Flags5 };
                case 6: return new ShipWireBlip { NetworkId = rpc.N6, X = rpc.X6, Z = rpc.Z6, Team = rpc.Team6, Level = rpc.Level6, Flags = rpc.Flags6 };
                default: return new ShipWireBlip { NetworkId = rpc.N7, X = rpc.X7, Z = rpc.Z7, Team = rpc.Team7, Level = rpc.Level7, Flags = rpc.Flags7 };
            }
        }

        /// <summary>Writes one roster row into slot <paramref name="index"/> (0–3).</summary>
        public static void WriteRoster(ref ShipRosterSnapshotRpc rpc, int index, in ShipWireRoster row)
        {
            switch (index)
            {
                case 0:
                    rpc.N0 = row.NetworkId; rpc.Team0 = row.Team; rpc.Level0 = row.Level; rpc.Flags0 = row.Flags;
                    rpc.Kills0 = row.Kills; rpc.Gems0 = row.GemsDeposited; rpc.People0 = row.PeopleDelivered; rpc.Score0 = row.Score;
                    break;
                case 1:
                    rpc.N1 = row.NetworkId; rpc.Team1 = row.Team; rpc.Level1 = row.Level; rpc.Flags1 = row.Flags;
                    rpc.Kills1 = row.Kills; rpc.Gems1 = row.GemsDeposited; rpc.People1 = row.PeopleDelivered; rpc.Score1 = row.Score;
                    break;
                case 2:
                    rpc.N2 = row.NetworkId; rpc.Team2 = row.Team; rpc.Level2 = row.Level; rpc.Flags2 = row.Flags;
                    rpc.Kills2 = row.Kills; rpc.Gems2 = row.GemsDeposited; rpc.People2 = row.PeopleDelivered; rpc.Score2 = row.Score;
                    break;
                default:
                    rpc.N3 = row.NetworkId; rpc.Team3 = row.Team; rpc.Level3 = row.Level; rpc.Flags3 = row.Flags;
                    rpc.Kills3 = row.Kills; rpc.Gems3 = row.GemsDeposited; rpc.People3 = row.PeopleDelivered; rpc.Score3 = row.Score;
                    break;
            }
        }

        /// <summary>Reads roster slot <paramref name="index"/> (0–3).</summary>
        public static ShipWireRoster ReadRoster(in ShipRosterSnapshotRpc rpc, int index)
        {
            switch (index)
            {
                case 0:
                    return new ShipWireRoster { NetworkId = rpc.N0, Team = rpc.Team0, Level = rpc.Level0, Flags = rpc.Flags0, Kills = rpc.Kills0, GemsDeposited = rpc.Gems0, PeopleDelivered = rpc.People0, Score = rpc.Score0 };
                case 1:
                    return new ShipWireRoster { NetworkId = rpc.N1, Team = rpc.Team1, Level = rpc.Level1, Flags = rpc.Flags1, Kills = rpc.Kills1, GemsDeposited = rpc.Gems1, PeopleDelivered = rpc.People1, Score = rpc.Score1 };
                case 2:
                    return new ShipWireRoster { NetworkId = rpc.N2, Team = rpc.Team2, Level = rpc.Level2, Flags = rpc.Flags2, Kills = rpc.Kills2, GemsDeposited = rpc.Gems2, PeopleDelivered = rpc.People2, Score = rpc.Score2 };
                default:
                    return new ShipWireRoster { NetworkId = rpc.N3, Team = rpc.Team3, Level = rpc.Level3, Flags = rpc.Flags3, Kills = rpc.Kills3, GemsDeposited = rpc.Gems3, PeopleDelivered = rpc.People3, Score = rpc.Score3 };
            }
        }
    }
}
