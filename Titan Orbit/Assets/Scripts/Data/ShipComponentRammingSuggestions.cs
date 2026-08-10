using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Shared ramming / grinding damage helpers. Used by the HUD and server
    /// <c>ShipRammingCollisionDamageSystem</c> so display and authority cannot drift.
    /// <para>
    /// [TITAN-ORBIT] Simple product aligned with mobility mass tax:
    /// <c>Impact = rating × totalMass × closingSpeed</c>
    /// <c>GrindPulse = rating × totalMass × taxedAccel × pulseInterval</c>
    /// where <c>totalMass</c> is the same gems/people/ComponentSize mass as SPD/ACC/TURN,
    /// closing speed is live approach (after-tax flight), and taxedAccel is
    /// <see cref="ShipMobilityResolution"/> after-tax acceleration.
    /// </para>
    /// <para>
    /// Balance via <see cref="ShipRammingSettings"/> —
    /// <see cref="GlobalDamageMultiplier"/>, <see cref="SelfToAsteroidDamageRatio"/> —
    /// and each ShipFamilyDefinition component's <c>rammingPower</c>.
    /// No MaxHealth fraction caps — calculated damage is applied as-is.
    /// </para>
    /// </summary>
    public static class ShipComponentRammingSuggestions
    {
        /// <summary>
        /// Fallback when no <see cref="ShipRammingSettings"/> asset is loaded.
        /// Prefer editing Assets/Resources/ShipRammingSettings.asset in the Inspector.
        /// </summary>
        public const float DefaultGlobalDamageMultiplier = 0.5f;

        /// <summary>
        /// Fallback self-to-target ratio when no settings asset is loaded.
        /// Prefer editing Assets/Resources/ShipRammingSettings.asset in the Inspector.
        /// </summary>
        public const float DefaultSelfToAsteroidDamageRatio = 2f;

        /// <summary>
        /// Scales summed family <c>rammingPower</c> into a damage rating.
        /// Lower = softer rams/grinds overall; raise to make ramming meaner.
        /// Source: <see cref="ShipRammingSettings.GlobalDamageMultiplier"/>.
        /// </summary>
        public static float GlobalDamageMultiplier =>
            ShipRammingSettingsCache.ResolveOrDefault().GlobalDamageMultiplier;

        /// <summary>
        /// Self hull chip vs damage dealt on the same hit. Below 1 = you hurt the rock/enemy more
        /// than yourself; above 1 = ramming is self-punishing.
        /// Source: <see cref="ShipRammingSettings.SelfToAsteroidDamageRatio"/>.
        /// </summary>
        public static float SelfToAsteroidDamageRatio =>
            ShipRammingSettingsCache.ResolveOrDefault().SelfToAsteroidDamageRatio;

        /// <summary>
        /// Soft scale when deriving closing speed from PhysX solver impulse (impulse / mass × this).
        /// Impulse is not world u/s — this converts it into an approach-speed hint.
        /// </summary>
        public const float ImpulseToClosingSpeedScale = 0.25f;

        /// <summary>
        /// Upper bound when converting PhysX impulse → closing speed (unit sanitization only).
        /// </summary>
        public const float MaxClosingSpeedFromImpulse = 14f;

        /// <summary>
        /// Ignore grind below this push magnitude (taxedAccel · into-surface) to avoid jitter
        /// when nearly parallel to the rock. Gate only — not a damage scale.
        /// </summary>
        public const float GrindMinPushNewtons = 8f;

        /// <summary>Min seconds between grind damage pulses per asteroid contact (0.25 = 4 pulses/sec).</summary>
        public const float GrindPulseIntervalSeconds = 0.25f;

        /// <summary>Ramming power at version 1 (cockpit) for Scan / ProfileSet seeds.</summary>
        public const float RammingPowerV1 = 1f;

        /// <summary>Ramming power added per version tier when scanning family assets.</summary>
        public const float RammingPowerPerVersion = 0.12f;

        /// <summary>Per-level ramming power as a fraction of base when scanning family assets.</summary>
        public const float RammingPerLevelFractionOfBase = 0.25f;

        /// <summary>Suggested cockpit rammingPower for Scan / ProfileSet at a version tier.</summary>
        public static float GetSuggestedRammingPower(int version)
        {
            int v = Mathf.Max(1, version);
            return RammingPowerV1 + (v - 1) * RammingPowerPerVersion;
        }

        /// <summary>Suggested rammingPowerPerAbilityLevel for Scan / ProfileSet — float only (no RoundToInt).</summary>
        public static float GetSuggestedRammingPowerPerLevel(int version) =>
            Mathf.Max(0f, GetSuggestedRammingPower(version) * RammingPerLevelFractionOfBase);

        /// <summary>
        /// Converts summed family-component <c>rammingPower</c> (level-scaled via
        /// <c>ShipStatApplyLogic</c> → <c>ShipMotorConfig.RammingPower</c>) into a damage rating.
        /// </summary>
        public static float ComputeDamageRatingFromFamilyPower(float summedFamilyRammingPower) =>
            Mathf.Max(0.05f, summedFamilyRammingPower) * GlobalDamageMultiplier;

        /// <summary>
        /// Builds closing speed for impact formulas. Prefers measured approach speed; falls back to
        /// a scaled impulse estimate (PhysX units → world-ish speed).
        /// </summary>
        /// <param name="measuredClosingSpeed">Toroidal / kinematic approach speed (world u/s), or 0.</param>
        /// <param name="estimatedImpulse">PhysX collision impulse, or 0.</param>
        /// <param name="massForImpulse">
        /// Mass used only to interpret impulse (prefer mobility <c>totalMass</c>).
        /// </param>
        public static float ResolveClosingSpeedForDamage(
            float measuredClosingSpeed,
            float estimatedImpulse,
            float massForImpulse)
        {
            float closing = Mathf.Max(0f, measuredClosingSpeed);
            if (estimatedImpulse > 0.01f)
            {
                float mass = Mathf.Max(0.5f, massForImpulse);
                float fromImpulse = (estimatedImpulse / mass) * ImpulseToClosingSpeedScale;
                fromImpulse = Mathf.Min(fromImpulse, MaxClosingSpeedFromImpulse);
                closing = Mathf.Max(closing, fromImpulse);
            }

            return Mathf.Max(0f, closing);
        }

        /// <summary>
        /// Impact damage to an asteroid or enemy hull:
        /// <c>rating × totalMass × closingSpeed</c>.
        /// Closing speed already reflects after-tax flight.
        /// </summary>
        /// <param name="ramDamageRating">Family rammingPower × GlobalDamageMultiplier.</param>
        /// <param name="totalMass">Mobility totalMass (gems + people + ComponentSize).</param>
        /// <param name="closingSpeed">Live approach speed (world u/s).</param>
        public static float ComputeImpactDamage(
            float ramDamageRating,
            float totalMass,
            float closingSpeed)
        {
            return Mathf.Max(
                0f,
                ramDamageRating * Mathf.Max(0f, totalMass) * Mathf.Max(0f, closingSpeed));
        }

        /// <summary>
        /// Self impact chip: same product as <see cref="ComputeImpactDamage"/> ×
        /// <see cref="SelfToAsteroidDamageRatio"/>.
        /// </summary>
        public static float ComputeImpactSelfDamage(
            float ramDamageRating,
            float totalMass,
            float closingSpeed) =>
            Mathf.Max(
                0f,
                ComputeImpactDamage(ramDamageRating, totalMass, closingSpeed) * SelfToAsteroidDamageRatio);

        /// <summary>
        /// Grind pulse to asteroid:
        /// <c>rating × totalMass × taxedAccel × pulseInterval</c>.
        /// <paramref name="taxedAccel"/> is after-tax acceleration (same as drive), not chassis
        /// untaxed <c>ShipMotorConfig.EngineThrust</c>.
        /// </summary>
        public static float ComputeGrindDamagePerPulse(
            float ramDamageRating,
            float totalMass,
            float taxedAccel,
            float pulseInterval)
        {
            return Mathf.Max(
                0f,
                ramDamageRating
                * Mathf.Max(0f, totalMass)
                * Mathf.Max(0f, taxedAccel)
                * Mathf.Max(0f, pulseInterval));
        }

        /// <summary>
        /// Self grind chip for one pulse — grind pulse × <see cref="SelfToAsteroidDamageRatio"/>.
        /// </summary>
        public static float ComputeGrindSelfDamagePerPulse(
            float ramDamageRating,
            float totalMass,
            float taxedAccel,
            float pulseInterval) =>
            Mathf.Max(
                0f,
                ComputeGrindDamagePerPulse(ramDamageRating, totalMass, taxedAccel, pulseInterval)
                * SelfToAsteroidDamageRatio);

        /// <summary>
        /// Scalar push into the surface: outward asteroid normal on XZ · drive force on XZ.
        /// Positive when the ship thrusts into the rock. Used as a grind gate only (not damage scale).
        /// Drive force magnitude should be <b>taxedAccel</b>.
        /// </summary>
        public static float ComputeNormalPushNewtons(Vector3 surfaceOutwardNormalXZ, Vector3 driveForceXZ)
        {
            if (surfaceOutwardNormalXZ.sqrMagnitude < 1e-8f)
                return 0f;
            return Mathf.Max(0f, -Vector3.Dot(driveForceXZ, surfaceOutwardNormalXZ));
        }

        /// <summary>Harder asteroid impacts → higher intensity → gems eject faster/farther.</summary>
        public static float ComputeRamImpactGemExpulsionIntensity(float impactForceNewtons, float damage)
        {
            float forceT = Mathf.InverseLerp(35f, 900f, impactForceNewtons);
            float damageT = Mathf.InverseLerp(1f, 25f, damage);
            return Mathf.Clamp01(Mathf.Max(forceT, damageT) * 0.85f + 0.15f);
        }

        /// <summary>Grinding chip damage → lower intensity than impacts → softer gem launches.</summary>
        /// <param name="taxedAccelOrPush">Taxed accel (or gate push magnitude) for soft intensity.</param>
        public static float ComputeRamGrindGemExpulsionIntensity(float taxedAccelOrPush, float damage)
        {
            float pushT = Mathf.InverseLerp(GrindMinPushNewtons, GrindMinPushNewtons * 10f, taxedAccelOrPush);
            float damageT = Mathf.InverseLerp(0.5f, 10f, damage);
            return Mathf.Clamp01(Mathf.Max(pushT, damageT) * 0.22f + 0.04f);
        }
    }
}
