using UnityEngine;

namespace TitanOrbit.Data
{
    // --- Type members ---
    /// <summary>
    /// Shared ramming-damage tuning constants and helpers. Used by HUD damage estimates and the
    /// server ShipRammingCollisionDamageSystem so display and authority cannot drift.
    /// Mass scaling uses power-law exponents so heavier hulls hit harder without linear explosion.
    /// <para>
    /// [TITAN-ORBIT] No per-hit MaxHealth fraction caps — calculated damage is applied as-is.
    /// Balance via <see cref="ShipRammingSettings"/> (Inspector asset) —
    /// <see cref="GlobalDamageMultiplier"/>, <see cref="SelfToAsteroidDamageRatio"/> —
    /// and each ShipFamilyDefinition component's <c>rammingPower</c>.
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
        /// Lower = softer rams/grinds overall (asteroid and ship); raise to make ramming meaner.
        /// Source: <see cref="ShipRammingSettings.GlobalDamageMultiplier"/>.
        /// </summary>
        public static float GlobalDamageMultiplier =>
            ShipRammingSettingsCache.ResolveOrDefault().GlobalDamageMultiplier;

        /// <summary>Mass exponent for damage dealt to asteroids / enemy hulls (sublinear cargo weight).</summary>
        public const float MassDamageExponent = 0.45f;

        /// <summary>Softer mass curve for self chip damage than offense mass factor.</summary>
        public const float SelfMassDamageExponent = 0.28f;

        /// <summary>
        /// Self hull chip vs damage dealt on the same hit. Below 1 = you hurt the rock/enemy more
        /// than yourself; above 1 = ramming is self-punishing.
        /// Source: <see cref="ShipRammingSettings.SelfToAsteroidDamageRatio"/>.
        /// </summary>
        public static float SelfToAsteroidDamageRatio =>
            ShipRammingSettingsCache.ResolveOrDefault().SelfToAsteroidDamageRatio;

        /// <summary>Inbound normal speed that maps to speedFactor = 1.</summary>
        public const float ReferenceImpactSpeed = 10f;

        /// <summary>
        /// Soft scale when deriving closing speed from PhysX solver impulse (impulse / mass × this).
        /// Impulse is not world u/s — this converts it into the same units the speed factor expects.
        /// </summary>
        public const float ImpulseToClosingSpeedScale = 0.25f;

        /// <summary>
        /// Upper bound when converting PhysX impulse → closing speed (unit sanitization only —
        /// not a damage fraction of MaxHealth).
        /// </summary>
        public const float MaxClosingSpeedFromImpulse = 14f;

        /// <summary>
        /// [TITAN-ORBIT] Max restitution used in the damage energy budget (not bounce restitution).
        /// Suppressed bounce still chips as if the full elastic Δv were available.
        /// </summary>
        public const float MaxAsteroidRestitutionForDamage = 0.93f;

        /// <summary>Push (N) that maps to pushFactor = 1 on grind pulses.</summary>
        public const float ReferenceGrindPushNewtons = 80f;

        /// <summary>Ignore grind below this push (N) to avoid jitter when nearly parallel to the surface.</summary>
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
        /// Converts summed family-component <c>rammingPower</c> (from ShipFamilyDefinition entries,
        /// level-scaled via <c>ShipStatApplyLogic</c> → <c>ShipMotorConfig.RammingPower</c>) into a
        /// damage rating. Ships with more / stronger ram components hit harder.
        /// </summary>
        public static float ComputeDamageRatingFromFamilyPower(float summedFamilyRammingPower) =>
            Mathf.Max(0.05f, summedFamilyRammingPower) * GlobalDamageMultiplier;

        /// <summary>
        /// Builds closing speed for impact formulas. Prefers measured approach speed; falls back to
        /// a scaled impulse estimate (PhysX units → world-ish speed).
        /// </summary>
        /// <param name="measuredClosingSpeed">Toroidal / kinematic approach speed (world u/s), or 0.</param>
        /// <param name="estimatedImpulse">PhysX collision impulse, or 0.</param>
        /// <param name="rammingMass">Ship ramming mass used to interpret impulse.</param>
        public static float ResolveClosingSpeedForDamage(
            float measuredClosingSpeed,
            float estimatedImpulse,
            float rammingMass)
        {
            float closing = Mathf.Max(0f, measuredClosingSpeed);
            if (estimatedImpulse > 0.01f)
            {
                float mass = Mathf.Max(0.5f, rammingMass);
                float fromImpulse = (estimatedImpulse / mass) * ImpulseToClosingSpeedScale;
                // Sanitize impulse→speed only (not a MaxHealth damage cap).
                fromImpulse = Mathf.Min(fromImpulse, MaxClosingSpeedFromImpulse);
                closing = Mathf.Max(closing, fromImpulse);
            }

            return Mathf.Max(0f, closing);
        }

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

