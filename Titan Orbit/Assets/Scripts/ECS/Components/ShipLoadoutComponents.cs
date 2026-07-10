using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Extended ship loadout replicated as ghost fields. Tracks consumables (rockets, mines),
    /// upgrade branch selection, and chassis index. Equipped cards/equipment live in DynamicBuffers.
    /// ShipUpgradeSystem placeholder — full store validation lives in orbit store RPC handlers.
    /// </summary>
    public struct ShipLoadoutState : IComponentData
    {
        [GhostField] public int RocketCount;
        [GhostField] public int MineCount;
        [GhostField] public int RuntimeBulletIndex;
        /// <summary>Upgrade tree branch (affects chassis id from PlanetShipFamilyConfig).</summary>
        [GhostField] public int BranchIndex;
        [GhostField] public int ChassisIndex;
    }

    /// <summary>Equipped card ids on the ship (buffer supports multiple card slots).</summary>
    public struct EquippedCardElement : IBufferElementData
    {
        [GhostField] public int CardId;
    }

    /// <summary>Equipped store items with remaining charges (rockets, shields, etc.).</summary>
    public struct EquippedEquipmentElement : IBufferElementData
    {
        [GhostField] public int ItemType;
        [GhostField] public int RemainingCharges;
        [GhostField] public FixedString64Bytes ComponentId;
    }

    /// <summary>
    /// Placeholder for server-validated ship upgrades from orbit store UI.
    /// Full parity builds send PurchaseShipUpgradeCommand RPCs instead.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    public partial struct ShipUpgradeSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            // Server validates upgrades; UI sends RPC commands in full parity builds.
        }
    }
}
