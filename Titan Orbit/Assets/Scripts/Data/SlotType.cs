using System;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Logical slot category for ship upgrade cards and USC module parts. Cards and
    /// <see cref="ShipPartDefinition"/> rows declare which grid tab they belong to at the orbit station.
    /// Consumed by card shop draws, loadout validation, and UI tab filtering. Client/editor only —
    /// not replicated as a standalone ghost field; equipped cards encode stats on the ship entity.
    /// </summary>
    [Serializable]
    public enum SlotType
    {
        // --- Orbit station upgrade grid tabs ---
        /// <summary>[TITAN-ORBIT] Guns, turrets, and damage-focused weapon cards.</summary>
        Weapon,

        /// <summary>[TITAN-ORBIT] Engines, wings, cockpit, and structural/system cards.</summary>
        Ship,

        /// <summary>[TITAN-ORBIT] Capacity, storage, mining, and hauling-focused cards.</summary>
        Cargo
    }
}
