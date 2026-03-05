using System;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Logical slot type for ship upgrade cards and parts.
    /// Weapon = guns / turrets / damage.
    /// Ship = engines, wings, cockpit, structural/systems pieces.
    /// Cargo = capacity, storage, mining / hauling focused parts.
    /// </summary>
    [Serializable]
    public enum SlotType
    {
        Weapon,
        Ship,
        Cargo
    }
}

