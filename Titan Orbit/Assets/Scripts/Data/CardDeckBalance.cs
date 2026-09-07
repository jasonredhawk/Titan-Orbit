namespace TitanOrbit.Data
{
    // --- Type members ---
    /// <summary>
    /// Shared tuning formulas for the Astro Eagle procedural card deck. Kept in one place so
    /// <see cref="CardDeckRuntimeDefaults"/>, <see cref="CardDataRuntimeRestore"/>, and editor-generated
    /// <see cref="CardData"/> assets stay numerically aligned. Reference baseline: typical family parts
    /// sum to ~100+ hull, ~15 thrust, weapon ~15 energy / ~3 regen. Gem and people bonuses are whole
    /// numbers (see <see cref="CargoGemAdd"/> / <see cref="ColonyPeopleAdd"/>); runtime also rounds card
    /// adds on the ship.
    /// </summary>
    public static class CardDeckBalance
    {
        /// <summary>
        /// Weapon damage multiplier for tiered rarity cards.
        /// <paramref name="L"/> = card level (1–7); <paramref name="r"/> = rarity index (1–5).
        /// </summary>
        public static float KineticDamageMultiplier(int L, int r) =>
            1f + 0.022f * L + 0.012f * (r - 1);

        /// <summary>Flat max-hull bonus for Aegis Plating cards.</summary>
        public static float AegisHullAdd(int L, int r) =>
            4f + L * 4f + (r - 1) * 2.5f;

        /// <summary>Flat gem-capacity bonus — always a whole number stored as float on <see cref="CardData"/>.</summary>
        public static float CargoGemAdd(int L, int r) =>
            3 + L * 5 + (r - 1) * 3;

        /// <summary>Projectile speed multiplier for Shard Projector cards.</summary>
        public static float ShardBulletSpeedMultiplier(int L, int r) =>
            1f + 0.022f * L + 0.012f * (r - 1);

        /// <summary>Energy regeneration per second for Arc Reactor cards.</summary>
        public static float ArcEnergyRegenAdd(int L, int r) =>
            0.15f + L * 0.08f + (r - 1) * 0.05f;

        /// <summary>Flat energy pool bonus for Capacitor Bank cards.</summary>
        public static float CapacitorEnergyCapAdd(int L, int r) =>
            3f + L * 3f + (r - 1) * 2f;

        /// <summary>Multiplier on gem deposit speed (Refinery Drones) and troop transfer (Transit Uplink).</summary>
        public static float QualityOfLifeMultiplier(int L, int r) =>
            1f + 0.02f * L + 0.012f * (r - 1);

        /// <summary>Flat movement speed add for single-rarity Afterburner cards.</summary>
        public static float AfterburnerMoveAdd(int L) =>
            0.35f + L * 0.18f;

        /// <summary>Flat rotation speed add (deg/s) for Gyro Stabilizer cards.</summary>
        public static float GyroRotationAdd(int L) =>
            4f + L * 5f;

        /// <summary>Hull regen per second for Regen Gel cards.</summary>
        public static float RegenGelHealthRegenAdd(int L) =>
            0.06f + L * 0.035f;

        /// <summary>Mining rate add for Mining Laser cargo cards.</summary>
        public static float MiningRateAdd(int L) =>
            0.12f + L * 0.08f;

        /// <summary>Flat troop cap — whole number stored as float on <see cref="CardData"/>.</summary>
        public static float ColonyPeopleAdd(int L) =>
            2 + L * 2;

        /// <summary>Legendary Titanforge weapon damage multiplier (level only, no rarity tier).</summary>
        public static float TitanforgeDamageMul(int L) =>
            1f + 0.045f * L;

        /// <summary>Legendary Titanforge bundled hull bonus.</summary>
        public static float TitanforgeHullAdd(int L) =>
            6f + L * 4f;

        /// <summary>Suggested gem shop price from level and rarity before dynamic economy modifiers.</summary>
        public static float SuggestedGemCost(int L, int r) =>
            15f + L * 5f + r * 3f;
    }
}
