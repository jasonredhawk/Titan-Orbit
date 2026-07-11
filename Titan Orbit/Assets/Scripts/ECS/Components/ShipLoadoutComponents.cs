using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Extended ship loadout replicated as ghost fields. Tracks consumables (rockets, mines),
    /// runtime bullet bank index, upgrade branch, and chassis index. Equipped cards and store
    /// equipment live in DynamicBuffers on the same ship entity. Server validates purchases via
    /// orbit-store RPC handlers; this struct holds the authoritative result.
    /// </summary>
    public struct ShipLoadoutState : IComponentData
    {
        // --- Type members ---
        /// <summary>Remaining homing rocket charges (store consumable).</summary>
        [GhostField] public int RocketCount;

        /// <summary>Remaining deployable mine charges.</summary>
        [GhostField] public int MineCount;

        /// <summary>Index into bullet VFX/stats bank for current weapon appearance.</summary>
        [GhostField] public int RuntimeBulletIndex;

        /// <summary>Upgrade tree branch — selects chassis row from PlanetShipFamilyConfig.</summary>
        [GhostField] public int BranchIndex;

        /// <summary>Chassis variant within the family branch (visual + base stats).</summary>
        [GhostField] public int ChassisIndex;
    }

    /// <summary>One equipped card id in the ship's card buffer (supports multiple slots).</summary>
    public struct EquippedCardElement : IBufferElementData
    {
        /// <summary>Catalog card id from ship component data.</summary>
        [GhostField] public int CardId;
    }

    /// <summary>One equipped store item with remaining charges (rockets, shields, etc.).</summary>
    public struct EquippedEquipmentElement : IBufferElementData
    {
        /// <summary>Store item type enum value.</summary>
        [GhostField] public int ItemType;

        /// <summary>Uses left before the item is consumed or removed.</summary>
        [GhostField] public int RemainingCharges;

        /// <summary>Stable component id string for stat lookup in ShipPartCatalog.</summary>
        [GhostField] public FixedString64Bytes ComponentId;
    }

    /// <summary>
    /// Placeholder server system for ship upgrades from orbit store UI. Full parity builds send
    /// PurchaseShipUpgradeCommand RPCs from MoonOrbitStoreSystem instead of polling here.
    /// World: ServerSimulation.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    public partial struct ShipUpgradeSystem : ISystem
    {
        /// <summary>Intentionally empty — upgrade validation lives in RPC handlers today.</summary>
        public void OnUpdate(ref SystemState state)
        {
            // [TITAN-ORBIT] Server validates upgrades via RPC; this system reserves a future hook.
        }
    }
}
