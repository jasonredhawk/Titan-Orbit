namespace TitanOrbit.Data
{
    /// <summary>
    /// Shared tuning for the scaled Astro Eagle card deck. Kept in one place so runtime defaults and editor-generated <see cref="CardData"/> assets match.
    /// Reference: typical family parts sum to ~100+ hull, ~15 thrust, weapon ~15 energy / ~3 regen.
    /// </summary>
    public static class CardDeckBalance
    {
        public static float KineticDamageMultiplier(int L, int r) => 1f + 0.012f * L + 0.006f * (r - 1);

        public static float AegisHullAdd(int L, int r) => 1.5f + L * 2f + (r - 1) * 1.25f;

        public static float CargoGemAdd(int L, int r) => 1f + L * 2f + (r - 1) * 1.5f;

        public static float ShardBulletSpeedMultiplier(int L, int r) => 1f + 0.012f * L + 0.006f * (r - 1);

        public static float ArcEnergyRegenAdd(int L, int r) => 0.05f + L * 0.035f + (r - 1) * 0.02f;

        public static float CapacitorEnergyCapAdd(int L, int r) => 0.5f + L * 1.25f + (r - 1) * 0.75f;

        public static float QualityOfLifeMultiplier(int L, int r) => 1f + 0.01f * L + 0.006f * (r - 1);

        public static float AfterburnerMoveAdd(int L) => 0.12f + L * 0.06f;

        public static float GyroRotationAdd(int L) => 1.5f + L * 2f;

        public static float RegenGelHealthRegenAdd(int L) => 0.02f + L * 0.012f;

        public static float MiningRateAdd(int L) => 0.04f + L * 0.025f;

        public static float ColonyPeopleAdd(int L) => 0.5f + L * 0.4f;

        public static float TitanforgeDamageMul(int L) => 1f + 0.02f * L;

        public static float TitanforgeHullAdd(int L) => 2f + L * 1.5f;

        public static float SuggestedGemCost(int L, int r) => 12f + L * 4f + r * 2.5f;
    }
}
