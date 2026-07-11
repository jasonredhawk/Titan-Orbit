namespace TitanOrbit.Data
{
    /// <summary>
    /// Rarity tier for upgrade cards. Drives shop draw weights in <see cref="Systems.CardShopSystem"/>,
    /// UI tinting in orbit station and HUD, and designer expectations for power level. Serialized
    /// as int 1–5 to match legacy <see cref="CardData"/> assets on disk.
    /// </summary>
    public enum CardRarity
    {
        // --- Shop draw tiers (1 = most common) ---
        /// <summary>Most common; highest shop weight.</summary>
        Common = 1,

        /// <summary>Second tier; moderate shop weight.</summary>
        Uncommon = 2,

        /// <summary>Mid tier; lower shop weight.</summary>
        Rare = 3,

        /// <summary>High tier; rare in shop draws.</summary>
        Epic = 4,

        /// <summary>Top tier; lowest shop weight.</summary>
        Legendary = 5
    }
}
