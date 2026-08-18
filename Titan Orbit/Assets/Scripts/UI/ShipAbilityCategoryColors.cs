using UnityEngine;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Canonical two-tone color palette for the ten ship power-bar stats (offense through capacity).
    /// Shared by upgrade-tree nodes, equipment cards, and attribute HUD buttons via
    /// <see cref="GetPowerBreakdownStatColor"/> / <see cref="GetPowerBreakdownStatColorForHud"/>.
    /// </summary>
    public static class ShipAbilityCategoryColors
    {
        // --- HUD category shortcuts (five tabs) ---
        public const int PowerBreakdownStatCount = 10;

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

        public static Color GetPowerBreakdownStatColor(int statIndex)
        {
            if (statIndex < 0 || statIndex >= PowerBreakdownStatColors.Length)
                return Color.white;
            return PowerBreakdownStatColors[statIndex];
        }

        /// <summary>Same two-tone stat colors as the upgrade-tree power bar, with HUD button alpha.</summary>
        public static Color GetPowerBreakdownStatColorForHud(int statIndex, float alpha = 0.9f)
        {
            Color c = GetPowerBreakdownStatColor(statIndex);
            c.a = alpha;
            return c;
        }

        private static Color[] BuildPowerBreakdownStatColors()
        {
            // --- Two tones per ODEMC category pair ---
            var colors = new Color[PowerBreakdownStatCount];
            for (int category = 0; category < PowerBreakdownOdEmc.Length; category++)
            {
                Color baseColor = PowerBreakdownOdEmc[category];
                colors[category * 2] = Color.Lerp(baseColor, Color.white, 0.28f);
                colors[category * 2 + 1] = Color.Lerp(baseColor, Color.black, 0.22f);
            }

            return colors;
        }
    }
}
