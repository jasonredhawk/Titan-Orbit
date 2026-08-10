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

        /// <summary>[TITAN-ORBIT] Consumable rockets — pack of 4, fired with Q.</summary>
        SmallRockets,

        /// <summary>[TITAN-ORBIT] Consumable rockets — pack of 2, fired with Q.</summary>
        LargeRockets,

        /// <summary>[TITAN-ORBIT] Deployable mines — pack of 4, placed with E.</summary>
        SmallMines,

        /// <summary>[TITAN-ORBIT] Deployable mines — pack of 2, placed with E.</summary>
        LargeMines,

        // --- Authored ship parts ---
        /// <summary>
        /// Authored ship-family component; component id stored in
        /// <see cref="Entities.EquippedEquipmentEntry.componentId"/>.
        /// </summary>
        ShipComponent
    }
}
