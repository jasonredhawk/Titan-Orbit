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
        public const float HealthCapV1 = 6.3f;
        public const float HealthCapPerVersion = 1.8f;
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

        /// <summary>Weapon Cannon fire power ≈ 4× Weapon Bullet.</summary>
        public const float CannonFirePowerV1 = FirePowerV1 * 4f;
        /// <summary>Weapon Cannon shots per second.</summary>
        public const float CannonFireRate = 1f;
        /// <summary>Weapon Cannon projectile speed (slightly slower than bullets).</summary>
        public const float CannonBulletSpeedV1 = 10f;

        /// <summary>
        /// HUD clamps attribute ticks to 7; each Fire Power tick is +10%
        /// (matches ShipAttributeUpgradeLogic.MultiplierPerLevel without a Data→ECS reference).
        /// Used so energy cap can afford one maxed fire-power shot at base weapon stats.
        /// </summary>
        public const int MaxFirePowerAttributeTicks = 7;
        public const float FirePowerAttributeMultiplierPerLevel = 0.1f;

        /// <summary>
        /// Sustained fire must drain energy (regen &lt; firePower × fireRate).
        /// 0.35 ⇒ holding fire empties the pool; waiting recovers between bursts.
        /// </summary>
        public const float EnergyRegenFractionOfSustainedDrain = 0.35f;

        /// <summary>
        /// Weapon Bullet energy cap = this many max-attribute shots (short burst).
        /// Cannons use 1 so one maxed shot nearly empties the bar.
        /// </summary>
        public const float BulletEnergyCapMaxAttributeShots = 3f;

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
        /// Sizes weapon energy pool/regen from fire power + fire rate.
        /// <para>
        /// Combat spends <c>firePower</c> energy per shot. Cap is sized so a max Fire Power
        /// attribute shot fits (cannons: ~1 shot empties the bar; bullets: a short burst).
        /// Regen is a fraction of sustained drain so holding fire always nets energy loss.
        /// </para>
        /// </summary>
        /// <param name="stats">Weapon component stats (reads firePower / fireRate; writes energy).</param>
        /// <param name="maxAttributeShotsInCap">
        /// How many max-attribute shots fit in the energy bar (1 for cannon, ~3 for bullets).
        /// </param>
        public static void ApplyBalancedEnergy(
            ref ShipComponentAbilityStats stats,
            float maxAttributeShotsInCap = BulletEnergyCapMaxAttributeShots)
        {
            float firePower = Mathf.Max(0f, stats.firePower);
            float fireRate = Mathf.Max(0.01f, stats.fireRate);
            if (firePower <= 0f)
                return;

            // --- Cap: afford max Fire Power attribute shots at this component's base fire power ---
            // [TITAN-ORBIT] Energy cost per shot = firePower. Cap uses max-attribute cost so
            // upgrading Fire Power to full never makes a shot more expensive than the pool.
            float maxShotCost = GetMaxAttributeFirePowerCost(firePower);
            float shots = Mathf.Max(1f, maxAttributeShotsInCap);
            stats.energyCap = maxShotCost * shots;
            stats.energyCapPerLevel = stats.energyCap * ShipPropulsionAggregation.PerLevelFractionOfBase;

            // --- Regen: always slower than sustained fire drain ---
            float sustainedDrain = ComputeSustainedEnergyDrain(firePower, fireRate);
            stats.energyRegen = sustainedDrain * EnergyRegenFractionOfSustainedDrain;
            stats.energyRegenPerLevel = stats.energyRegen * ShipPropulsionAggregation.PerLevelFractionOfBase;
        }

        /// <summary>
        /// Cannon energy: one maxed Fire Power shot ≈ full energy bar; regen &lt; 1 shot/sec drain.
        /// </summary>
        public static void ApplyCannonBalancedEnergy(ref ShipComponentAbilityStats stats) =>
            ApplyBalancedEnergy(ref stats, maxAttributeShotsInCap: 1f);

        /// <summary>
        /// Bullet energy: short burst of maxed shots in the bar; regen &lt; 3 shots/sec drain.
        /// </summary>
        public static void ApplyBulletBalancedEnergy(ref ShipComponentAbilityStats stats) =>
            ApplyBalancedEnergy(ref stats, maxAttributeShotsInCap: BulletEnergyCapMaxAttributeShots);
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

    /// <summary>Scan/auto-populate people capacity defaults.</summary>
    public static class ShipComponentPeopleCapacitySuggestions
    {
        public const float PeopleCapacityV1 = 2f;

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
    /// Scan / ProfileSet turn-speed seeds for the Tail Part Profile group.
    /// <para>
    /// [TITAN-ORBIT] Fin and Tail used to be separate part types with their own seeds
    /// (<see cref="FinTurnSpeedPerVersion"/> + <see cref="TailTurnSpeedPerVersion"/>).
    /// They now share the Tail profile; <see cref="GetSuggestedTurnSpeed"/> returns the
    /// combined package so Reset / CreateDefaultProfile keep the old total turn budget.
    /// Thrusters never author turn — only Tail (incl. Fin name mappings).
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
