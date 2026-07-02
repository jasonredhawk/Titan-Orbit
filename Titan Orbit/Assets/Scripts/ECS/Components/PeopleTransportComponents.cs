using TitanOrbit.Core;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    public struct PeopleTransportTag : IComponentData { }

    public struct PeopleTransportState : IComponentData
    {
        [GhostField] public float Amount;
        [GhostField] public float Health;
        [GhostField] public float3 Velocity;
        [GhostField] public float3 SpawnPosition;
        [GhostField] public float SpawnTime;
        [GhostField] public float CruiseSpeed;
        [GhostField] public int TargetShipNetworkId;
        [GhostField] public int SourcePlanetId;
        [GhostField] public int TargetPlanetId;
        [GhostField] public int SourceShipNetworkId;
        [GhostField] public byte IsLoad;
        [GhostField] public byte Team;
    }
}
