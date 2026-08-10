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
    /// <para>
    /// [NETCODE] Must be baked on the starship ghost (<see cref="Authoring.StarshipGhostAuthoring"/>).
    /// Adding this component only at runtime does <b>not</b> replicate GhostFields — B-key
    /// <see cref="RuntimeBulletIndex"/> stays stuck at 0 on clients.
    /// </para>
    /// </summary>
    public struct ShipLoadoutState : IComponentData
    {
        // --- Type members ---
        /// <summary>Remaining homing rocket charges (store consumable).</summary>
        [GhostField] public int RocketCount;

        /// <summary>Remaining deployable mine charges.</summary>
        [GhostField] public int MineCount;

        /// <summary>
        /// Index into <c>BulletVfxBank</c> categories for current weapon VFX.
        /// B-key cycles via <see cref="ShipCycleBulletSystem"/>; wraps at CategoryCount.
        /// </summary>
        [GhostField] public int RuntimeBulletIndex;

        /// <summary>Upgrade tree branch — selects chassis row from PlanetShipFamilyConfig.</summary>
        [GhostField] public int BranchIndex;

        /// <summary>Chassis variant within the family branch (visual + base stats).</summary>
        [GhostField] public int ChassisIndex;
    }

    /// <summary>
    /// One equipped upgrade card in the ship's card buffer (supports multiple slots).
    /// [NETCODE] Uses a stable string id (same as <see cref="Data.CardData.GetStableCardId"/>) so
    /// orbit spin/take and UI sync do not need a separate int registry.
    /// </summary>
    public struct EquippedCardElement : IBufferElementData
    {
        /// <summary>Catalog card id from the ship family deck (e.g. AstroEagle_Engine_2_L).</summary>
        [GhostField] public FixedString64Bytes CardId;
    }

    /// <summary>One equipped store item with remaining charges (rockets, shields, etc.).</summary>
    public struct EquippedEquipmentElement : IBufferElementData
    {
        /// <summary>Store item type enum value.</summary>
        [GhostField] public int ItemType;

        /// <summary>Uses left before the item is consumed or removed.</summary>
        [GhostField] public int RemainingCharges;

        /// <summary>
        /// Ship level at purchase time for leveled drones (fighter / mining / shield).
        /// [TITAN-ORBIT] Damage, HP, cost, and visual size use this fixed level — drones do not
        /// auto-upgrade when the ship levels. Non-drone items store 0 (ignored).
        /// </summary>
        [GhostField] public int ItemLevel;

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
