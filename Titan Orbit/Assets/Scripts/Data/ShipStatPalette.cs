using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// One color language for ship stats. Ability buttons, the orbit-menu power bar,
    /// the top-left vital bars, nameplate cargo bars, and troop meters all read hues from here.
    /// <para>
    /// Five categories, in power-bar order: Offense, Defense, Energy, Mobility, Capacity.
    /// Each category owns two slots (a lighter primary and a darker secondary). Mobility is
    /// the only blue — Move Speed and Turn Speed. Energy is yellow. Gem Cap and Troop Cap
    /// are the two purples. Do not paint energy blue or troops yellow; those hues are already taken.
    /// </para>
    /// </summary>
    public static class ShipStatPalette
    {
        public const int StatCount = 10;
        public const int CategoryCount = 5;

        /// <summary>Fire Power / Bullet Speed.</summary>
        public static readonly Color Offense = new Color(0.9f, 0.35f, 0.2f, 1f);

        /// <summary>Health Cap / Health Regen.</summary>
        public static readonly Color Defense = new Color(0.2f, 0.85f, 0.4f, 1f);

        /// <summary>Energy Cap / Energy Regen. Yellow — blue belongs to Move / Turn.</summary>
        public static readonly Color Energy = new Color(0.95f, 0.8f, 0.2f, 1f);

        /// <summary>Move Speed / Turn Speed. The only blue in the stat palette.</summary>
        public static readonly Color Mobility = new Color(0.2f, 0.7f, 0.95f, 1f);

        /// <summary>Gem Cap / Troop Cap.</summary>
        public static readonly Color Capacity = new Color(0.65f, 0.4f, 0.9f, 1f);

        /// <summary>Offense through Capacity, full alpha. Same order as the power-bar pairs.</summary>
        public static readonly Color[] CategoryColors =
        {
            Offense, Defense, Energy, Mobility, Capacity
        };

        /// <summary>
        /// Top-left HUD row → power-bar slot.
        /// 0 health → Health Cap (2), 1 energy → Energy Cap (4),
        /// 2 gems → Gem Cap (8), 3 troops → Troop Cap (9).
        /// </summary>
        static readonly int[] VitalStatSlots = { 2, 4, 8, 9 };

        /// <summary>
        /// Two-tone color for power-bar slot <paramref name="statIndex"/> (0 = Fire Power … 9 = Troop Cap).
        /// Even slots are the lighter primary; odd slots are the darker secondary of the same category.
        /// </summary>
        public static Color GetStatColor(int statIndex)
        {
            if (statIndex < 0 || statIndex >= StatCount)
                return Color.white;

            int category = statIndex / 2;
            Color baseColor = CategoryColors[category];
            bool primary = (statIndex & 1) == 0;
            // Same mix the orbit-menu bar has always used: lift the first stat, sink the second.
            return primary
                ? Color.Lerp(baseColor, Color.white, 0.28f)
                : Color.Lerp(baseColor, Color.black, 0.22f);
        }

        /// <summary>
        /// Color for a live pool bar: health, energy, gems, or troops (indices 0–3).
        /// Matches the Health Cap, Energy Cap, Gem Cap, and Troop Cap slots so the
        /// top-left HUD, nameplate, and orbit power bar agree.
        /// </summary>
        public static Color GetVitalBarColor(int vitalIndex)
        {
            if (vitalIndex < 0 || vitalIndex >= VitalStatSlots.Length)
                return Color.white;
            return GetStatColor(VitalStatSlots[vitalIndex]);
        }
    }
}
