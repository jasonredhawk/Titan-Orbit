using System;
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
    /// Each part uses <b>its own</b> PerExtraLevel — an engine’s move PerExtra is not
    /// reused for a thruster. Only the <b>primary</b> part in a non-weapon pool contributes
    /// Base. Extra parts add <c>their PerExtra × levels</c> (no extra Bases).
    /// </para>
    /// <para>
    /// Non-weapons:
    /// <c>PrimaryBase + Σ (part.PerExtra × ((shipLevel − 1) + abilityLevel))</c>
    /// Buying a second cockpit / engine / wing raises the total by that part’s PerExtra
    /// steps, not by copying another Base into the hull.
    /// </para>
    /// <para>
    /// [TITAN-ORBIT] Callers pass Base / PerExtra already multiplied by prefab starting
    /// <c>localScale</c> (<see cref="ShipComponentAbilityStatsMath.ScaleStatsByTransform"/>).
    /// A Cockpit at scale 3 is <c>3 ×</c> catalog Health / Gems / Troops. Ability details
    /// cards show that multiply as its own formula step. Moon-store extras stay at ×1.
    /// </para>
    /// <para>
    /// Weapons (each barrel, same Extra Level — no component-count term):
    /// <c>Base + PerExtraLevel × ((shipLevel − 1) + abilityLevel)</c>
    /// Live mounts each use their own Base / PerExtra.
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
        /// <param name="baseValue">This part’s Base for the field (already × prefab start scale when scanned from a chassis).</param>
        /// <param name="perExtraLevel">This part’s own Per Extra Level step (same start-scale multiply as <paramref name="baseValue"/>). Engines and thrusters must pass different values.</param>
        /// <param name="shipLevel">Chassis tier (1-based).</param>
        /// <param name="abilityLevel">Bottom-HUD purchases for the matching ability (0 if none).</param>
        /// <param name="componentCount">Unused for Extra Level steps (each part is evaluated alone). Pass 0 to force a zero result.</param>
        /// <param name="includeExtraComponentLevels">
        /// Kept for older call sites. Extra parts are separate Evaluate calls with
        /// <paramref name="includeBase"/> false.
        /// </param>
        /// <param name="includeBase">
        /// True for the pool primary (and every weapon barrel). False for stacked extras —
        /// they contribute PerExtra × levels only.
        /// </param>
        public static float Evaluate(
            float baseValue,
            float perExtraLevel,
            int shipLevel,
            int abilityLevel,
            int componentCount,
            bool includeExtraComponentLevels,
            bool includeBase = true)
        {
            _ = includeExtraComponentLevels;

            // Empty pool / missing part — do not invent a Base from nowhere.
            if (componentCount <= 0)
                return 0f;

            // --- Ship tier above 1 + ability purchases ---
            int levels = Mathf.Max(0, shipLevel - 1) + Mathf.Max(0, abilityLevel);
            float usedBase = includeBase ? baseValue : 0f;
            return usedBase + perExtraLevel * levels;
        }

        /// <summary>
        /// Extra Level multiplier steps for one non-weapon part:
        /// <c>(shipLevel−1) + abilityLevel</c>.
        /// <paramref name="componentCount"/> is ignored (kept so older call sites compile).
        /// Each extra part is evaluated on its own Base / PerExtra instead of adding <c>(N−1)</c>
        /// of the primary’s PerExtra.
        /// </summary>
        public static int CountExtraLevels(int shipLevel, int abilityLevel, int componentCount)
        {
            _ = componentCount;
            return Mathf.Max(0, shipLevel - 1) + Mathf.Max(0, abilityLevel);
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
        /// Evaluates every field on one part (or a leftover single-block fallback).
        /// Pass <paramref name="includeBase"/> false for stacked extras — PerExtra still scales,
        /// Base stays 0 so a second thruster does not double Move Base.
        /// <c>*PerExtraLevel</c> fields are copied through unchanged (tooltips / next-buy steps).
        /// </summary>
        public static ShipComponentAbilityStats EvaluatePool(
            in ShipComponentAbilityStats primary,
            int componentCount,
            int shipLevel,
            in ShipAbilityLevelCounts attrs,
            bool isWeaponPool,
            bool includeBase = true)
        {
            int n = Mathf.Max(0, componentCount);
            // [TITAN-ORBIT] Weapons ignore N — each barrel uses ship+ability only on its own Base.
            bool stackExtras = !isWeaponPool;
            float usedBase(float v) => includeBase ? v : 0f;

            // --- Bullet speed ---
            // [TITAN-ORBIT] Weapon bullet speed grows only with Bullet Speed ability purchases.
            float bulletSpeedValue = isWeaponPool
                ? EvaluateWeaponBulletSpeed(
                    usedBase(primary.bulletSpeed), primary.bulletSpeedPerExtraLevel, attrs.BulletSpeed)
                : Evaluate(
                    usedBase(primary.bulletSpeed), primary.bulletSpeedPerExtraLevel,
                    shipLevel, attrs.BulletSpeed, n, includeExtraComponentLevels: stackExtras,
                    includeBase: true);

            return new ShipComponentAbilityStats
            {
                firePower = Evaluate(
                    usedBase(primary.firePower), primary.firePowerPerExtraLevel,
                    shipLevel, attrs.FirePower, n, includeExtraComponentLevels: stackExtras),
                firePowerPerExtraLevel = primary.firePowerPerExtraLevel,

                bulletSpeed = bulletSpeedValue,
                bulletSpeedPerExtraLevel = primary.bulletSpeedPerExtraLevel,

                // [TITAN-ORBIT] No bottom-HUD Bullet Range ability — abilityLevel stays 0.
                bulletRange = Evaluate(
                    usedBase(primary.bulletRange), primary.bulletRangePerExtraLevel,
                    shipLevel, 0, n, includeExtraComponentLevels: stackExtras),
                bulletRangePerExtraLevel = primary.bulletRangePerExtraLevel,

                // [TITAN-ORBIT] No Fire Rate ability — ship/component levels only.
                fireRate = Evaluate(
                    usedBase(primary.fireRate), primary.fireRatePerExtraLevel,
                    shipLevel, 0, n, includeExtraComponentLevels: stackExtras),
                fireRatePerExtraLevel = primary.fireRatePerExtraLevel,

                rammingPower = Evaluate(
                    usedBase(primary.rammingPower), primary.rammingPowerPerExtraLevel,
                    shipLevel, 0, n, includeExtraComponentLevels: stackExtras),
                rammingPowerPerExtraLevel = primary.rammingPowerPerExtraLevel,

                healthCap = Evaluate(
                    usedBase(primary.healthCap), primary.healthCapPerExtraLevel,
                    shipLevel, attrs.MaxHealth, n, includeExtraComponentLevels: stackExtras),
                healthCapPerExtraLevel = primary.healthCapPerExtraLevel,

                healthRegen = Evaluate(
                    usedBase(primary.healthRegen), primary.healthRegenPerExtraLevel,
                    shipLevel, attrs.HealthRegen, n, includeExtraComponentLevels: stackExtras),
                healthRegenPerExtraLevel = primary.healthRegenPerExtraLevel,

                energyCap = Evaluate(
                    usedBase(primary.energyCap), primary.energyCapPerExtraLevel,
                    shipLevel, attrs.EnergyCapacity, n, includeExtraComponentLevels: stackExtras),
                energyCapPerExtraLevel = primary.energyCapPerExtraLevel,

                energyRegen = Evaluate(
                    usedBase(primary.energyRegen), primary.energyRegenPerExtraLevel,
                    shipLevel, attrs.EnergyRegen, n, includeExtraComponentLevels: stackExtras),
                energyRegenPerExtraLevel = primary.energyRegenPerExtraLevel,

                moveSpeed = Evaluate(
                    usedBase(primary.moveSpeed), primary.moveSpeedPerExtraLevel,
                    shipLevel, attrs.MovementSpeed, n, includeExtraComponentLevels: stackExtras),
                moveSpeedPerExtraLevel = primary.moveSpeedPerExtraLevel,

                accelerationCap = Evaluate(
                    usedBase(primary.accelerationCap), primary.accelerationCapPerExtraLevel,
                    shipLevel, attrs.MovementSpeed, n, includeExtraComponentLevels: stackExtras),
                accelerationCapPerExtraLevel = primary.accelerationCapPerExtraLevel,

                extraSpeedPercent = Evaluate(
                    usedBase(primary.extraSpeedPercent), primary.extraSpeedPercentPerExtraLevel,
                    shipLevel, attrs.MovementSpeed, n, includeExtraComponentLevels: stackExtras),
                extraSpeedPercentPerExtraLevel = primary.extraSpeedPercentPerExtraLevel,

                extraSpeedEnergyDrain = Evaluate(
                    usedBase(primary.extraSpeedEnergyDrain), primary.extraSpeedEnergyDrainPerExtraLevel,
                    shipLevel, attrs.MovementSpeed, n, includeExtraComponentLevels: stackExtras),
                extraSpeedEnergyDrainPerExtraLevel = primary.extraSpeedEnergyDrainPerExtraLevel,

                turnSpeed = Evaluate(
                    usedBase(primary.turnSpeed), primary.turnSpeedPerExtraLevel,
                    shipLevel, attrs.RotationSpeed, n, includeExtraComponentLevels: stackExtras),
                turnSpeedPerExtraLevel = primary.turnSpeedPerExtraLevel,

                maxGems = Evaluate(
                    usedBase(primary.maxGems), primary.maxGemsPerExtraLevel,
                    shipLevel, attrs.GemCapacity, n, includeExtraComponentLevels: stackExtras),
                maxGemsPerExtraLevel = primary.maxGemsPerExtraLevel,

                tractorBeamDistance = Evaluate(
                    usedBase(primary.tractorBeamDistance), primary.tractorBeamDistancePerExtraLevel,
                    shipLevel, 0, n, includeExtraComponentLevels: stackExtras),
                tractorBeamDistancePerExtraLevel = primary.tractorBeamDistancePerExtraLevel,

                tractorBeamPower = Evaluate(
                    usedBase(primary.tractorBeamPower), primary.tractorBeamPowerPerExtraLevel,
                    shipLevel, 0, n, includeExtraComponentLevels: stackExtras),
                tractorBeamPowerPerExtraLevel = primary.tractorBeamPowerPerExtraLevel,

                maxPeople = Evaluate(
                    usedBase(primary.maxPeople), primary.maxPeoplePerExtraLevel,
                    shipLevel, attrs.PeopleCapacity, n, includeExtraComponentLevels: stackExtras),
                maxPeoplePerExtraLevel = primary.maxPeoplePerExtraLevel,
            };
        }

        /// <summary>
        /// Extra-Levels every matched part with <b>that part’s</b> PerExtra, then sums.
        /// Non-weapon pools keep <b>only the primary Base</b>. Extras add PerExtra × levels.
        /// Weapons keep each barrel’s Base (they fire on their own).
        /// <para>
        /// [TITAN-ORBIT] Movement is Engine + Thruster: primary Move Base + engine PerExtra
        /// × levels + thruster PerExtra × levels. A CosmicShark thruster does not inherit
        /// an AstroEagle engine’s PerExtra, and it does not add a second Move Base.
        /// </para>
        /// Weapon projectile speed / range stay <b>max</b> across barrels (one travel speed,
        /// not N× guns). OVERDRIVE percent is also max (AddInPlace already does this).
        /// Live shot damage still comes from per-mount apply after this hull block.
        /// </summary>
        /// <param name="storeExtraStartIndex">
        /// First moon-store extra index so the newest purchase is the display primary.
        /// <see cref="int.MaxValue"/> when the hull has no store gear.
        /// </param>
        public static ShipComponentAbilityStats AggregateAndEvaluate(
            IReadOnlyList<string> componentIds,
            IReadOnlyList<ShipComponentAbilityStats> perComponentStats,
            int shipLevel,
            in ShipAbilityLevelCounts attrs,
            int storeExtraStartIndex = int.MaxValue)
        {
            var total = default(ShipComponentAbilityStats);
            if (componentIds == null || perComponentStats == null)
                return total;

            int count = Mathf.Min(componentIds.Count, perComponentStats.Count);
            if (count <= 0)
                return total;

            // --- Group by stack pool so we know which part owns Base ---
            var groups = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < count; i++)
            {
                string id = componentIds[i];
                if (string.IsNullOrWhiteSpace(id))
                    continue;
                if (ShipFamilyPartCalcProfileSet.IsCosmeticPartName(id))
                    continue;

                string key = ShipComponentStackAggregation.ResolveStackPoolKey(id);
                if (!groups.TryGetValue(key, out List<int> list))
                {
                    list = new List<int>(4);
                    groups[key] = list;
                }

                list.Add(i);
            }

            // Weapon travel is one hull value (fastest barrel), not a sum of every gun.
            float maxWeaponBulletSpeed = 0f;
            float maxWeaponBulletSpeedPer = 0f;
            float maxWeaponBulletRange = 0f;
            float maxWeaponBulletRangePer = 0f;
            bool anyWeapon = false;

            foreach (KeyValuePair<string, List<int>> pair in groups)
            {
                bool weapon = ShipComponentStackAggregation.IsWeaponPoolKey(pair.Key);
                int primaryLocal = ShipComponentStackAggregation.PickPrimaryLocalIndex(
                    pair.Key, pair.Value, perComponentStats, storeExtraStartIndex);

                for (int m = 0; m < pair.Value.Count; m++)
                {
                    int gi = pair.Value[m];
                    // Weapons keep every barrel’s Base. Stacked extras do not.
                    bool includeBase = weapon || m == primaryLocal;
                    ShipComponentAbilityStats evaluated = EvaluatePool(
                        perComponentStats[gi],
                        componentCount: 1,
                        shipLevel,
                        in attrs,
                        isWeaponPool: weapon,
                        includeBase: includeBase);

                    if (weapon)
                    {
                        anyWeapon = true;
                        maxWeaponBulletSpeed = Mathf.Max(maxWeaponBulletSpeed, evaluated.bulletSpeed);
                        maxWeaponBulletSpeedPer = Mathf.Max(
                            maxWeaponBulletSpeedPer, evaluated.bulletSpeedPerExtraLevel);
                        maxWeaponBulletRange = Mathf.Max(maxWeaponBulletRange, evaluated.bulletRange);
                        maxWeaponBulletRangePer = Mathf.Max(
                            maxWeaponBulletRangePer, evaluated.bulletRangePerExtraLevel);
                        evaluated.bulletSpeed = 0f;
                        evaluated.bulletSpeedPerExtraLevel = 0f;
                        evaluated.bulletRange = 0f;
                        evaluated.bulletRangePerExtraLevel = 0f;
                    }

                    total.AddInPlace(evaluated);
                }
            }

            if (anyWeapon)
            {
                total.bulletSpeed += maxWeaponBulletSpeed;
                total.bulletSpeedPerExtraLevel += maxWeaponBulletSpeedPer;
                total.bulletRange += maxWeaponBulletRange;
                total.bulletRangePerExtraLevel += maxWeaponBulletRangePer;
            }

            return total;
        }

        /// <summary>
        /// Same as <see cref="AggregateAndEvaluate"/> with all ability purchases at zero.
        /// </summary>
        public static ShipComponentAbilityStats AggregateAndEvaluate(
            IReadOnlyList<string> componentIds,
            IReadOnlyList<ShipComponentAbilityStats> perComponentStats,
            int shipLevel,
            int storeExtraStartIndex = int.MaxValue)
        {
            var attrs = default(ShipAbilityLevelCounts);
            return AggregateAndEvaluate(
                componentIds, perComponentStats, shipLevel, in attrs, storeExtraStartIndex);
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
