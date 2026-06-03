using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Static store item definitions: price, display name, pack size for consumables.
    /// </summary>
    public static class StoreItemData
    {
        public static float GetPrice(StoreItemType item)
        {
            switch (item)
            {
                case StoreItemType.FighterDrone: return 80f;
                case StoreItemType.ShieldDrone: return 100f;
                case StoreItemType.MiningDrone: return 70f;
                case StoreItemType.SmallRockets: return 50f;
                case StoreItemType.LargeRockets: return 90f;
                case StoreItemType.SmallMines: return 45f;
                case StoreItemType.LargeMines: return 85f;
                default: return 999f;
            }
        }

        public static string GetDisplayName(StoreItemType item)
        {
            switch (item)
            {
                case StoreItemType.FighterDrone: return "Fighter Drone";
                case StoreItemType.ShieldDrone: return "Shield Drone";
                case StoreItemType.MiningDrone: return "Mining Drone";
                case StoreItemType.SmallRockets: return "Small Rockets (x4)";
                case StoreItemType.LargeRockets: return "Large Rockets (x2)";
                case StoreItemType.SmallMines: return "Small Mines (x4)";
                case StoreItemType.LargeMines: return "Large Mines (x2)";
                default: return item.ToString();
            }
        }

        /// <summary>Pack size for rockets/mines; drones are 1 per purchase.</summary>
        public static int GetPackSize(StoreItemType item)
        {
            switch (item)
            {
                case StoreItemType.SmallRockets:
                case StoreItemType.SmallMines: return 4;
                case StoreItemType.LargeRockets:
                case StoreItemType.LargeMines: return 2;
                default: return 1;
            }
        }

        /// <summary>Compact card title for moon dock store row.</summary>
        public static string GetShortDisplayName(StoreItemType item)
        {
            switch (item)
            {
                case StoreItemType.FighterDrone: return "Fighter";
                case StoreItemType.ShieldDrone: return "Shield";
                case StoreItemType.MiningDrone: return "Mining";
                case StoreItemType.SmallRockets: return "Rockets S";
                case StoreItemType.LargeRockets: return "Rockets L";
                case StoreItemType.SmallMines: return "Mines S";
                case StoreItemType.LargeMines: return "Mines L";
                default: return item.ToString();
            }
        }

        /// <summary>
        /// Stat index (0–9) into <see cref="TitanOrbit.UI.ShipAbilityCategoryColors"/> for card tinting —
        /// same palette as the bottom ship upgrade bar.
        /// </summary>
        public static int GetAbilityColorStatIndex(StoreItemType item)
        {
            switch (item)
            {
                case StoreItemType.FighterDrone: return 0; // Fire Power
                case StoreItemType.SmallRockets: return 1; // Bullet Speed
                case StoreItemType.LargeRockets: return 0;
                case StoreItemType.ShieldDrone: return 2; // Health Cap
                case StoreItemType.SmallMines: return 3; // Health Regen
                case StoreItemType.LargeMines: return 3;
                case StoreItemType.MiningDrone: return 8; // Gem Cap
                default: return 0;
            }
        }

        public static bool IsDrone(StoreItemType item)
        {
            return item == StoreItemType.FighterDrone
                || item == StoreItemType.ShieldDrone
                || item == StoreItemType.MiningDrone;
        }

        public static bool IsShipComponent(StoreItemType item) => item == StoreItemType.ShipComponent;

        public static bool IsSupportItem(StoreItemType item) => !IsShipComponent(item);

        /// <summary>Short description for equipment slot UI.</summary>
        public static string GetDescription(StoreItemType item)
        {
            switch (item)
            {
                case StoreItemType.FighterDrone: return "Attacks enemy ships.";
                case StoreItemType.ShieldDrone: return "Blocks incoming fire.";
                case StoreItemType.MiningDrone: return "Mines nearby asteroids.";
                case StoreItemType.SmallRockets: return "Q to fire · pack of 4.";
                case StoreItemType.LargeRockets: return "Q to fire · pack of 2.";
                case StoreItemType.SmallMines: return "E to place · pack of 4.";
                case StoreItemType.LargeMines: return "E to place · pack of 2.";
                default: return string.Empty;
            }
        }

        /// <summary>Large glyph shown in the card icon area when no sprite is assigned.</summary>
        public static string GetIconGlyph(StoreItemType item)
        {
            switch (item)
            {
                case StoreItemType.FighterDrone: return "\u2694"; // crossed swords
                case StoreItemType.ShieldDrone: return "\u25C8"; // diamond
                case StoreItemType.MiningDrone: return "\u2692"; // pick
                case StoreItemType.SmallRockets:
                case StoreItemType.LargeRockets: return "\u25B2"; // triangle
                case StoreItemType.SmallMines:
                case StoreItemType.LargeMines: return "\u25CF"; // circle
                default: return "?";
            }
        }
    }
}
