using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>Ramming damage tuning shared by HUD estimates and future collision parity.</summary>
    public static class ShipComponentRammingSuggestions
    {
        public const float GlobalDamageMultiplier = 3f;
        public const float MassDamageExponent = 0.45f;
        public const float SelfMassDamageExponent = 0.28f;
        public const float SelfToAsteroidDamageRatio = 1.35f;
        public const float MaxSelfImpactDamageFractionOfMaxHealth = 0.22f;
        public const float ReferenceImpactSpeed = 10f;

        public static float ComputeDamageRatingFromFamilyPower(float summedFamilyRammingPower) =>
            Mathf.Max(0.05f, summedFamilyRammingPower) * GlobalDamageMultiplier;

        public static float ComputeMassDamageFactor(float mass, float hullBaselineMass, float exponent = MassDamageExponent)
        {
            float ratio = Mathf.Max(0.1f, mass / Mathf.Max(0.5f, hullBaselineMass));
            return Mathf.Pow(ratio, Mathf.Max(0.05f, exponent));
        }

        public static float ComputeSelfMassDamageFactor(float mass, float hullBaselineMass) =>
            ComputeMassDamageFactor(mass, hullBaselineMass, SelfMassDamageExponent);
    }
}
