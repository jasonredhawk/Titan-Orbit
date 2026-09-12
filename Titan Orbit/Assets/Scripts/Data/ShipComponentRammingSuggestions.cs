using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Shared ramming / grinding damage helpers. Used by the HUD and server
    /// <c>ShipRammingCollisionDamageSystem</c> so display and authority cannot drift.
    /// <para>
    /// [TITAN-ORBIT] The RAM chip (family <c>rammingPower</c>, e.g. 5.8) is grind HP per second
    /// at <see cref="MassReference"/>. Mass stays in both formulas so a heavier hull or a
    /// cargo-loaded ship hits harder:
    /// <c>grindDps = rating × (totalMass / MassReference)</c>
    /// <c>impact = grindDps × (1 + closingSpeed / RamClosingSpeedForDouble)</c>
    /// where <c>rating = rammingPower × GlobalDamageMultiplier</c> and <c>totalMass</c> is the
    /// same gems/people/ComponentSize mass as SPD/ACC/TURN.
    /// After-tax Accel is a grind <b>gate</b> only (must thrust into the rock) — it does not
    /// scale damage. Closing speed is live approach (after-tax flight).
    /// </para>
    /// <para>
    /// Balance via <see cref="ShipRammingSettings"/> —
    /// <see cref="GlobalDamageMultiplier"/>, <see cref="MassReference"/>,
    /// <see cref="RamClosingSpeedForDouble"/>, <see cref="SelfToAsteroidDamageRatio"/> —
    /// grind pulse interval on <see cref="AsteroidSettings"/> —
    /// and each ShipFamilyDefinition component's <c>rammingPower</c>.
    /// No MaxHealth fraction caps — calculated damage is applied as-is.
    /// </para>
    /// </summary>
    public static class ShipComponentRammingSuggestions
    {
        /// <summary>
        /// Fallback when no <see cref="ShipRammingSettings"/> asset is loaded.
        /// Prefer editing Assets/Resources/ShipRammingSettings.asset in the Inspector.
        /// 1 = RAM chip is grind DPS at <see cref="DefaultMassReference"/>.
        /// </summary>
        public const float DefaultGlobalDamageMultiplier = 1f;

        /// <summary>
        /// Fallback self-to-target ratio when no settings asset is loaded.
        /// Prefer editing Assets/Resources/ShipRammingSettings.asset in the Inspector.
        /// </summary>
        public const float DefaultSelfToAsteroidDamageRatio = 2f;

        /// <summary>
        /// Fallback mobility totalMass at which grind DPS equals the RAM chip.
        /// Typical empty hull after HullMassScale lands near this.
        /// </summary>
        public const float DefaultMassReference = 10f;

        /// <summary>
        /// Fallback closing speed (world u/s) at which a ram burst is 2× grind DPS.
        /// </summary>
        public const float DefaultRamClosingSpeedForDouble = 10f;

        /// <summary>
        /// Scales summed family <c>rammingPower</c> into a damage rating.
        /// 1 = the RAM chip is grind DPS at <see cref="MassReference"/>.
        /// Source: <see cref="ShipRammingSettings.GlobalDamageMultiplier"/>.
        /// </summary>
        public static float GlobalDamageMultiplier =>
            ShipRammingSettingsCache.ResolveOrDefault().GlobalDamageMultiplier;

        /// <summary>
        /// Mobility totalMass at which grind DPS equals the RAM chip × Global.
        /// Heavier ships scale above; lighter ships scale below.
        /// Source: <see cref="ShipRammingSettings.MassReference"/>.
        /// </summary>
        public static float MassReference =>
            Mathf.Max(0.01f, ShipRammingSettingsCache.ResolveOrDefault().MassReference);

        /// <summary>
        /// Closing speed that doubles grind DPS into a ram burst
        /// (<c>ramMul = 1 + closing / this</c>).
        /// Source: <see cref="ShipRammingSettings.RamClosingSpeedForDouble"/>.
        /// </summary>
        public static float RamClosingSpeedForDouble =>
            Mathf.Max(0.01f, ShipRammingSettingsCache.ResolveOrDefault().RamClosingSpeedForDouble);

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

        /// <summary>
        /// Extra visual scale on a ram/grind pulse that kills the rock. Grind chips stay at
        /// <see cref="TitanOrbit.Simulation.BulletVisualScale.ComputePerShotScale"/>; the finishing
        /// blow is a bigger boom.
        /// </summary>
        public const float RamKillImpactVisualScale = 1.75f;

        /// <summary>
        /// Fallback grind pulse interval when <see cref="AsteroidSettings"/> is missing or authored 0
        /// (old YAML without the field deserializes as 0 and would zero grind DPS).
        /// 0.25 seconds = 4 Hz = four gems per second, each sized to that pulse's damage.
        /// </summary>
        public const float DefaultGrindPulseIntervalSeconds = 0.25f;

        /// <summary>
        /// Min seconds between grind damage pulses per asteroid contact.
        /// Source: <see cref="AsteroidSettings.GrindPulseIntervalSeconds"/> (default 0.25 = 4 Hz).
        /// Damage per pulse is grind DPS × this interval, then one gem spawns with that pulse's
        /// expelled cargo — 4 Hz means 4 gems/s, no banking.
        /// </summary>
        public static float GrindPulseIntervalSeconds
        {
            get
            {
                float authored = AsteroidSettingsCache.ResolveOrDefault().GrindPulseIntervalSeconds;
                return authored >= 0.05f ? authored : DefaultGrindPulseIntervalSeconds;
            }
        }

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

        /// <summary>Suggested rammingPowerPerExtraLevel for Scan / ProfileSet — float only (no RoundToInt).</summary>
        public static float GetSuggestedRammingPowerPerLevel(int version) =>
            Mathf.Max(0f, GetSuggestedRammingPower(version) * RammingPerLevelFractionOfBase);

        /// <summary>
        /// Converts summed family-component <c>rammingPower</c> (level-scaled via
        /// <c>ShipStatApplyLogic</c> → <c>ShipMotorConfig.RammingPower</c>) into a damage rating.
        /// At Global 1 this equals the RAM chip (the grind DPS at <see cref="MassReference"/>).
        /// </summary>
        public static float ComputeDamageRatingFromFamilyPower(float summedFamilyRammingPower) =>
            Mathf.Max(0.05f, summedFamilyRammingPower) * GlobalDamageMultiplier;

        /// <summary>
        /// Mass scale wired into both grind and ram: <c>totalMass / MassReference</c>.
        /// 1 at the reference hull; 2 when the ship is twice as heavy (cargo or a bigger chassis).
        /// </summary>
        /// <param name="totalMass">Mobility totalMass (gems + people + ComponentSize).</param>
        public static float ComputeMassFactor(float totalMass) =>
            Mathf.Max(0f, totalMass) / MassReference;

        /// <summary>
        /// Sustained grind HP per second while thrusting into a rock:
        /// <c>rating × (totalMass / MassReference)</c>.
        /// Accel is not in this product — it only gates whether grind is allowed.
        /// </summary>
        /// <param name="ramDamageRating">Family rammingPower × GlobalDamageMultiplier.</param>
        /// <param name="totalMass">Mobility totalMass (gems + people + ComponentSize).</param>
        public static float ComputeGrindDps(float ramDamageRating, float totalMass) =>
            Mathf.Max(0f, ramDamageRating * ComputeMassFactor(totalMass));

        /// <summary>
        /// Ram burst multiplier from live closing speed.
        /// <c>1 + closing / RamClosingSpeedForDouble</c> — 1× at a dead stop (impact still
        /// gated at 0.35 u/s), 2× at the reference speed, 3× at twice that.
        /// </summary>
        /// <param name="closingSpeed">Live approach speed (world u/s).</param>
        public static float ComputeRamSpeedMultiplier(float closingSpeed) =>
            1f + Mathf.Max(0f, closingSpeed) / RamClosingSpeedForDouble;

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
                // --- Impulse hint ---
                // [PHYSICS] Solver impulse is not world u/s. Divide by mass and scale so a
                // glancing bump cannot outrank a real measured approach.
                float mass = Mathf.Max(0.5f, massForImpulse);
                float fromImpulse = (estimatedImpulse / mass) * ImpulseToClosingSpeedScale;
                fromImpulse = Mathf.Min(fromImpulse, MaxClosingSpeedFromImpulse);
                closing = Mathf.Max(closing, fromImpulse);
            }

            return Mathf.Max(0f, closing);
        }

        /// <summary>
        /// Impact damage to an asteroid or enemy hull — one burst on contact enter:
        /// <c>grindDps × (1 + closingSpeed / RamClosingSpeedForDouble)</c>.
        /// Mass is already inside grind DPS. Closing speed already reflects after-tax flight.
        /// </summary>
        /// <param name="ramDamageRating">Family rammingPower × GlobalDamageMultiplier.</param>
        /// <param name="totalMass">Mobility totalMass (gems + people + ComponentSize).</param>
        /// <param name="closingSpeed">Live approach speed (world u/s).</param>
        public static float ComputeImpactDamage(
            float ramDamageRating,
            float totalMass,
            float closingSpeed)
        {
            // --- Ram = grind baseline × speed multiplier ---
            // [TITAN-ORBIT] Same mass term as grind, then closing speed makes the first hit harder.
            return Mathf.Max(
                0f,
                ComputeGrindDps(ramDamageRating, totalMass) * ComputeRamSpeedMultiplier(closingSpeed));
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
        /// Grind pulse to asteroid: <c>grindDps × pulseInterval</c>.
        /// Four pulses per second at the default 0.25 s interval equals full grind DPS.
        /// </summary>
        /// <param name="ramDamageRating">Family rammingPower × GlobalDamageMultiplier.</param>
        /// <param name="totalMass">Mobility totalMass (gems + people + ComponentSize).</param>
        /// <param name="pulseInterval">Seconds this pulse represents (usually 0.25).</param>
        public static float ComputeGrindDamagePerPulse(
            float ramDamageRating,
            float totalMass,
            float pulseInterval)
        {
            return Mathf.Max(0f, ComputeGrindDps(ramDamageRating, totalMass) * Mathf.Max(0f, pulseInterval));
        }

        /// <summary>
        /// Self grind chip for one pulse — grind pulse × <see cref="SelfToAsteroidDamageRatio"/>.
        /// </summary>
        public static float ComputeGrindSelfDamagePerPulse(
            float ramDamageRating,
            float totalMass,
            float pulseInterval) =>
            Mathf.Max(
                0f,
                ComputeGrindDamagePerPulse(ramDamageRating, totalMass, pulseInterval)
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
