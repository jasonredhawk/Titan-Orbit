using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Shared ramming / grinding damage helpers. Used by the HUD and server
    /// <c>ShipRammingCollisionDamageSystem</c> so display and authority cannot drift.
    /// <para>
    /// [TITAN-ORBIT] The RAM chip (family <c>rammingPower</c>, e.g. 5.8) is grind HP per second.
    /// Hull mass and cargo do not scale either product — they still change how the ship flies
    /// and how hard it bounces, but not HP dealt:
    /// <c>grindDps = rating</c>
    /// <c>impact = rating × (1 + currentSpeed / stockCruise)</c>
    /// where <c>rating = rammingPower × GlobalDamageMultiplier</c> and <c>stockCruise</c> is
    /// this ship's max speed before Move Speed ability purchases (same mass tax and territory
    /// as a stock cruise). <c>currentSpeed</c> is how fast the ship is flying at the hit,
    /// including OVERDRIVE — it is not capped to cruise. At stock cruise the hit is 2× the
    /// chip. Going faster raises it.
    /// After-tax Accel is a grind <b>gate</b> only (must thrust into the rock) — it does not
    /// scale damage. Closing speed is live approach, capped to after-tax cruise.
    /// </para>
    /// <para>
    /// Balance via <see cref="ShipRammingSettings"/> —
    /// <see cref="GlobalDamageMultiplier"/>, <see cref="SelfToAsteroidDamageRatio"/> —
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
        /// 1 = the RAM chip is grind HP per second.
        /// </summary>
        public const float DefaultGlobalDamageMultiplier = 1f;

        /// <summary>
        /// Fallback self-to-target ratio when no settings asset is loaded.
        /// Prefer editing Assets/Resources/ShipRammingSettings.asset in the Inspector.
        /// </summary>
        public const float DefaultSelfToAsteroidDamageRatio = 2f;

        /// <summary>
        /// Scales summed family <c>rammingPower</c> into a damage rating.
        /// 1 = the RAM chip is grind HP per second.
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
        /// Soft floor for gem-expulsion intensity InverseLerp. Not a grind-damage gate —
        /// contact + thrust or planar motion is enough to pulse.
        /// </summary>
        public const float GrindMinPushNewtons = 8f;

        /// <summary>
        /// Planar speed (world u/s) that counts as “moving” for grind when Thrust is not held.
        /// Resting against a rock with Thrust still grinds (solver often zeros velocity).
        /// </summary>
        public const float GrindMinPlanarSpeed = 0.12f;

        /// <summary>
        /// Extra world slack on the hull+rock radii so a resting PhysX contact still
        /// counts after collision events go quiet.
        /// </summary>
        public const float GrindOverlapSkin = 0.45f;

        /// <summary>
        /// True when this hull should emit grind pulses: thrusting (including jammed
        /// velocity-zero push) or sliding along the rock. Nose alignment is not required.
        /// </summary>
        public static bool ShouldGrindFromMotion(bool thrustHeld, float planarSpeed) =>
            thrustHeld || planarSpeed >= GrindMinPlanarSpeed;

        /// <summary>
        /// Compresses ram/grind HP onto the fire-power piano so L6 slams stay audible.
        /// Raw pulse/impact HP of 40–88 mapped 1:1 sat on A0 (sub-bass rumble).
        /// 1 HP → key 1; each damage doubling is a half-octave; hard cap 3 octaves (key 36).
        /// </summary>
        public static float CollisionSfxPianoAmount(float damage)
        {
            float d = Mathf.Max(0.25f, damage);
            float key = 1f + 6f * Mathf.Log(d, 2f);
            return Mathf.Clamp(key, 1f, 36f);
        }

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
        /// At Global 1 this equals the RAM chip (grind HP per second). A stock-cruise ram is 2× this.
        /// </summary>
        public static float ComputeDamageRatingFromFamilyPower(float summedFamilyRammingPower) =>
            Mathf.Max(0.05f, summedFamilyRammingPower) * GlobalDamageMultiplier;

        /// <summary>
        /// Sustained grind HP per second while thrusting into a rock.
        /// This is the RAM chip. Hull mass and cargo do not scale it.
        /// Accel is not in this product — it only gates whether grind is allowed.
        /// </summary>
        /// <param name="ramDamageRating">Family rammingPower × GlobalDamageMultiplier.</param>
        public static float ComputeGrindDps(float ramDamageRating) =>
            Mathf.Max(0f, ramDamageRating);

        /// <summary>
        /// Ram burst multiplier from the ship's current flight speed and its stock cruise.
        /// <c>1 + currentSpeed / stockCruise</c>. Stock cruise is max speed before Move Speed
        /// ability purchases. At that speed the multiplier is 2. OVERDRIVE and a higher cruise
        /// both go above 2. A stop is 1 (impact is still gated at 0.35 u/s of approach).
        /// </summary>
        /// <param name="currentSpeed">Planar speed at the hit (world u/s), including OVERDRIVE.</param>
        /// <param name="baseShipSpeed">
        /// This ship's cruise before Move Speed purchases, in the same units as
        /// <paramref name="currentSpeed"/> (after mass tax and territory).
        /// </param>
        public static float ComputeRamSpeedMultiplier(float currentSpeed, float baseShipSpeed)
        {
            // --- Stock cruise is the 2× point ---
            // [TITAN-ORBIT] The numerator is how fast the hull is moving, not a cruise cap
            // and not the contact-normal approach. Faster flight, including OVERDRIVE, hits harder.
            float basis = Mathf.Max(0.01f, baseShipSpeed);
            return 1f + Mathf.Max(0f, currentSpeed) / basis;
        }

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
        /// <c>rating × (1 + currentSpeed / stockCruise)</c>.
        /// Mass is not in this product. <paramref name="baseShipSpeed"/> is this ship's
        /// cruise before Move Speed ability purchases. <paramref name="currentSpeed"/> is
        /// flight speed at the hit, including OVERDRIVE.
        /// </summary>
        /// <param name="ramDamageRating">Family rammingPower × GlobalDamageMultiplier.</param>
        /// <param name="currentSpeed">Planar speed at the hit (world u/s).</param>
        /// <param name="baseShipSpeed">Stock cruise in the same units as current speed.</param>
        public static float ComputeImpactDamage(
            float ramDamageRating,
            float currentSpeed,
            float baseShipSpeed)
        {
            // --- Ram = chip × (1 + current speed / stock cruise) ---
            // [TITAN-ORBIT] At stock cruise this is 2× the chip. OVERDRIVE and Move Speed
            // purchases raise current speed above stock and the hit grows with it.
            return Mathf.Max(
                0f,
                ComputeGrindDps(ramDamageRating)
                * ComputeRamSpeedMultiplier(currentSpeed, baseShipSpeed));
        }

        /// <summary>
        /// Self impact chip: same product as <see cref="ComputeImpactDamage"/> ×
        /// <see cref="SelfToAsteroidDamageRatio"/>.
        /// </summary>
        public static float ComputeImpactSelfDamage(
            float ramDamageRating,
            float currentSpeed,
            float baseShipSpeed) =>
            Mathf.Max(
                0f,
                ComputeImpactDamage(ramDamageRating, currentSpeed, baseShipSpeed)
                * SelfToAsteroidDamageRatio);

        /// <summary>
        /// Factors behind one full-cruise ram. The Fire Power card and the speedometer
        /// RAM tip both print this so the chip (for example 12) is not read as the hit
        /// unless cruise equals the reference.
        /// Asteroid damage is the first-contact floater at the current cruise.
        /// At stock cruise that floater is 2× the chip. Move Speed purchases raise it.
        /// Holding the rock after that adds grind pulses on top. Mass is not a factor.
        /// </summary>
        public struct RamCruiseImpactPreview
        {
            /// <summary>Family ramming power × global × bullet-bank multiplier. Also grind HP/s.</summary>
            public float Rating;
            /// <summary>The chip. Sustained HP/s while grinding. Mass does not change this.</summary>
            public float GrindDps;
            /// <summary>After-tax cruise used as the closing-speed preview (world u/s), including Move Speed purchases.</summary>
            public float CruiseSpeed;
            /// <summary>Cruise before Move Speed purchases, same tax and territory as <see cref="CruiseSpeed"/>.</summary>
            public float BaseShipSpeed;
            /// <summary><c>1 + cruise / BaseShipSpeed</c>. Stock cruise is 2×.</summary>
            public float SpeedMultiplier;
            /// <summary>First-contact damage to the asteroid at full cruise.</summary>
            public float AsteroidDamage;
            /// <summary>Hull chip on that same hit, before the rock-HP cap.</summary>
            public float SelfDamage;
        }

        /// <summary>
        /// Splits a full-cruise ram into the factors the HUD prints.
        /// Damage numbers come from <see cref="ComputeImpactDamage"/> so the card cannot
        /// drift from <c>ShipRammingCollisionDamageSystem</c>.
        /// </summary>
        /// <param name="ramDamageRating">Chip after global and bank multipliers.</param>
        /// <param name="cruiseSpeed">Closing speed to preview, usually after-tax cruise with upgrades.</param>
        /// <param name="baseShipSpeed">Stock cruise before Move Speed purchases. 0 uses <paramref name="cruiseSpeed"/>.</param>
        public static RamCruiseImpactPreview BuildCruiseImpactPreview(
            float ramDamageRating,
            float cruiseSpeed,
            float baseShipSpeed)
        {
            // --- Same product the server applies on contact enter ---
            // [TITAN-ORBIT] Preview at the live cruise so the card matches a full-speed ram.
            // Stock cruise in the divisor keeps an un-upgraded hull at 2× the chip.
            float rating = Mathf.Max(0f, ramDamageRating);
            float cruise = Mathf.Max(0f, cruiseSpeed);
            float basis = baseShipSpeed > 0.01f ? baseShipSpeed : Mathf.Max(0.01f, cruise);
            float grind = ComputeGrindDps(rating);
            float speedMul = ComputeRamSpeedMultiplier(cruise, basis);

            return new RamCruiseImpactPreview
            {
                Rating = rating,
                GrindDps = grind,
                CruiseSpeed = cruise,
                BaseShipSpeed = basis,
                SpeedMultiplier = speedMul,
                AsteroidDamage = ComputeImpactDamage(rating, cruise, basis),
                SelfDamage = ComputeImpactSelfDamage(rating, cruise, basis),
            };
        }

        /// <summary>
        /// Hull chip a regular ship takes from a rock: the ram or grind self formula,
        /// capped by the asteroid's current Health. A small rock cannot deal a full
        /// cruise ram back into the hull. MEGA plow uses its own remaining-HP slider.
        /// </summary>
        /// <param name="rammingSelfDamage">
        /// Uncapped self-chip from <see cref="ComputeImpactSelfDamage"/> or
        /// <see cref="ComputeGrindSelfDamagePerPulse"/>.
        /// </param>
        /// <param name="asteroidHealth">Asteroid Health before this hit is applied.</param>
        public static float CapCollisionSelfDamageToAsteroidHealth(
            float rammingSelfDamage,
            float asteroidHealth) =>
            Mathf.Min(Mathf.Max(0f, rammingSelfDamage), Mathf.Max(0f, asteroidHealth));

        /// <summary>
        /// Grind pulse to asteroid: <c>grindDps × pulseInterval</c>.
        /// Four pulses per second at the default 0.25 s interval equals full grind DPS.
        /// Mass is not in this product.
        /// </summary>
        /// <param name="ramDamageRating">Family rammingPower × GlobalDamageMultiplier.</param>
        /// <param name="pulseInterval">Seconds this pulse represents (usually 0.25).</param>
        public static float ComputeGrindDamagePerPulse(float ramDamageRating, float pulseInterval)
        {
            return Mathf.Max(
                0f,
                ComputeGrindDps(ramDamageRating) * Mathf.Max(0f, pulseInterval));
        }

        /// <summary>
        /// Self grind chip for one pulse — grind pulse × <see cref="SelfToAsteroidDamageRatio"/>.
        /// </summary>
        public static float ComputeGrindSelfDamagePerPulse(float ramDamageRating, float pulseInterval) =>
            Mathf.Max(
                0f,
                ComputeGrindDamagePerPulse(ramDamageRating, pulseInterval) * SelfToAsteroidDamageRatio);

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
