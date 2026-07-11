using TitanOrbit.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [NETCODE] Replicated team roster and elimination state. Ghost singleton replicated to all clients.
    /// Written by <see cref="TeamManagementSystem"/> on team pick; read by HUD, minimap, and win logic.
    /// </summary>
    public struct TeamStateSingleton : IComponentData
    {
        // --- Type members ---
        /// <summary>[TITAN-ORBIT] Number of teams in this match (2–5 from map generation).</summary>
        [GhostField] public int ActiveTeamCount;

        /// <summary>[TITAN-ORBIT] Player count on team A.</summary>
        [GhostField] public int TeamACount;

        /// <summary>[TITAN-ORBIT] Player count on team B.</summary>
        [GhostField] public int TeamBCount;

        /// <summary>[TITAN-ORBIT] Player count on team C.</summary>
        [GhostField] public int TeamCCount;

        /// <summary>[TITAN-ORBIT] Player count on team D.</summary>
        [GhostField] public int TeamDCount;

        /// <summary>[TITAN-ORBIT] Player count on team E.</summary>
        [GhostField] public int TeamECount;

        /// <summary>[TITAN-ORBIT] Bitmask of teams eliminated from the match (no home planet left).</summary>
        [GhostField] public int EliminatedTeamsMask;

        /// <summary>[TITAN-ORBIT] Server cap per team from bootstrap config (not ghost-serialized default).</summary>
        public int MaxPlayersPerTeam;
    }

    /// <summary>
    /// [NETCODE] Match lifecycle: timer, win state, and high-level game phase byte. Replicated to
    /// clients for HUD and end-game screens.
    /// </summary>
    public struct MatchStateSingleton : IComponentData
    {
        /// <summary>[TITAN-ORBIT] True after first ship spawns and match clock starts.</summary>
        [GhostField] public bool MatchStarted;

        /// <summary>[UNITY] Elapsed match time in seconds since MatchStarted.</summary>
        [GhostField] public float MatchTimer;

        /// <summary>
        /// [TITAN-ORBIT] Non-<see cref="TeamId.None"/> when <see cref="CaptureSystem"/> detects all
        /// planets owned by one team.
        /// </summary>
        [GhostField] public TeamId WinningTeam;

        /// <summary>[TITAN-ORBIT] Opaque phase id for UI (0=lobby, 1=playing, 2=won, etc.).</summary>
        [GhostField] public byte GameState;
    }

    /// <summary>
    /// [NETCODE] Toroidal map dimensions and async loading progress during map generation.
    /// Ghost-serialized so clients know world bounds and loading screen state.
    /// </summary>
    public struct MapStateSingleton : IComponentData
    {
        /// <summary>[TITAN-ORBIT] Toroidal map width in world units.</summary>
        [GhostField] public float MapWidth;

        /// <summary>[TITAN-ORBIT] Toroidal map height in world units.</summary>
        [GhostField] public float MapHeight;

        /// <summary>[TITAN-ORBIT] Seed used for this match layout (debug/replay).</summary>
        [GhostField] public int BlueprintSeed;

        /// <summary>[TITAN-ORBIT] 0–1 loading progress for client loading screen.</summary>
        [GhostField] public float LoadingProgress;

        /// <summary>[TITAN-ORBIT] Completed map generation steps.</summary>
        [GhostField] public int LoadingCompletedSteps;

        /// <summary>[TITAN-ORBIT] Total map generation steps.</summary>
        [GhostField] public int LoadingTotalSteps;

        /// <summary>[TITAN-ORBIT] True when map entities are fully spawned and playable.</summary>
        [GhostField] public bool LoadingComplete;
    }

    /// <summary>
    /// [NETCODE] Serialized planet/asteroid layout entry for client-side minimap and late joiners.
    /// Filled during map generation on the server into a ghost buffer.
    /// </summary>
    public struct MapLayoutEntryElement : IBufferElementData
    {
        /// <summary>[TITAN-ORBIT] Entity kind byte (planet, asteroid, home, etc.).</summary>
        [GhostField] public byte EntityKind;

        /// <summary>[ECS/DOTS] Toroidal world position at spawn.</summary>
        [GhostField] public float3 Position;

        /// <summary>[TITAN-ORBIT] Initial team ownership for tinting.</summary>
        [GhostField] public TeamId Team;

        /// <summary>[TITAN-ORBIT] Planet id when EntityKind is a planet; 0 otherwise.</summary>
        [GhostField] public int PlanetId;

        /// <summary>[UNITY] Visual/body scale at spawn.</summary>
        [GhostField] public float Scale;
    }

    /// <summary>
    /// [NETCODE] Display names keyed by network id — set via <see cref="SetPlayerNameCommand"/> RPC.
    /// </summary>
    public struct PlayerNameElement : IBufferElementData
    {
        /// <summary>[NETCODE] Player network id.</summary>
        [GhostField] public int NetworkId;

        /// <summary>[TITAN-ORBIT] UTF-8 display name for scoreboard and HUD.</summary>
        [GhostField] public FixedString64Bytes DisplayName;
    }
}
