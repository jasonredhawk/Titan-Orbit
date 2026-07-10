using TitanOrbit.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Replicated team roster and elimination state. Ghost — networked singleton replicated to all clients.
    /// Written by <see cref="TeamManagementSystem"/> on team pick; read by HUD and win logic.
    /// </summary>
    public struct TeamStateSingleton : IComponentData
    {
        /// <summary>Number of teams in this match (2–5 from map generation).</summary>
        [GhostField] public int ActiveTeamCount;
        [GhostField] public int TeamACount;
        [GhostField] public int TeamBCount;
        [GhostField] public int TeamCCount;
        [GhostField] public int TeamDCount;
        [GhostField] public int TeamECount;
        /// <summary>Bitmask of teams eliminated from the match.</summary>
        [GhostField] public int EliminatedTeamsMask;
        /// <summary>Server cap per team from bootstrap config.</summary>
        public int MaxPlayersPerTeam;
    }

    /// <summary>
    /// Match lifecycle: timer, win state, and high-level game phase byte. Replicated to clients for HUD.
    /// </summary>
    public struct MatchStateSingleton : IComponentData
    {
        [GhostField] public bool MatchStarted;
        [GhostField] public float MatchTimer;
        /// <summary>Non-None when <see cref="CaptureSystem"/> detects all planets owned by one team.</summary>
        [GhostField] public TeamId WinningTeam;
        /// <summary>Opaque phase id for UI (0=lobby, 1=playing, 2=won, etc.).</summary>
        [GhostField] public byte GameState;
    }

    /// <summary>
    /// Toroidal map dimensions and async loading progress during <see cref="MapGenerationSystem"/>.
    /// Ghost-serialized so clients know world bounds and loading screen state.
    /// </summary>
    public struct MapStateSingleton : IComponentData
    {
        [GhostField] public float MapWidth;
        [GhostField] public float MapHeight;
        [GhostField] public int BlueprintSeed;
        [GhostField] public float LoadingProgress;
        [GhostField] public int LoadingCompletedSteps;
        [GhostField] public int LoadingTotalSteps;
        [GhostField] public bool LoadingComplete;
    }

    /// <summary>
    /// Serialized planet/asteroid layout for client-side minimap and late joiners.
    /// Filled during map generation on the server.
    /// </summary>
    public struct MapLayoutEntryElement : IBufferElementData
    {
        [GhostField] public byte EntityKind;
        [GhostField] public float3 Position;
        [GhostField] public TeamId Team;
        [GhostField] public int PlanetId;
        [GhostField] public float Scale;
    }

    /// <summary>Display names keyed by network id — set via <see cref="SetPlayerNameCommand"/> RPC.</summary>
    public struct PlayerNameElement : IBufferElementData
    {
        [GhostField] public int NetworkId;
        [GhostField] public FixedString64Bytes DisplayName;
    }
}
