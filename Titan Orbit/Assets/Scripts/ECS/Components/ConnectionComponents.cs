using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Tracks a joining player until their ship is spawned. Server-only bookkeeping on connection entities.
    /// Read/written by <see cref="TeamManagementSystem"/> and bootstrap flows.
    /// </summary>
    public struct PendingPlayerConnection : IComponentData
    {
        /// <summary>[NETCODE] Stable id for this client connection.</summary>
        public NetworkId NetworkId;
        /// <summary>True after <see cref="TeamManagementSystem"/> spawns the player's ship ghost.</summary>
        public bool HasSpawnedShip;
    }

    /// <summary>
    /// Buffer of active player connections on a server singleton. Used for team counts and rejoin lookups.
    /// </summary>
    public struct PlayerConnectionElement : IBufferElementData
    {
        public NetworkId NetworkId;
        /// <summary>NetCode connection entity — target for RPC replies.</summary>
        public Entity ConnectionEntity;
        public bool HasTeam;
        public byte Team;
    }
}
