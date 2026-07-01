using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    public struct PendingPlayerConnection : IComponentData
    {
        public NetworkId NetworkId;
        public bool HasSpawnedShip;
    }

    public struct PlayerConnectionElement : IBufferElementData
    {
        public NetworkId NetworkId;
        public Entity ConnectionEntity;
        public bool HasTeam;
        public byte Team;
    }
}
