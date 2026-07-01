using TitanOrbit.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    public struct TeamStateSingleton : IComponentData
    {
        [GhostField] public int ActiveTeamCount;
        [GhostField] public int TeamACount;
        [GhostField] public int TeamBCount;
        [GhostField] public int TeamCCount;
        [GhostField] public int TeamDCount;
        [GhostField] public int TeamECount;
        [GhostField] public int EliminatedTeamsMask;
        public int MaxPlayersPerTeam;
    }

    public struct MatchStateSingleton : IComponentData
    {
        [GhostField] public bool MatchStarted;
        [GhostField] public float MatchTimer;
        [GhostField] public TeamId WinningTeam;
        [GhostField] public byte GameState;
    }

    public struct MapStateSingleton : IComponentData
    {
        [GhostField] public float MapWidth;
        [GhostField] public float MapHeight;
        [GhostField] public int BlueprintSeed;
        [GhostField] public float LoadingProgress;
        [GhostField] public bool LoadingComplete;
    }

    public struct MapLayoutEntryElement : IBufferElementData
    {
        [GhostField] public byte EntityKind;
        [GhostField] public float3 Position;
        [GhostField] public TeamId Team;
        [GhostField] public int PlanetId;
        [GhostField] public float Scale;
    }

    public struct PlayerNameElement : IBufferElementData
    {
        [GhostField] public int NetworkId;
        [GhostField] public FixedString64Bytes DisplayName;
    }
}
