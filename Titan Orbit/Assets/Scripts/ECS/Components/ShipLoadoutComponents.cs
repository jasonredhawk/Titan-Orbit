using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>Extended ship loadout replicated as ghost fields (Phase 6 parity subset).</summary>
    public struct ShipLoadoutState : IComponentData
    {
        [GhostField] public int RocketCount;
        [GhostField] public int MineCount;
        [GhostField] public int RuntimeBulletIndex;
        [GhostField] public int BranchIndex;
        [GhostField] public int ChassisIndex;
    }

    public struct EquippedCardElement : IBufferElementData
    {
        [GhostField] public int CardId;
    }

    public struct EquippedEquipmentElement : IBufferElementData
    {
        [GhostField] public int ItemType;
        [GhostField] public int RemainingCharges;
        [GhostField] public FixedString64Bytes ComponentId;
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    public partial struct ShipUpgradeSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            // Server validates upgrades; UI sends RPC commands in full parity builds.
        }
    }
}