        /// <summary>
        /// Impact damage dealt to an asteroid (or enemy ship hull): rating × massFactor × speed factor.
        /// No MaxHealth fraction clamp — full calculated value.
        /// </summary>
        public static float ComputeImpactDamage(
            float ramDamageRating,
            float mass,
            float hullBaselineMass,
            float inboundNormalSpeed,
            float maxRestitutionForDamage)
        {
            float deltaNormalSpeed =
                (1f + Mathf.Clamp01(maxRestitutionForDamage)) * Mathf.Max(0f, inboundNormalSpeed);
            float speedFactor = deltaNormalSpeed / Mathf.Max(0.1f, ReferenceImpactSpeed);
            float massFactor = ComputeMassDamageFactor(mass, hullBaselineMass);
            return Mathf.Max(0f, ramDamageRating * massFactor * speedFactor);
        }

        /// <summary>
        /// Self impact chip: softer mass curve × <see cref="SelfToAsteroidDamageRatio"/>.
        /// No MaxHealth fraction clamp — full calculated value.
        /// </summary>
        public static float ComputeImpactSelfDamage(
            float ramDamageRating,
            float mass,
            float hullBaselineMass,
            float inboundNormalSpeed,
            float maxRestitutionForDamage)
        {
            float deltaNormalSpeed =
                (1f + Mathf.Clamp01(maxRestitutionForDamage)) * Mathf.Max(0f, inboundNormalSpeed);
            float speedFactor = deltaNormalSpeed / Mathf.Max(0.1f, ReferenceImpactSpeed);
            float selfMassFactor = ComputeSelfMassDamageFactor(mass, hullBaselineMass);
            return Mathf.Max(0f, ramDamageRating * selfMassFactor * speedFactor * SelfToAsteroidDamageRatio);
        }

        /// <summary>Grind pulse to asteroid: rating × massFactor × push factor × interval. No DPS cap.</summary>
        public static float ComputeGrindDamagePerPulse(
            float ramDamageRating,
            float mass,
            float hullBaselineMass,
            float pushNewtons,
            float pulseInterval)
        {
            float pushFactor = pushNewtons / Mathf.Max(1f, ReferenceGrindPushNewtons);
            float massFactor = ComputeMassDamageFactor(mass, hullBaselineMass);
            return Mathf.Max(0f, ramDamageRating * massFactor * pushFactor * pulseInterval);
        }

        /// <summary>
        /// Self grind chip for one pulse — softer mass curve × self ratio. No MaxHealth fraction clamp.
        /// </summary>
        public static float ComputeGrindSelfDamagePerPulse(
            float ramDamageRating,
            float mass,
            float hullBaselineMass,
            float pushNewtons,
            float pulseInterval)
        {
            float pushFactor = pushNewtons / Mathf.Max(1f, ReferenceGrindPushNewtons);
            float selfMassFactor = ComputeSelfMassDamageFactor(mass, hullBaselineMass);
            return Mathf.Max(
                0f,
                ramDamageRating * selfMassFactor * pushFactor * pulseInterval * SelfToAsteroidDamageRatio);
        }

        /// <summary>
        /// Scalar push into the surface (Newtons): outward asteroid normal on XZ · drive force on XZ.
        /// Positive when the ship thrusts into the rock (force opposes the outward normal).
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
        public static float ComputeRamGrindGemExpulsionIntensity(float pushNewtons, float damage)
        {
            float pushT = Mathf.InverseLerp(GrindMinPushNewtons, GrindMinPushNewtons * 10f, pushNewtons);
            float damageT = Mathf.InverseLerp(0.5f, 10f, damage);
            return Mathf.Clamp01(Mathf.Max(pushT, damageT) * 0.22f + 0.04f);
        }
    }
}
