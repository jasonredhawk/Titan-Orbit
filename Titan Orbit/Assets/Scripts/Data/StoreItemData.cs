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
    }
}
