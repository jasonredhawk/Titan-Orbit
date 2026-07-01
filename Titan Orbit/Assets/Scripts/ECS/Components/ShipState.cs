using TitanOrbit.Core;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    public struct ShipState : IComponentData
    {
        [GhostField] public float Health;
        [GhostField] public float MaxHealth;
        [GhostField] public TeamId Team;
        [GhostField] public int ShipLevel;
        [GhostField] public float CurrentGems;
        [GhostField] public float GemCapacity;
        [GhostField] public int CurrentPeople;
        [GhostField] public bool IsDead;
        [GhostField] public bool AwaitingTeamSelection;
    }

    public struct ShipMotorConfig : IComponentData
    {
        public float EngineThrust;
        public float MaxSpeed;
        public float RotationSpeed;
        public float BrakeDeceleration;
        public float Mass;
    }

    public struct ShipKinematics : IComponentData
    {
        public float3 Velocity;
    }

    public struct ShipTag : IComponentData { }

    public struct LocalPlayerShipTag : IComponentData { }
}
