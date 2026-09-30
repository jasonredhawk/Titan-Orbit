using TitanOrbit.Data;
using UnityEngine;

namespace TitanOrbit.UI
{
    /// <summary>
    /// UI-facing names for <see cref="ShipStatPalette"/>. Upgrade-tree nodes, equipment cards,
    /// and attribute HUD buttons all ask this class so they cannot invent a second set of hues.
    /// Energy stays yellow. Move / Turn stay blue. Gem Cap and Troop Cap stay the two purples.
    /// </summary>
    public static class ShipAbilityCategoryColors
    {
        // --- HUD category shortcuts (five tabs) ---
        public const int PowerBreakdownStatCount = ShipStatPalette.StatCount;

        public static readonly Color WeaponForHud = WithHudAlpha(ShipStatPalette.Offense);
        public static readonly Color HealthForHud = WithHudAlpha(ShipStatPalette.Defense);
        public static readonly Color EnergyForHud = WithHudAlpha(ShipStatPalette.Energy);
        public static readonly Color ShipForHud = WithHudAlpha(ShipStatPalette.Mobility);
        public static readonly Color CargoForHud = WithHudAlpha(ShipStatPalette.Capacity);

        /// <summary>Offense, Defense, Energy, Mobility, Capacity — full alpha for bars/text on dark UI.</summary>
        public static readonly Color[] PowerBreakdownOdEmc = CopyCategoryColors();

        /// <summary>Short labels for orbit ship-tree stat columns (matches ship upgrade menu order).</summary>
        public static readonly string[] PowerBreakdownStatLabels =
        {
            "FP", "BS",
            "HC", "HR",
            "EC", "ER",
            "MS", "TS",
            "GC", "TC"
        };

        /// <summary>Full labels for the ship-tree power legend (matches ship upgrade menu order).</summary>
        public static readonly string[] PowerBreakdownStatFullLabels =
        {
            "Fire Power", "Bullet Speed",
            "Health Cap", "Health Regen",
            "Energy Cap", "Energy Regen",
            "Move Speed", "Turn Speed",
            "Gem Cap", "Troop Cap"
        };

        public const int PowerBreakdownPairCount = PowerBreakdownStatCount / 2;

        /// <summary>Category titles for legend groups (Offense, Defense, Energy, Movement, Capacity).</summary>
        public static readonly string[] PowerBreakdownCategoryTitles =
        {
            "Offense", "Defense", "Energy", "Movement", "Capacity"
        };

        /// <summary>Returns category title for legend pair index (Offense, Defense, …).</summary>
        public static string GetPowerBreakdownCategoryTitle(int pairIndex)
        {
            if (pairIndex < 0 || pairIndex >= PowerBreakdownCategoryTitles.Length)
                return string.Empty;
            return PowerBreakdownCategoryTitles[pairIndex];
        }

        /// <summary>Two tones per category pair — lighter primary stat, darker secondary stat.</summary>
        public static readonly Color[] PowerBreakdownStatColors = BuildPowerBreakdownStatColors();

        public static Color GetPowerBreakdownStatColor(int statIndex) =>
            ShipStatPalette.GetStatColor(statIndex);

        /// <summary>
        /// Top-left vital row (0 health, 1 energy, 2 gems, 3 troops).
        /// Same hues as Health Cap, Energy Cap, Gem Cap, and Troop Cap on the power bar.
        /// </summary>
        public static Color GetShipVitalBarColor(int vitalIndex) =>
            ShipStatPalette.GetVitalBarColor(vitalIndex);

        /// <summary>Same two-tone stat colors as the upgrade-tree power bar, with HUD button alpha.</summary>
        public static Color GetPowerBreakdownStatColorForHud(int statIndex, float alpha = 0.9f)
        {
            Color c = GetPowerBreakdownStatColor(statIndex);
            c.a = alpha;
            return c;
        }

        /// <summary>Own array so a caller writing one slot cannot retint the shared palette.</summary>
        static Color[] CopyCategoryColors()
        {
            Color[] src = ShipStatPalette.CategoryColors;
            var copy = new Color[src.Length];
            for (int i = 0; i < src.Length; i++)
                copy[i] = src[i];
            return copy;
        }

        /// <summary>Category hue at the ability-button alpha. RGB stays the palette color.</summary>
        static Color WithHudAlpha(Color category)
        {
            category.a = 0.9f;
            return category;
        }

        static Color[] BuildPowerBreakdownStatColors()
        {
            // --- Copy so callers can index the array without going through the palette each time ---
            var colors = new Color[PowerBreakdownStatCount];
            for (int i = 0; i < colors.Length; i++)
                colors[i] = ShipStatPalette.GetStatColor(i);
            return colors;
        }
    }
}
