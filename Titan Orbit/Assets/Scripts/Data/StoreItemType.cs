namespace TitanOrbit.Data
{
    /// <summary>
    /// Item kinds sold at the home-planet / moon-dock store. Prices, pack sizes, and UI strings
    /// live in <see cref="StoreItemData"/>; this enum is the stable id passed to purchase RPCs
    /// and <see cref="Systems.HomePlanetStoreSystem"/>. Drones and consumables are support gear;
    /// <see cref="ShipComponent"/> is an authored family part from the upgrade tree.
    /// </summary>
    public enum StoreItemType
    {
        // --- Autonomous drones ---
        /// <summary>[TITAN-ORBIT] Autonomous fighter that attacks enemy ships.</summary>
        FighterDrone,

        /// <summary>[TITAN-ORBIT] Autonomous shield that blocks incoming fire.</summary>
        ShieldDrone,

        /// <summary>[TITAN-ORBIT] Autonomous miner for nearby asteroids.</summary>
        MiningDrone,

        /// <summary>
        /// [TITAN-ORBIT] Canonical store rockets — pack of 2, fired with ALT.
        /// Enum value stays <c>SmallRockets</c> so ghost <c>ItemType</c> ints stay stable.
        /// </summary>
        SmallRockets,

        /// <summary>
        /// Legacy rocket SKU (hidden from Orbit Menu). Still treated as a rocket if equipped.
        /// </summary>
        LargeRockets,

        /// <summary>
        /// [TITAN-ORBIT] Canonical store mines — pack of 4, placed with E.
        /// Enum value stays <c>SmallMines</c> so ghost <c>ItemType</c> ints stay stable.
        /// </summary>
        SmallMines,

        /// <summary>
        /// Legacy mine SKU (hidden from Orbit Menu). Still treated as a mine if equipped.
        /// </summary>
        LargeMines,

        // --- Authored ship parts ---
        /// <summary>
        /// Authored ship-family component; component id stored in
        /// <see cref="Entities.EquippedEquipmentEntry.componentId"/>.
        /// </summary>
        ShipComponent
    }
}
