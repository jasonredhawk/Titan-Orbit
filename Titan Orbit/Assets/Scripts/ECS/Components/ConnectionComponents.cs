using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [NETCODE] Tracks a joining player until their ship is spawned. Server-only bookkeeping on
    /// connection entities — not ghost-replicated. Read/written by <see cref="TeamManagementSystem"/>
    /// and bootstrap flows during team pick and first spawn.
    /// </summary>
    public struct PendingPlayerConnection : IComponentData
    {
        // --- Type members ---
        /// <summary>[NETCODE] Stable id for this client connection (used in RPCs and scoreboard).</summary>
        public NetworkId NetworkId;

        /// <summary>
        /// [TITAN-ORBIT] True after <see cref="TeamManagementSystem"/> spawns the player's ship ghost;
        /// false while the player is still in team-selection lobby.
        /// </summary>
        public bool HasSpawnedShip;
    }

    /// <summary>
    /// [NETCODE] Buffer of active player connections on a server singleton entity. Used for team
    /// counts, rejoin lookups, and broadcasting RPC replies to the correct connection entity.
    /// </summary>
    public struct PlayerConnectionElement : IBufferElementData
    {
        /// <summary>[NETCODE] Stable network id for this connected player.</summary>
        public NetworkId NetworkId;

        /// <summary>[NETCODE] NetCode connection entity — target for RPC replies and disconnect cleanup.</summary>
        public Entity ConnectionEntity;

        /// <summary>[TITAN-ORBIT] True after the player has picked a team via <see cref="RequestTeamCommand"/>.</summary>
        public bool HasTeam;

        /// <summary>[TITAN-ORBIT] Assigned team as byte (cast to <see cref="Core.TeamId"/>).</summary>
        public byte Team;
    }
}
