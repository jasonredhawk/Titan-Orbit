using UnityEngine;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Canonical colors for the five ship-ability categories used by the bottom upgrade bar
    /// (<see cref="ShipAttributeUpgradeHUD"/>). Power breakdown O/D/E/M/C uses the same palette in order:
    /// Offense (weapon), Defense (health), Energy, Mobility (ship/movement), Capacity (cargo).
    /// </summary>
    public static class ShipAbilityCategoryColors
    {
        public static readonly Color WeaponForHud = new Color(0.9f, 0.35f, 0.2f, 0.9f);
        public static readonly Color HealthForHud = new Color(0.2f, 0.85f, 0.4f, 0.9f);
        public static readonly Color EnergyForHud = new Color(0.95f, 0.8f, 0.2f, 0.9f);
        public static readonly Color ShipForHud = new Color(0.2f, 0.7f, 0.95f, 0.9f);
        public static readonly Color CargoForHud = new Color(0.65f, 0.4f, 0.9f, 0.9f);

        /// <summary>Offense, Defense, Energy, Mobility, Capacity — full alpha for bars/text on dark UI.</summary>
        public static readonly Color[] PowerBreakdownOdEmc =
        {
            new Color(0.9f, 0.35f, 0.2f, 1f),
            new Color(0.2f, 0.85f, 0.4f, 1f),
            new Color(0.95f, 0.8f, 0.2f, 1f),
            new Color(0.2f, 0.7f, 0.95f, 1f),
            new Color(0.65f, 0.4f, 0.9f, 1f)
        };
    }
}
