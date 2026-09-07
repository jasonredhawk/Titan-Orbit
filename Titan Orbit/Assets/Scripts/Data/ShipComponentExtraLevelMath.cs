using System.Collections.Generic;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Plain ability-purchase counts for Extra Level evaluation (no ECS dependency).
    /// Mirrors <c>ShipAttributeUpgradeState</c> field order used by the bottom HUD.
    /// </summary>
    public struct ShipAbilityLevelCounts
    {
        /// <summary>Fire Power ability purchases.</summary>
        public int FirePower;
        /// <summary>Bullet Speed ability purchases.</summary>
        public int BulletSpeed;
        /// <summary>Health Cap ability purchases.</summary>
        public int MaxHealth;
        /// <summary>Health Regen ability purchases.</summary>
        public int HealthRegen;
        /// <summary>Energy Capacity ability purchases.</summary>
        public int EnergyCapacity;
        /// <summary>Energy Regen ability purchases.</summary>
        public int EnergyRegen;
        /// <summary>Move Speed ability purchases (also scales accel / OVERDRIVE drain).</summary>
        public int MovementSpeed;
        /// <summary>Turn / Rotation Speed ability purchases.</summary>
        public int RotationSpeed;
        /// <summary>Gem Cap ability purchases.</summary>
        public int GemCapacity;
        /// <summary>Troop Cap ability purchases.</summary>
        public int PeopleCapacity;

        /// <summary>All abilities maxed to <paramref name="shipLevel"/> (preview / power-score ceiling).</summary>
        public static ShipAbilityLevelCounts Maxed(int shipLevel)
        {
            int n = Mathf.Max(0, shipLevel);
            return new ShipAbilityLevelCounts
            {
                FirePower = n,
                BulletSpeed = n,
                MaxHealth = n,
                HealthRegen = n,
                EnergyCapacity = n,
                EnergyRegen = n,
                MovementSpeed = n,
                RotationSpeed = n,
                GemCapacity = n,
                PeopleCapacity = n,
            };
        }
    }

    /// <summary>
    /// Unified Extra Level ship-stat formula for Titan Orbit.
    /// <para>
    /// [TITAN-ORBIT] Replaces Extra Stack Weight, family <c>shipLevelStatGrowthFraction</c> % tier growth,
    /// and bottom-HUD ×1.1 ability multipliers.
    /// </para>
    /// <para>
    /// Non-weapon pools (engines, wings, cockpits, …) — primary Base, extras raise <c>(N−1)</c>:
    /// <c>Base + PerExtraLevel × ((shipLevel − 1) + abilityLevel + (numberOfComponents − 1))</c>
    /// <para>
    /// [TITAN-ORBIT] Callers pass Base / PerExtra already multiplied by prefab starting
    /// <c>localScale</c> (<see cref="ShipComponentAbilityStatsMath.ScaleStatsByTransform"/>).
    /// A Cockpit at scale 3 is <c>3 ×</c> catalog Health / Gems / Troops. Ability details
    /// cards show that multiply as its own formula step.
    /// </para>
    /// </para>
    /// <para>
    /// Weapon fire power / fire rate / range (no component-count term):
    /// <c>Base + PerExtraLevel × ((shipLevel − 1) + abilityLevel)</c>
    /// Live mounts each use their own Base / PerExtra; they are not pooled via <c>(N−1)</c>.
    /// </para>
    /// <para>
    /// Weapon bullet speed is ability-only (no ship level, no N):
    /// <c>Base + PerExtraLevel × abilityLevel</c>
    /// </para>
    /// </summary>
    public static class ShipComponentExtraLevelMath
    {
        /// <summary>
        /// Evaluates one scalar with the Extra Level formula.
        /// </summary>
        /// <param name="baseValue">Part base for this field (primary for stacked pools; per-mount for weapons). Already includes prefab starting scale when the caller scanned a chassis prefab.</param>
        /// <param name="perExtraLevel">Per Extra Level step for this field (same starting-scale multiply as <paramref name="baseValue"/>).</param>
        /// <param name="shipLevel">Chassis tier (1-based).</param>
        /// <param name="abilityLevel">Bottom-HUD purchases for the matching ability (0 if none).</param>
        /// <param name="componentCount">Parts in the owning stack pool (ignored when <paramref name="includeExtraComponentLevels"/> is false).</param>
        /// <param name="includeExtraComponentLevels">
        /// True for engines / wings / cockpits / … (<c>+(N−1)</c>).
        /// False for weapons (ship + ability only — each gun fires on its own Base).
        /// </param>
        public static float Evaluate(
            float baseValue,
            float perExtraLevel,
            int shipLevel,
            int abilityLevel,
            int componentCount,
            bool includeExtraComponentLevels)
        {
            // --- Ship tier above 1 + ability purchases ---
            int levels = Mathf.Max(0, shipLevel - 1) + Mathf.Max(0, abilityLevel);

            // --- Non-weapon pools: extras beyond primary also add Extra Levels ---
            if (includeExtraComponentLevels)
            {
                int count = Mathf.Max(0, componentCount);
                if (count <= 0)
                    return 0f;
                levels += count - 1;
            }

            return baseValue + perExtraLevel * levels;
        }

        /// <summary>
        /// How many Extra Level multiplier steps non-weapon pools use:
        /// <c>(shipLevel−1) + abilityLevel + (componentCount−1)</c>.
        /// </summary>
        public static int CountExtraLevels(int shipLevel, int abilityLevel, int componentCount)
        {
            int count = Mathf.Max(0, componentCount);
            int extras = Mathf.Max(0, count - 1);
            return Mathf.Max(0, shipLevel - 1) + Mathf.Max(0, abilityLevel) + extras;
        }

        /// <summary>
        /// Weapon Extra Level steps (no component stack): <c>(shipLevel−1) + abilityLevel</c>.
        /// </summary>
        public static int CountWeaponExtraLevels(int shipLevel, int abilityLevel) =>
            Mathf.Max(0, shipLevel - 1) + Mathf.Max(0, abilityLevel);

        /// <summary>
        /// Weapon bullet speed steps — ability purchases only (no ship level, no N).
        /// </summary>
        public static int CountWeaponBulletSpeedExtraLevels(int abilityLevel) =>
            Mathf.Max(0, abilityLevel);

        /// <summary>
        /// Weapon bullet speed: <c>Base + PerExtraLevel × abilityLevel</c>.
        /// </summary>
        public static float EvaluateWeaponBulletSpeed(
            float baseValue,
            float perExtraLevel,
            int abilityLevel) =>
            baseValue + perExtraLevel * Mathf.Max(0, abilityLevel);

        /// <summary>
        /// Evaluates every field on a primary pool contribution, then returns effective bases.
        /// <c>*PerExtraLevel</c> fields are copied through unchanged (still useful for tooltips / mesh grow rates).
        /// </summary>
        public static ShipComponentAbilityStats EvaluatePool(
            in ShipComponentAbilityStats primary,
            int componentCount,
            int shipLevel,
            in ShipAbilityLevelCounts attrs,
            bool isWeaponPool)
        {
            int n = Mathf.Max(0, componentCount);
            // [TITAN-ORBIT] Weapons ignore N — each barrel uses ship+ability only on its own Base.
            bool stackExtras = !isWeaponPool;

            // --- Bullet speed ---
            // [TITAN-ORBIT] Weapon bullet speed grows only with Bullet Speed ability purchases.
            float bulletSpeedValue = isWeaponPool
                ? EvaluateWeaponBulletSpeed(
                    primary.bulletSpeed, primary.bulletSpeedPerExtraLevel, attrs.BulletSpeed)
                : Evaluate(
                    primary.bulletSpeed, primary.bulletSpeedPerExtraLevel,
                    shipLevel, attrs.BulletSpeed, n, includeExtraComponentLevels: stackExtras);

            return new ShipComponentAbilityStats
            {
                firePower = Evaluate(
                    primary.firePower, primary.firePowerPerExtraLevel,
                    shipLevel, attrs.FirePower, n, includeExtraComponentLevels: stackExtras),
                firePowerPerExtraLevel = primary.firePowerPerExtraLevel,

                bulletSpeed = bulletSpeedValue,
                bulletSpeedPerExtraLevel = primary.bulletSpeedPerExtraLevel,

                // [TITAN-ORBIT] No bottom-HUD Bullet Range ability — abilityLevel stays 0.
                bulletRange = Evaluate(
                    primary.bulletRange, primary.bulletRangePerExtraLevel,
                    shipLevel, 0, n, includeExtraComponentLevels: stackExtras),
                bulletRangePerExtraLevel = primary.bulletRangePerExtraLevel,

                // [TITAN-ORBIT] No Fire Rate ability — ship/component levels only.
                fireRate = Evaluate(
                    primary.fireRate, primary.fireRatePerExtraLevel,
                    shipLevel, 0, n, includeExtraComponentLevels: stackExtras),
                fireRatePerExtraLevel = primary.fireRatePerExtraLevel,

                rammingPower = Evaluate(
                    primary.rammingPower, primary.rammingPowerPerExtraLevel,
                    shipLevel, 0, n, includeExtraComponentLevels: stackExtras),
                rammingPowerPerExtraLevel = primary.rammingPowerPerExtraLevel,

                healthCap = Evaluate(
                    primary.healthCap, primary.healthCapPerExtraLevel,
                    shipLevel, attrs.MaxHealth, n, includeExtraComponentLevels: stackExtras),
                healthCapPerExtraLevel = primary.healthCapPerExtraLevel,

                healthRegen = Evaluate(
                    primary.healthRegen, primary.healthRegenPerExtraLevel,
                    shipLevel, attrs.HealthRegen, n, includeExtraComponentLevels: stackExtras),
                healthRegenPerExtraLevel = primary.healthRegenPerExtraLevel,

                energyCap = Evaluate(
                    primary.energyCap, primary.energyCapPerExtraLevel,
                    shipLevel, attrs.EnergyCapacity, n, includeExtraComponentLevels: stackExtras),
                energyCapPerExtraLevel = primary.energyCapPerExtraLevel,

                energyRegen = Evaluate(
                    primary.energyRegen, primary.energyRegenPerExtraLevel,
                    shipLevel, attrs.EnergyRegen, n, includeExtraComponentLevels: stackExtras),
                energyRegenPerExtraLevel = primary.energyRegenPerExtraLevel,

                moveSpeed = Evaluate(
                    primary.moveSpeed, primary.moveSpeedPerExtraLevel,
                    shipLevel, attrs.MovementSpeed, n, includeExtraComponentLevels: stackExtras),
                moveSpeedPerExtraLevel = primary.moveSpeedPerExtraLevel,

                accelerationCap = Evaluate(
                    primary.accelerationCap, primary.accelerationCapPerExtraLevel,
                    shipLevel, attrs.MovementSpeed, n, includeExtraComponentLevels: stackExtras),
                accelerationCapPerExtraLevel = primary.accelerationCapPerExtraLevel,

                extraSpeedPercent = Evaluate(
                    primary.extraSpeedPercent, primary.extraSpeedPercentPerExtraLevel,
                    shipLevel, attrs.MovementSpeed, n, includeExtraComponentLevels: stackExtras),
                extraSpeedPercentPerExtraLevel = primary.extraSpeedPercentPerExtraLevel,

                extraSpeedEnergyDrain = Evaluate(
                    primary.extraSpeedEnergyDrain, primary.extraSpeedEnergyDrainPerExtraLevel,
                    shipLevel, attrs.MovementSpeed, n, includeExtraComponentLevels: stackExtras),
                extraSpeedEnergyDrainPerExtraLevel = primary.extraSpeedEnergyDrainPerExtraLevel,

                turnSpeed = Evaluate(
                    primary.turnSpeed, primary.turnSpeedPerExtraLevel,
                    shipLevel, attrs.RotationSpeed, n, includeExtraComponentLevels: stackExtras),
                turnSpeedPerExtraLevel = primary.turnSpeedPerExtraLevel,

                maxGems = Evaluate(
                    primary.maxGems, primary.maxGemsPerExtraLevel,
                    shipLevel, attrs.GemCapacity, n, includeExtraComponentLevels: stackExtras),
                maxGemsPerExtraLevel = primary.maxGemsPerExtraLevel,

                tractorBeamDistance = Evaluate(
                    primary.tractorBeamDistance, primary.tractorBeamDistancePerExtraLevel,
                    shipLevel, 0, n, includeExtraComponentLevels: stackExtras),
                tractorBeamDistancePerExtraLevel = primary.tractorBeamDistancePerExtraLevel,

                tractorBeamPower = Evaluate(
                    primary.tractorBeamPower, primary.tractorBeamPowerPerExtraLevel,
                    shipLevel, 0, n, includeExtraComponentLevels: stackExtras),
                tractorBeamPowerPerExtraLevel = primary.tractorBeamPowerPerExtraLevel,

                maxPeople = Evaluate(
                    primary.maxPeople, primary.maxPeoplePerExtraLevel,
                    shipLevel, attrs.PeopleCapacity, n, includeExtraComponentLevels: stackExtras),
                maxPeoplePerExtraLevel = primary.maxPeoplePerExtraLevel,
            };
        }

        /// <summary>
        /// Primary-per-pool aggregate, then Extra Level evaluate each pool, then field-wise sum.
        /// Weapon pools use ship+ability only (no <c>(N−1)</c>); live damage still comes from per-mount apply.
        /// </summary>
        public static ShipComponentAbilityStats AggregateAndEvaluate(
            IReadOnlyList<string> componentIds,
            IReadOnlyList<ShipComponentAbilityStats> perComponentStats,
            int shipLevel,
            in ShipAbilityLevelCounts attrs)
        {
            ShipComponentStackAggregation.AggregatePrimaries(
                componentIds,
                perComponentStats,
                out _,
                out List<ShipComponentStackAggregation.PoolContribution> pools);

            var total = default(ShipComponentAbilityStats);
            if (pools == null)
                return total;

            for (int i = 0; i < pools.Count; i++)
            {
                ShipComponentStackAggregation.PoolContribution pool = pools[i];
                ShipComponentAbilityStats evaluated = EvaluatePool(
                    pool.PrimaryStats,
                    pool.ComponentCount,
                    shipLevel,
                    in attrs,
                    pool.IsWeaponPool);
                total.AddInPlace(evaluated);
            }

            return total;
        }

        /// <summary>
        /// Same as <see cref="AggregateAndEvaluate"/> with all ability purchases at zero.
        /// </summary>
        public static ShipComponentAbilityStats AggregateAndEvaluate(
            IReadOnlyList<string> componentIds,
            IReadOnlyList<ShipComponentAbilityStats> perComponentStats,
            int shipLevel)
        {
            var attrs = default(ShipAbilityLevelCounts);
            return AggregateAndEvaluate(componentIds, perComponentStats, shipLevel, in attrs);
        }

        /// <summary>
        /// Applies cargo mobility penalties after Extra Level evaluation (move / accel / turn only).
        /// </summary>
        public static ShipComponentAbilityStats ApplyMobilityPenalties(
            in ShipComponentAbilityStats stats,
            int shipLevel)
        {
            int perLvl = Mathf.Max(0, shipLevel - 1);
            ShipCargoMobilitySettings mobility = ShipCargoMobilitySettingsCache.ResolveOrDefault();
            var result = stats;
            result.moveSpeed = ShipPropulsionAggregation.ApplyShipLevelMobilityScale(
                stats.moveSpeed, perLvl, mobility.levelMaxSpeedPenaltyFractionPerLevel);
            result.accelerationCap = ShipPropulsionAggregation.ApplyShipLevelMobilityScale(
                stats.accelerationCap, perLvl, mobility.levelAccelPenaltyFractionPerLevel);
            result.turnSpeed = ShipPropulsionAggregation.ApplyShipLevelMobilityScale(
                stats.turnSpeed, perLvl, mobility.levelTurnPenaltyFractionPerLevel);
            return result;
        }
    }
}
