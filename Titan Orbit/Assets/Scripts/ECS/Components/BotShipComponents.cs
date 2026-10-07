using TitanOrbit;
using TitanOrbit.Core;
using Unity.Entities;
using Unity.Mathematics;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server-only marker on an AI hull. Not ghosted. Orphan cleanup and the bot brain
    /// key off this so a hull with no player connection is not destroyed.
    /// </summary>
    public struct BotShipTag : IComponentData
    {
    }

    /// <summary>
    /// What the brain is doing this decision. Server-only — not a ghost field.
    /// </summary>
    public enum BotTaskKind : byte
    {
        None = 0,
        Mine = 1,
        Deposit = 2,
        Upgrade = 3,
        LoadTroops = 4,
        UnloadTroops = 5,
        /// <summary>Commander transport order: unload if carrying, otherwise load.</summary>
        Transport = 6,
        Attack = 7,
        Defend = 8,
        Follow = 9,
        Hold = 10,
        /// <summary>Fly to a ping and stop. Does not mine or peel off to fight.</summary>
        Advance = 11,
        /// <summary>Scoop loose gems from a rock this ship just destroyed.</summary>
        Collect = 12,
    }

    /// <summary>
    /// Server-only task memory for one AI ship. Re-decided about twice a second.
    /// Steering reads <see cref="Goal"/> every tick.
    /// </summary>
    public struct BotShipBrain : IComponentData
    {
        public BotTaskKind Task;
        /// <summary>World time when the next asteroid / planet scan may run.</summary>
        public float NextDecisionTime;
        public float3 Goal;
        /// <summary>
        /// Mine tasks: distance from the rock center where the hull should stop
        /// (visual surface plus a small gap). 0 for every other task.
        /// </summary>
        public float GoalRadius;
        /// <summary>Wreck position while <see cref="BotTaskKind.Collect"/> is gathering that burst.</summary>
        public float3 CollectOrigin;
        public int GoalPlanetId;
        public int GoalNetworkId;
        /// <summary>1 after this landing already bought one attribute. Cleared on takeoff.</summary>
        public byte AttributeBoughtWhileLanded;
        /// <summary>1 after a deposit, so the next empty hold loads troops before mining again.</summary>
        public byte SeekTroops;
        /// <summary>World time when <see cref="Task"/> or the people count last changed.</summary>
        public float TaskClock;
        /// <summary>People aboard when <see cref="TaskClock"/> was stamped.</summary>
        public int PeopleSnapshot;
    }

    /// <summary>
    /// One team's current commander override. <see cref="Task"/> None means the bots
    /// use their default program. Not ghosted.
    /// </summary>
    public struct BotTeamOrder
    {
        public BotTaskKind Task;
        /// <summary>First circled bot, or 0 when <see cref="LimitAudience"/> is 0 (every bot).</summary>
        public int AudienceNetworkId;
        /// <summary>Other circled bots. 0 when that slot is empty.</summary>
        public int AudienceA;
        public int AudienceB;
        public int AudienceC;
        public int AudienceD;
        /// <summary>1 = only the listed network ids. 0 = every bot on the team.</summary>
        public byte LimitAudience;
        /// <summary>Ship to follow, escort, or attack. 0 when unused.</summary>
        public int SubjectNetworkId;
        public int PlanetId;
        public float WaypointX;
        public float WaypointZ;
        public byte HasWaypoint;
        public float ExpireTime;

        public float3 Waypoint => new float3(WaypointX, 0f, WaypointZ);

        public bool IsActive(float now) => Task != BotTaskKind.None && now < ExpireTime;

        /// <summary>True when this hull should follow the order.</summary>
        public bool Includes(int networkId)
        {
            if (networkId <= 0)
                return false;
            if (LimitAudience == 0)
                return true;
            return networkId == AudienceNetworkId
                   || networkId == AudienceA
                   || networkId == AudienceB
                   || networkId == AudienceC
                   || networkId == AudienceD;
        }
    }

    /// <summary>
    /// Per-world commander orders, one slot per playable team. Written when a seated
    /// commander’s Comms Matrix sentence is accepted.
    /// </summary>
    public struct BotTeamOrders : IComponentData
    {
        public BotTeamOrder A;
        public BotTeamOrder B;
        public BotTeamOrder C;
        public BotTeamOrder D;
        public BotTeamOrder E;

        public BotTeamOrder Get(TeamId team)
        {
            switch (team)
            {
                case TeamId.TeamA: return A;
                case TeamId.TeamB: return B;
                case TeamId.TeamC: return C;
                case TeamId.TeamD: return D;
                case TeamId.TeamE: return E;
                default: return default;
            }
        }

        public void Set(TeamId team, in BotTeamOrder order)
        {
            switch (team)
            {
                case TeamId.TeamA: A = order; break;
                case TeamId.TeamB: B = order; break;
                case TeamId.TeamC: C = order; break;
                case TeamId.TeamD: D = order; break;
                case TeamId.TeamE: E = order; break;
            }
        }
    }

    /// <summary>
    /// Synthetic <see cref="GhostOwner.NetworkId"/> band for AI ships.
    /// Real NetCode connection ids stay far below <see cref="First"/>.
    /// Troop transfer and the gem bank require a non-zero id, and this band
    /// is how command-seat code skips bots without a ghosted tag.
    /// </summary>
    public static class BotShipIds
    {
        public const int First = 1_000_000;
        public const int MaxPerTeam = TitanOrbitDebugFlags.AiShipsPerTeamCap;
        public const int TeamCount = 5;

        public static bool IsBot(int networkId) =>
            networkId >= First && networkId < First + TeamCount * MaxPerTeam;

        /// <summary>Stable id for <paramref name="team"/> slot 0..<see cref="MaxPerTeam"/>.</summary>
        public static int ForSlot(TeamId team, int slot)
        {
            int teamIndex = (int)team - 1;
            return First + teamIndex * MaxPerTeam + slot;
        }
    }
}
