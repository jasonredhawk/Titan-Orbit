namespace TitanOrbit
{
    /// <summary>
    /// Single formula for how many loadout slots a ship may fill.
    /// Cards and store equipment share one pool. Base cap is ship level (at least 1).
    /// A rewarded-ad bonus can add one extra slot for the current match only.
    /// <para>
    /// [TITAN-ORBIT] Server <c>MoonOrbitStoreSystem</c> and client orbit UI both call this
    /// so a level-1 hull never shows 2 buyable slots unless the ghosted bonus is 1.
    /// Dedicated server does not invent a bonus — it only trusts the ship component.
    /// </para>
    /// </summary>
    public static class ShipLoadoutCapacity
    {
        /// <summary>
        /// Hard ceiling on rewarded extra slots. The death/ad UI offers one locked row;
        /// the server rejects any bonus above this.
        /// </summary>
        public const int MaxBonusSlots = 1;

        /// <summary>
        /// Loadout cap = max(1, ship level) + clamped bonus.
        /// </summary>
        /// <param name="shipLevel">Ghosted <c>ShipState.ShipLevel</c> (1–7 in play).</param>
        /// <param name="bonusSlots">Ghosted <c>ShipLoadoutState.LoadoutBonusSlots</c> (0 or 1).</param>
        /// <returns>How many cards + equipment items the ship may hold.</returns>
        public static int GetCap(int shipLevel, int bonusSlots)
        {
            // --- Clamp inputs ---
            // [TITAN-ORBIT] Level 0 can appear on a brand-new bake before team pick; treat as 1.
            int baseCap = shipLevel < 1 ? 1 : shipLevel;
            int bonus = bonusSlots;
            if (bonus < 0)
                bonus = 0;
            if (bonus > MaxBonusSlots)
                bonus = MaxBonusSlots;
            return baseCap + bonus;
        }
    }
}
