using System.Collections.Generic;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Engine and thruster move speed and acceleration rules shared by <see cref="Entities.Starship"/> and editor previews.
    /// Engines and thrusters share one propulsion pool: the single best base <see cref="ShipComponentAbilityStats.moveSpeed"/>
    /// plus half the sum of every other part's <see cref="ShipComponentAbilityStats.moveSpeedPerLevel"/>.
    /// Example: 6 identical v1 parts (moveSpeed 7.8, moveSpeedPerLevel 1.56) → (7.8 + (5 × 1.56) / 2) × 0.8 ≈ 9.36 effective top speed.
    /// Acceleration caps sum across all engines and thrusters (same global scale).
    /// </summary>
    public static class ShipPropulsionAggregation
    {
        /// <summary>Applied to aggregated top speed and acceleration at runtime (0.8 = 20% slower overall).</summary>
        public const float OverallPropulsionSpeedMultiplier = 0.8f;

        /// <summary>Per-level terms for non-propulsion stats (~25% of base). Used when balancing weapon energy after scan.</summary>
        public const float PerLevelFractionOfBase = 0.25f;

        /// <summary>Engine/thruster moveSpeedPerLevel and accelerationCapPerLevel are this fraction of base (20%).</summary>
        public const float PropulsionPerLevelFractionOfBase = 0.20f;

        /// <summary>Per level after 1, mobility loses this fraction of the base stat (matches Starship).</summary>
        public const float ShipLevelMobilityPenaltyFractionPerLevel = 0.11f;

        /// <summary>Each additional engine/thruster contributes moveSpeedPerLevel × this factor (0.5 = half).</summary>
        public const float AdditionalPropulsionMoveSpeedPerLevelFactor = 0.5f;

        /// <summary>
        /// Visual banking (°) when turn rate equals the global max ship turn speed (see <see cref="ShipFamilyDefinition.GetGlobalMaxUpgradeTreeTurnSpeedAuthoredUnits"/>).
        /// </summary>
        public const float VisualBankReferenceMaxAngleDegrees = 111f;

        /// <summary>
        /// Fallback max turn speed (authored units, level 1) when no upgrade-tree breakdown is available.
        /// Matches ForceBadger tier scan (~43.4); runtime uses the max across all loaded families when possible.
        /// </summary>
        public const float VisualBankReferenceMaxTurnSpeedAuthoredUnits = 43.40541f;

        /// <summary>
        /// Target visual bank angle (°): 0 turn rate → 0°, global max turn rate → <paramref name="maxBankDegrees"/>.
        /// </summary>
        public static float ComputeVisualBankTargetAngle(
            float signedAngularVelDegPerSec,
            float maxBankDegrees,
            float globalMaxTurnDegPerSec)
        {
            if (globalMaxTurnDegPerSec <= 0f || Mathf.Abs(signedAngularVelDegPerSec) <= 0f)
                return 0f;

            float turnRatio = Mathf.Clamp01(Mathf.Abs(signedAngularVelDegPerSec) / globalMaxTurnDegPerSec);
            return Mathf.Sign(signedAngularVelDegPerSec) * turnRatio * maxBankDegrees;
        }

        /// <summary>Global max ship turn speed in °/s for visual banking (family definition units × scale).</summary>
        public static float GetGlobalMaxTurnSpeedDegreesPerSecond(float definitionUnitsToDegreesPerSecond = 10f)
        {
            float authored = ShipFamilyDefinition.GetGlobalMaxUpgradeTreeTurnSpeedAuthoredUnits();
            return authored * definitionUnitsToDegreesPerSecond;
        }

        /// <summary>Scan/auto-populate move speed for engine/thruster version 1 (Engine_1), before <see cref="OverallPropulsionSpeedMultiplier"/>.</summary>
        public const float SuggestedPropulsionMoveSpeedV1 = ShipComponentPropulsionSuggestions.MoveSpeedV1;

        /// <summary>Move speed added per version tier (v2 = 11, v3 = 13, …), before global propulsion scale.</summary>
        public const float SuggestedPropulsionMoveSpeedPerVersion = ShipComponentPropulsionSuggestions.MoveSpeedPerVersion;

        /// <summary>Acceleration cap as a fraction of suggested move speed for that version.</summary>
        public const float SuggestedPropulsionAccelerationFractionOfMoveSpeed =
            ShipComponentPropulsionSuggestions.AccelerationFractionOfMoveSpeed;

        /// <summary>Engine/thruster move speed from version: v1=7.8, v2=10.4, v3=13, …</summary>
        public static float GetSuggestedPropulsionMoveSpeed(int version) =>
            ShipComponentPropulsionSuggestions.GetSuggestedMoveSpeed(version);

        /// <summary>Engine/thruster acceleration cap from version (half of move speed by default).</summary>
        public static float GetSuggestedPropulsionAccelerationCap(int version) =>
            ShipComponentPropulsionSuggestions.GetSuggestedAccelerationCap(version);

        /// <summary>moveSpeedPerLevel for scan/auto-populate (20% of base move speed for that version).</summary>
        public static float GetSuggestedPropulsionMoveSpeedPerLevel(int version) =>
            ShipComponentPropulsionSuggestions.GetSuggestedMoveSpeedPerLevel(version);

        /// <summary>accelerationCapPerLevel for scan/auto-populate (20% of base acceleration for that version).</summary>
        public static float GetSuggestedPropulsionAccelerationCapPerLevel(int version) =>
            ShipComponentPropulsionSuggestions.GetSuggestedAccelerationCapPerLevel(version);

        public static float ApplyOverallPropulsionSpeedScale(float value)
        {
            return value * OverallPropulsionSpeedMultiplier;
        }

        public struct Result
        {
            public float topMoveSpeed;
            public float sumAcceleration;
            /// <summary>Index into matched component lists for the part whose base moveSpeed was used once.</summary>
            public int primaryIndex;
            /// <summary>Effective extra top speed from non-primary parts (half the summed moveSpeedPerLevel).</summary>
            public float extraMoveSpeedFromPerLevel;
        }

        public static float ApplyShipLevelMobilityScale(float baseStat, int levelsAfterFirst)
        {
            if (levelsAfterFirst <= 0 || baseStat <= 0f)
                return baseStat;
            return baseStat - (baseStat * ShipLevelMobilityPenaltyFractionPerLevel) * levelsAfterFirst;
        }

        /// <summary>
        /// Per-part acceleration before the global propulsion scale. Uses authored <see cref="ShipComponentAbilityStats.accelerationCap"/>
        /// (summed for thrusters and engines). When cap is unset, derives from move speed using the same ratio as scan suggestions.
        /// </summary>
        public static float GetPropulsionAccelerationContribution(
            ShipComponentAbilityStats comp,
            int levelsAfterFirst)
        {
            float authored = comp.accelerationCap + comp.accelerationCapPerLevel * levelsAfterFirst;
            if (authored > 0f)
                return authored;

            if (comp.moveSpeed <= 0f && comp.moveSpeedPerLevel <= 0f)
                return 0f;

            float moveDerived = comp.moveSpeed * SuggestedPropulsionAccelerationFractionOfMoveSpeed;
            float movePerLevelDerived = comp.moveSpeedPerLevel * SuggestedPropulsionAccelerationFractionOfMoveSpeed;
            return moveDerived + movePerLevelDerived * levelsAfterFirst;
        }

        /// <summary>
        /// Computes shared engine/thruster top speed and total acceleration from per-component stats at a ship level.
        /// Acceleration = sum of every matched engine and thruster part (each instance on the prefab counts).
        /// </summary>
        public static Result ComputeThrusterPropulsion(
            IReadOnlyList<string> componentIds,
            IReadOnlyList<ShipComponentAbilityStats> perComponentStats,
            int shipLevel)
        {
            var result = new Result { primaryIndex = -1 };
            if (componentIds == null || perComponentStats == null)
                return result;

            int count = Mathf.Min(componentIds.Count, perComponentStats.Count);
            if (count == 0)
                return result;

            int levelsAfterFirst = Mathf.Max(0, shipLevel - 1);
            float bestPrimaryMove = 0f;

            for (int i = 0; i < count; i++)
            {
                if (!ShipComponentAbilityStats.IsPropulsionComponent(componentIds[i]))
                    continue;

                ShipComponentAbilityStats comp = perComponentStats[i];
                if (comp.moveSpeed > bestPrimaryMove)
                {
                    bestPrimaryMove = comp.moveSpeed;
                    result.primaryIndex = i;
                }
            }

            if (result.primaryIndex >= 0)
            {
                float primaryMove = ApplyShipLevelMobilityScale(
                    perComponentStats[result.primaryIndex].moveSpeed,
                    levelsAfterFirst);

                float summedExtraPerLevel = 0f;
                for (int i = 0; i < count; i++)
                {
                    if (!ShipComponentAbilityStats.IsPropulsionComponent(componentIds[i]))
                        continue;
                    if (i == result.primaryIndex)
                        continue;
                    summedExtraPerLevel += Mathf.Max(0f, perComponentStats[i].moveSpeedPerLevel);
                }

                result.extraMoveSpeedFromPerLevel =
                    summedExtraPerLevel * AdditionalPropulsionMoveSpeedPerLevelFactor;
                result.topMoveSpeed = Mathf.Max(
                    0.1f,
                    primaryMove + result.extraMoveSpeedFromPerLevel);
            }

            for (int i = 0; i < count; i++)
            {
                if (!ShipComponentAbilityStats.IsPropulsionComponent(componentIds[i]))
                    continue;

                ShipComponentAbilityStats comp = perComponentStats[i];
                result.sumAcceleration += Mathf.Max(
                    0f,
                    GetPropulsionAccelerationContribution(comp, levelsAfterFirst));
            }

            result.topMoveSpeed = ApplyOverallPropulsionSpeedScale(result.topMoveSpeed);
            result.extraMoveSpeedFromPerLevel = ApplyOverallPropulsionSpeedScale(result.extraMoveSpeedFromPerLevel);
            result.sumAcceleration = ApplyOverallPropulsionSpeedScale(result.sumAcceleration);

            return result;
        }

        /// <summary>
        /// Replaces naively summed engine/thruster move stats in a total with the shared propulsion aggregation.
        /// Call after summing all scaled component stats (preview, power score, etc.).
        /// </summary>
        public static ShipComponentAbilityStats ApplyPropulsionToSummedStats(
            ShipComponentAbilityStats total,
            IReadOnlyList<string> componentIds,
            IReadOnlyList<ShipComponentAbilityStats> perComponentStats,
            int shipLevel = 1)
        {
            if (componentIds == null || perComponentStats == null)
                return total;

            int count = Mathf.Min(componentIds.Count, perComponentStats.Count);
            if (count == 0)
                return total;

            for (int i = 0; i < count; i++)
            {
                if (!ShipComponentAbilityStats.IsPropulsionComponent(componentIds[i]))
                    continue;

                ShipComponentAbilityStats s = perComponentStats[i];
                total.moveSpeed -= s.moveSpeed;
                total.moveSpeedPerLevel -= s.moveSpeedPerLevel;
                total.accelerationCap -= s.accelerationCap;
                total.accelerationCapPerLevel -= s.accelerationCapPerLevel;
            }

            Result propulsion = ComputeThrusterPropulsion(componentIds, perComponentStats, shipLevel);
            total.moveSpeed = Mathf.Max(0f, total.moveSpeed) + propulsion.topMoveSpeed;
            total.accelerationCap = Mathf.Max(0f, total.accelerationCap) + propulsion.sumAcceleration;

            for (int i = 0; i < count; i++)
            {
                if (!ShipComponentAbilityStats.IsPropulsionComponent(componentIds[i]))
                    continue;
                total.accelerationCapPerLevel += perComponentStats[i].accelerationCapPerLevel;
            }

            if (propulsion.primaryIndex >= 0 && propulsion.primaryIndex < count)
            {
                total.moveSpeedPerLevel = Mathf.Max(0f, total.moveSpeedPerLevel)
                    + perComponentStats[propulsion.primaryIndex].moveSpeedPerLevel;
            }

            return total;
        }

        /// <summary>
        /// Sustained energy drain per second when firing (fireRate × damagePerBullet; damage equals fire power at runtime).
        /// </summary>
        public static float ComputeWeaponSustainedEnergyDrain(ShipComponentAbilityStats weaponStats, int firePowerUpgrades = 0)
        {
            float firePower = weaponStats.firePower + weaponStats.firePowerPerLevel * Mathf.Max(0, firePowerUpgrades);
            float fireRate = Mathf.Max(0.01f, weaponStats.fireRate + weaponStats.fireRatePerLevel * Mathf.Max(0, firePowerUpgrades));
            return firePower * fireRate;
        }

        /// <summary>
        /// Sets each weapon's energy stats from <see cref="ShipComponentWeaponSuggestions"/> (burst pool from legacy drain rate, regen below sustained drain).
        /// </summary>
        public static void BalanceWeaponEnergyForComponents(
            IList<ShipFamilyComponentEntry> components,
            float sustainedFireRateShotsPerSecond = ShipComponentWeaponSuggestions.SustainedFireRateShotsPerSecond,
            float capacitySecondsAtFullDrain = ShipComponentWeaponSuggestions.BurstSecondsAtFullDrain)
        {
            if (components == null || components.Count == 0)
                return;

            for (int i = 0; i < components.Count; i++)
            {
                ShipFamilyComponentEntry entry = components[i];
                if (entry == null || string.IsNullOrWhiteSpace(entry.componentId))
                    continue;

                entry.EnsureStatCategories();
                if (!entry.statCategories.Contains(ShipComponentStatCategory.Energy))
                    continue;

                string partType = ShipComponentAbilityStats.ResolvePartTypeForSuggestedStats(entry.componentId);
                if (!string.Equals(partType, "Weapon", System.StringComparison.OrdinalIgnoreCase))
                    continue;

                ShipComponentWeaponSuggestions.ApplyBalancedEnergy(
                    ref entry.stats,
                    sustainedFireRateShotsPerSecond,
                    capacitySecondsAtFullDrain);
                if (entry.stats.energyCap <= 0f)
                    continue;
                entry.stats = ShipComponentAbilityStats.KeepOnlyAuthoringFields(
                    entry.stats,
                    entry.statCategories,
                    entry.componentId);
            }
        }
    }
}
