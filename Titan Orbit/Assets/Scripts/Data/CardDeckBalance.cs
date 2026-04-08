namespace TitanOrbit.Data
{
    /// <summary>
    /// Shared tuning for the Astro Eagle card deck. Kept in one place so runtime defaults and editor-generated <see cref="CardData"/> assets match.
    /// Reference: typical family parts sum to ~100+ hull, ~15 thrust, weapon ~15 energy / ~3 regen.
    /// Gem and people bonuses are whole numbers (see <see cref="CargoGemAdd"/> / <see cref="ColonyPeopleAdd"/>); runtime also rounds card adds on the ship.
    /// </summary>
    public static class CardDeckBalance
    {
        public static float KineticDamageMultiplier(int L, int r) =>
            1f + 0.022f * L + 0.012f * (r - 1);

        public static float AegisHullAdd(int L, int r) =>
            4f + L * 4f + (r - 1) * 2.5f;

        /// <summary>Always a whole number (float for <see cref="CardData"/>).</summary>
        public static float CargoGemAdd(int L, int r) =>
            3 + L * 5 + (r - 1) * 3;

        public static float ShardBulletSpeedMultiplier(int L, int r) =>
            1f + 0.022f * L + 0.012f * (r - 1);

        public static float ArcEnergyRegenAdd(int L, int r) =>
            0.15f + L * 0.08f + (r - 1) * 0.05f;

        public static float CapacitorEnergyCapAdd(int L, int r) =>
            3f + L * 3f + (r - 1) * 2f;

        public static float QualityOfLifeMultiplier(int L, int r) =>
            1f + 0.02f * L + 0.012f * (r - 1);

        public static float AfterburnerMoveAdd(int L) =>
            0.35f + L * 0.18f;

        public static float GyroRotationAdd(int L) =>
            4f + L * 5f;

        public static float RegenGelHealthRegenAdd(int L) =>
            0.06f + L * 0.035f;

        public static float MiningRateAdd(int L) =>
            0.12f + L * 0.08f;

        /// <summary>Always a whole number (float for <see cref="CardData"/>).</summary>
        public static float ColonyPeopleAdd(int L) =>
            2 + L * 2;

        public static float TitanforgeDamageMul(int L) =>
            1f + 0.045f * L;

        public static float TitanforgeHullAdd(int L) =>
            6f + L * 4f;

        public static float SuggestedGemCost(int L, int r) =>
            15f + L * 5f + r * 3f;
    }
}
