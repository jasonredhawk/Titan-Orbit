using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>Per-stat gem upgrade levels (0–ShipLevel each). Synced for the bottom upgrade HUD.</summary>
    public struct ShipAttributeUpgradeState : IComponentData
    {
        [GhostField] public int FirePower;
        [GhostField] public int BulletSpeed;
        [GhostField] public int MaxHealth;
        [GhostField] public int HealthRegen;
        [GhostField] public int EnergyCapacity;
        [GhostField] public int EnergyRegen;
        [GhostField] public int MovementSpeed;
        [GhostField] public int RotationSpeed;
        [GhostField] public int GemCapacity;
        [GhostField] public int PeopleCapacity;
    }
}
