using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Code defaults used to seed <see cref="ShipFamilyPartCalcProfileSet"/> part profiles
    /// (Reset Part Calc Profiles To Defaults). Scan/Populate prefers the ProfileSet asset numbers;
    /// these constants are the fallback when a profile row is missing.
    /// </summary>
    public static class ShipComponentHealthSuggestions
    {
        /// <summary>
        /// Health on one contributing part at version 1.
        /// [TITAN-ORBIT] Derived from <see cref="GameBalanceTargets.SuggestedHealthCapPerPartV1"/>
        /// so median L1 hulls sit near 3 seconds of reference DPS.
        /// </summary>
        public static float HealthCapV1 => GameBalanceTargets.SuggestedHealthCapPerPartV1;

        /// <summary>Per-version health step (same shape as people/gems — vN ≈ N × V1).</summary>
        public static float HealthCapPerVersion => HealthCapV1;

        /// <summary>Passive regen as a fraction of Cap (slow out-of-combat top-up).</summary>
        public const float HealthRegenFractionOfCap = 0.75f / 21f;

        public static float GetSuggestedHealthCap(int version)
        {
            int v = Mathf.Max(1, version);
            return HealthCapV1 + (v - 1) * HealthCapPerVersion;
        }

        public static float GetSuggestedHealthRegen(int version) =>
            GetSuggestedHealthCap(version) * HealthRegenFractionOfCap;

        public static float GetSuggestedHealthCapPerLevel(int version) =>
            GetSuggestedHealthCap(version) * ShipPropulsionAggregation.PerLevelFractionOfBase;

        public static float GetSuggestedHealthRegenPerLevel(int version) =>
            GetSuggestedHealthRegen(version) * ShipPropulsionAggregation.PerLevelFractionOfBase;
    }

    /// <summary>Scan/auto-populate weapon offense defaults for ProfileSet seeding.</summary>
    public static class ShipComponentWeaponSuggestions
    {
        /// <summary>Weapon Bullet fire power (gun / machinegun).</summary>
        public const float FirePowerV1 = 3f;
        /// <summary>Weapon Bullet shots per second.</summary>
        public const float FireRate = 3f;
        /// <summary>[TITAN-ORBIT] Always 0 for every weapon type — rate does not grow per level.</summary>
        public const float FireRatePerLevel = 0f;
        public const float BulletSpeedV1 = 12f;
        /// <summary>
        /// Weapon Bullet travel distance at version 1 (matches <c>ShipWeaponConfig.DefaultBulletMaxDistance</c>).
        /// [TITAN-ORBIT] Grows with ship level — not a bottom-bar attribute upgrade.
        /// </summary>
        public const float BulletRangeV1 = 30f;
        /// <summary>Extra bullet range added per weapon part version step in ProfileSet seeds.</summary>
        public const float BulletRangePerVersion = 4f;

        /// <summary>Weapon Cannon fire power ≈ 4× Weapon Bullet.</summary>
        public const float CannonFirePowerV1 = FirePowerV1 * 4f;
        /// <summary>Weapon Cannon shots per second.</summary>
        public const float CannonFireRate = 1f;
        /// <summary>Weapon Cannon projectile speed (slightly slower than bullets).</summary>
        public const float CannonBulletSpeedV1 = 10f;
        /// <summary>Weapon Cannon travel distance at version 1 (slightly longer than bullets).</summary>
        public const float CannonBulletRangeV1 = 36f;
        /// <summary>Extra cannon range per weapon part version step.</summary>
        public const float CannonBulletRangePerVersion = 5f;

        /// <summary>
        /// HUD clamps attribute ticks to 7; each Fire Power tick is +10%
        /// (matches ShipAttributeUpgradeLogic.MultiplierPerLevel without a Data→ECS reference).
        /// Used by engine fleet Cap mirroring and max-shot cost helpers.
        /// </summary>
        public const int MaxFirePowerAttributeTicks = 7;
        public const float FirePowerAttributeMultiplierPerLevel = 0.1f;

        /// <summary>
        /// Sustained fire must drain energy (regen &lt; firePower × fireRate).
        /// Matches <see cref="GameBalanceTargets.EnergyRegenFractionOfSustainedDrain"/> (~0.30).
        /// Holding fire empties the pool; waiting recovers between bursts.
        /// </summary>
        public const float EnergyRegenFractionOfSustainedDrain =
            GameBalanceTargets.EnergyRegenFractionOfSustainedDrain;

        /// <summary>
        /// Hull / engine battery size in seconds of continuous fire.
        /// Matches <see cref="GameBalanceTargets.EnergyBatterySecondsOfSustainedFire"/>.
        /// </summary>
        public const float EnergyBatterySecondsOfSustainedFire =
            GameBalanceTargets.EnergyBatterySecondsOfSustainedFire;

        /// <summary>
        /// [LEGACY] Old weapon Cap = N max-attribute shots. Weapon Cap now defaults to
        /// <c>firePower × fireRate</c> (1 second of sustained fire) via <see cref="ApplyWeaponBatteryCap"/>.
        /// </summary>
        public const float BulletEnergyCapMaxAttributeShots = 3f;

        /// <summary>[LEGACY] See <see cref="BulletEnergyCapMaxAttributeShots"/>.</summary>
        public const float CannonEnergyCapMaxAttributeShots = 1f;

        /// <summary>Legacy name kept for callers — maps to bullet fire rate.</summary>
        public const float BurstEnergyBalanceFireRate = FireRate;
        public const float BurstSecondsAtFullDrain = 2f;
        public const float SustainedFireRateShotsPerSecond = 1f;

        public static float GetSuggestedFirePower(int version)
        {
            int v = Mathf.Max(1, version);
            return FirePowerV1 * v;
        }

        public static float GetSuggestedBulletSpeed(int version)
        {
            int v = Mathf.Max(1, version);
            return BulletSpeedV1 * v;
        }

        /// <summary>Per-level fire power — float only (no RoundToInt; small increments must survive).</summary>
        public static float GetSuggestedFirePowerPerLevel(int version) =>
            Mathf.Max(0f, GetSuggestedFirePower(version) * ShipPropulsionAggregation.PerLevelFractionOfBase);

        /// <summary>Per-level bullet speed — float only (no RoundToInt).</summary>
        public static float GetSuggestedBulletSpeedPerLevel(int version) =>
            Mathf.Max(0f, GetSuggestedBulletSpeed(version) * ShipPropulsionAggregation.PerLevelFractionOfBase);

        public static float GetSuggestedBulletRange(int version)
        {
            int v = Mathf.Max(1, version);
            return BulletRangeV1 + (v - 1) * BulletRangePerVersion;
        }

        /// <summary>Per-level bullet range — float only (no RoundToInt). Scales with ship level at runtime.</summary>
        public static float GetSuggestedBulletRangePerLevel(int version) =>
            Mathf.Max(0f, GetSuggestedBulletRange(version) * ShipPropulsionAggregation.PerLevelFractionOfBase);

        /// <summary>Weapon Cannon fire-power per ship level (same fraction rule as bullets).</summary>
        public static float GetSuggestedCannonFirePowerPerLevel(int version)
        {
            int v = Mathf.Max(1, version);
            float power = CannonFirePowerV1 * v;
            return Mathf.Max(0f, power * ShipPropulsionAggregation.PerLevelFractionOfBase);
        }

        /// <summary>Weapon Cannon bullet-speed per ship level (same fraction rule as bullets).</summary>
        public static float GetSuggestedCannonBulletSpeedPerLevel(int version)
        {
            int v = Mathf.Max(1, version);
            float speed = CannonBulletSpeedV1 * v;
            return Mathf.Max(0f, speed * ShipPropulsionAggregation.PerLevelFractionOfBase);
        }

        public static float GetSuggestedCannonBulletRange(int version)
        {
            int v = Mathf.Max(1, version);
            return CannonBulletRangeV1 + (v - 1) * CannonBulletRangePerVersion;
        }

        /// <summary>Weapon Cannon bullet-range per ship level (same fraction rule as bullets).</summary>
        public static float GetSuggestedCannonBulletRangePerLevel(int version) =>
            Mathf.Max(0f, GetSuggestedCannonBulletRange(version) * ShipPropulsionAggregation.PerLevelFractionOfBase);

        public static float ComputeSustainedEnergyDrain(float firePower, float fireRate) =>
            Mathf.Max(0f, firePower) * Mathf.Max(0.01f, fireRate);

        /// <summary>
        /// Max fire-power cost of one shot after full HUD Fire Power ticks
        /// (energy cost per shot = firePower in combat).
        /// </summary>
        public static float GetMaxAttributeFirePowerCost(float baseFirePower)
        {
            float mul = 1f + MaxFirePowerAttributeTicks * FirePowerAttributeMultiplierPerLevel;
            return Mathf.Max(0.01f, baseFirePower) * mul;
        }

        /// <summary>
        /// Legacy helper: clears weapon Cap/Regen then (optionally) writes engine-style regen.
        /// [TITAN-ORBIT] Prefer <see cref="ApplyWeaponBatteryCap"/> on guns +
        /// <see cref="ShipPropulsionAggregation.BalanceEngineEnergyForComponents"/> on engines.
        /// Weapons do not own the power plant — Cap and Regen live on engines.
        /// </summary>
        /// <param name="stats">Weapon component stats (writes energy fields).</param>
        public static void ApplyBalancedEnergy(ref ShipComponentAbilityStats stats)
        {
            // Guns never author Cap or Regen — engines own the 3s / 30% plant.
            ApplyWeaponBatteryCap(ref stats);
        }

        /// <summary>
        /// [TITAN-ORBIT] Weapons spend energy; they do <b>not</b> store or regenerate it.
        /// Clears Cap/Regen on the gun so hull <c>MaxEnergy</c> comes from the engine plant
        /// (~<see cref="EnergyBatterySecondsOfSustainedFire"/> seconds of fire at 30% regen).
        /// </summary>
        /// <param name="stats">Weapon stats (clears energy Cap / Regen / PerLevel).</param>
        public static void ApplyWeaponBatteryCap(ref ShipComponentAbilityStats stats)
        {
            stats.energyCap = 0f;
            stats.energyCapPerAbilityLevel = 0f;
            stats.energyRegen = 0f;
            stats.energyRegenPerAbilityLevel = 0f;
        }

        /// <summary>Cannon energy clear — engines own Cap/Regen.</summary>
        public static void ApplyCannonBalancedEnergy(ref ShipComponentAbilityStats stats) =>
            ApplyBalancedEnergy(ref stats);

        /// <summary>Bullet energy clear — engines own Cap/Regen.</summary>
        public static void ApplyBulletBalancedEnergy(ref ShipComponentAbilityStats stats) =>
            ApplyBalancedEnergy(ref stats);
    }

    /// <summary>Scan/auto-populate wing tractor beam defaults.</summary>
    public static class ShipComponentTractorBeamSuggestions
    {
        public const float TractorDistanceV1 = 3f;
        public const float TractorDistancePerVersion = 3f;
        public const float TractorPowerV1 = 4f;
        public const float TractorPowerPerVersion = 4f;

        public static float GetSuggestedTractorDistance(int version)
        {
            int v = Mathf.Max(1, version);
            return TractorDistanceV1 + (v - 1) * TractorDistancePerVersion;
        }

        public static float GetSuggestedTractorPower(int version)
        {
            int v = Mathf.Max(1, version);
            return TractorPowerV1 + (v - 1) * TractorPowerPerVersion;
        }

        public static float GetSuggestedTractorDistancePerLevel(int version) =>
            GetSuggestedTractorDistance(version) * ShipPropulsionAggregation.PerLevelFractionOfBase;

        public static float GetSuggestedTractorPowerPerLevel(int version) =>
            GetSuggestedTractorPower(version) * ShipPropulsionAggregation.PerLevelFractionOfBase;
    }

    /// <summary>
    /// Scan/auto-populate people capacity defaults.
    /// Sized from <see cref="GameBalanceTargets"/> so a median Cockpit+Wings hull can help
    /// about three ships capture a same-level home planet in ~5 cargo unload cycles.
    /// </summary>
    public static class ShipComponentPeopleCapacitySuggestions
    {
        /// <summary>
        /// People on one cargo part (Cockpit or Wing) at version 1.
        /// [TITAN-ORBIT] Derived from capture targets ÷ expected cargo part count — not a free-hand guess.
        /// </summary>
        public static float PeopleCapacityV1 => GameBalanceTargets.SuggestedPeoplePerCargoPartV1;

        /// <summary>Per-version step equals V1 so vN ≈ N × V1 (same shape as legacy linear versions).</summary>
        public static float PeopleCapacityPerVersion => PeopleCapacityV1;

        public static float GetSuggestedPeopleCapacity(int version)
        {
            int v = Mathf.Max(1, version);
            return PeopleCapacityV1 * v;
        }

        /// <summary>Per ship-level people growth — float only (no RoundToInt).</summary>
        public static float GetSuggestedPeopleCapacityPerLevel(int version) =>
            Mathf.Max(0f, GetSuggestedPeopleCapacity(version) * ShipPropulsionAggregation.PerLevelFractionOfBase);
    }

    /// <summary>
    /// Scan/auto-populate gem capacity defaults for Cockpit / Wing cargo parts.
    /// Sized so a median hull’s gemCap ≈ <see cref="GameBalanceTargets.TargetMedianGemCapAtShipLevel1"/>
    /// and chassis purchase (<c>2 × gemCap</c>) stays ~two full cargo trips.
    /// </summary>
    public static class ShipComponentGemCapacitySuggestions
    {
        /// <summary>Gems on one cargo part at version 1 (from GameBalanceTargets).</summary>
        public static float GemCapacityV1 => GameBalanceTargets.SuggestedGemsPerCargoPartV1;

        /// <summary>Per-version gem step (linear versions, same as people).</summary>
        public static float GemCapacityPerVersion => GemCapacityV1;

        public static float GetSuggestedGemCapacity(int version)
        {
            int v = Mathf.Max(1, version);
            return GemCapacityV1 * v;
        }

        /// <summary>Per ship-level gem growth — float only.</summary>
        public static float GetSuggestedGemCapacityPerLevel(int version) =>
            Mathf.Max(0f, GetSuggestedGemCapacity(version) * ShipPropulsionAggregation.PerLevelFractionOfBase);
    }

    /// <summary>
    /// Scan / ProfileSet turn-speed seeds for the Tail Part Profile group and thruster mounts.
    /// <para>
    /// [TITAN-ORBIT] Fin and Tail used to be separate part types with their own seeds
    /// (<see cref="FinTurnSpeedPerVersion"/> + <see cref="TailTurnSpeedPerVersion"/>).
    /// They now share the Tail profile; <see cref="GetSuggestedTurnSpeed"/> returns the
    /// combined package so Reset / CreateDefaultProfile keep the old total turn budget.
    /// Thruster-like mounts also author Fin-scale turn (<see cref="GetSuggestedFinTurnSpeed"/>)
    /// so Tail/Fin + thrusters both contribute without exploding the turn budget.
    /// </para>
    /// </summary>
    public static class ShipComponentTurnSpeedSuggestions
    {
        /// <summary>
        /// Scales raw per-version turn constants into gameplay units.
        /// Same scale used historically for Fin/Tail component suggestions.
        /// </summary>
        public const float ComponentTurnSpeedScale = 22f / 37f;

        /// <summary>Legacy Fin per-version turn (pre-merge). Kept so merged totals stay auditable.</summary>
        public const float FinTurnSpeedPerVersion = 7f;

        /// <summary>Legacy Tail per-version turn (pre-merge). Kept so merged totals stay auditable.</summary>
        public const float TailTurnSpeedPerVersion = 11f;

        /// <summary>
        /// Fin + Tail per-version raw constant after the Part Profile merge.
        /// [TITAN-ORBIT] One Tail profile replaces two rows — use the sum, not Tail alone.
        /// </summary>
        public const float MergedTurnSpeedPerVersion = FinTurnSpeedPerVersion + TailTurnSpeedPerVersion;

        /// <summary>Legacy Fin-only suggestion (version tier × scale). Prefer <see cref="GetSuggestedTurnSpeed"/> for new profiles.</summary>
        public static float GetSuggestedFinTurnSpeed(int version)
        {
            int v = Mathf.Max(1, version);
            return FinTurnSpeedPerVersion * v * ComponentTurnSpeedScale;
        }

        /// <summary>Legacy Tail-only suggestion (version tier × scale). Prefer <see cref="GetSuggestedTurnSpeed"/> for new profiles.</summary>
        public static float GetSuggestedTailTurnSpeed(int version)
        {
            int v = Mathf.Max(1, version);
            return TailTurnSpeedPerVersion * v * ComponentTurnSpeedScale;
        }

        /// <summary>
        /// Canonical turn speed for the Tail Part Profile (Fin + Tail seeds).
        /// Version 1 ≈ 10.70; each higher version adds another MergedTurnSpeedPerVersion × scale.
        /// </summary>
        /// <param name="version">1-based part version digit from the component id.</param>
        public static float GetSuggestedTurnSpeed(int version)
        {
            int v = Mathf.Max(1, version);
            return MergedTurnSpeedPerVersion * v * ComponentTurnSpeedScale;
        }

        /// <summary>
        /// Per ship-level turn growth from a base turn value (25% of base by default).
        /// </summary>
        /// <param name="baseTurnSpeed">Turn at the component's version tier (before ship upgrades).</param>
        public static float GetSuggestedTurnSpeedPerLevel(float baseTurnSpeed) =>
            baseTurnSpeed * ShipPropulsionAggregation.PerLevelFractionOfBase;
    }
}
