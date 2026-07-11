using UnityEngine;

namespace TitanOrbit.Data
{
    // --- Type members ---
    /// <summary>
    /// Shared ramming-damage tuning constants and helpers. Used by HUD damage estimates and should stay
    /// aligned with future collision parity. Mass scaling uses power-law exponents so heavier hulls hit harder
    /// without linear explosion. [TITAN-ORBIT] Self-damage is capped as a fraction of max health on impact.
    /// </summary>
    public static class ShipComponentRammingSuggestions
    {
        public const float GlobalDamageMultiplier = 3f;
        public const float MassDamageExponent = 0.45f;
        public const float SelfMassDamageExponent = 0.28f;
        public const float SelfToAsteroidDamageRatio = 1.35f;
        public const float MaxSelfImpactDamageFractionOfMaxHealth = 0.22f;
        public const float ReferenceImpactSpeed = 10f;

        /// <summary>Converts summed family ramming power into a damage rating (× global multiplier).</summary>
        public static float ComputeDamageRatingFromFamilyPower(float summedFamilyRammingPower) =>
            Mathf.Max(0.05f, summedFamilyRammingPower) * GlobalDamageMultiplier;

        /// <summary>
        /// Mass-based damage factor: ratio of ship mass to hull baseline, raised to <paramref name="exponent"/>.
        /// </summary>
        public static float ComputeMassDamageFactor(float mass, float hullBaselineMass, float exponent = MassDamageExponent)
        {
            float ratio = Mathf.Max(0.1f, mass / Mathf.Max(0.5f, hullBaselineMass));
            return Mathf.Pow(ratio, Mathf.Max(0.05f, exponent));
        }

        /// <summary>Self-damage mass factor — softer exponent than damage dealt to targets.</summary>
        public static float ComputeSelfMassDamageFactor(float mass, float hullBaselineMass) =>
            ComputeMassDamageFactor(mass, hullBaselineMass, SelfMassDamageExponent);
    }
}
