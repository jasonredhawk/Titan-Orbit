using System.Collections.Generic;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Engine and thruster move-speed and acceleration rules shared by legacy <see cref="Entities.Starship"/>,
    /// ECS motor, and editor previews. [TITAN-ORBIT] Engines/thrusters share one propulsion pool: the single
    /// best base moveSpeed plus half the sum of every other part's moveSpeedPerLevel, then a global 0.8 scale.
    /// Acceleration caps sum across all propulsion parts. Paired with <see cref="ShipFamilyStatsCalculator"/>.
    /// </summary>
    public static class ShipPropulsionAggregation
    {
        /// <summary>Applied to aggregated top speed and acceleration at runtime (0.8 = 20% slower overall).</summary>
        public const float OverallPropulsionSpeedMultiplier = 0.8f;

        /// <summary>
        /// Multiplier on engine thrust force at runtime (legacy Starship <c>ENGINE_THRUST_VISIBILITY</c>).
        /// Keeps acceleration snappy while mass still scales thrust via F/m.
        /// </summary>
        public const float EngineThrustVisibility = 10f;

        /// <summary>Per-level terms for non-propulsion stats (~25% of base). Used when balancing weapon energy after scan.</summary>
        public const float PerLevelFractionOfBase = 0.25f;

        /// <summary>Engine/thruster moveSpeedPerLevel and accelerationCapPerLevel are this fraction of base (20%).</summary>
        public const float PropulsionPerLevelFractionOfBase = 0.20f;

        /// <summary>
        /// Legacy default MaxSpeed/turn level drag (11% per level after 1).
        /// Prefer <see cref="ShipCargoMobilitySettings.levelMaxSpeedPenaltyFractionPerLevel"/> /
        /// <see cref="ShipCargoMobilitySettings.levelTurnPenaltyFractionPerLevel"/> at runtime.
        /// </summary>
        public const float DefaultLevelMobilityPenaltyFractionPerLevel = 0.11f;

        /// <summary>
        /// Legacy default accel level drag (0% — accel only grew with *PerLevel).
        /// Prefer <see cref="ShipCargoMobilitySettings.levelAccelPenaltyFractionPerLevel"/>.
        /// </summary>
        public const float DefaultLevelAccelPenaltyFractionPerLevel = 0f;

        /// <summary>
        /// Family-authored <see cref="ShipComponentAbilityStats.turnSpeed"/> uses small definition units;
        /// multiply by this at runtime only (rotation/banking), not in power-score UI.
        /// </summary>
        public const float TurnDefinitionToDegreesPerSecond = 10f;

        /// <summary>
        /// Visual banking (°) when turn rate equals the global max ship turn speed
        /// (see <see cref="ShipFamilyDefinition.GetGlobalMaxUpgradeTreeTurnSpeedAuthoredUnits"/>).
        /// </summary>
        public const float VisualBankReferenceMaxAngleDegrees = 111f;

        /// <summary>
        /// Fallback max turn speed (authored units, level 1) when no upgrade-tree breakdown is available.
        /// </summary>
        public const float VisualBankReferenceMaxTurnSpeedAuthoredUnits = 43.40541f;

        public static float ConvertTurnDefinitionToDegreesPerSecond(float turnDefinition) =>
            Mathf.Max(1f, turnDefinition) * TurnDefinitionToDegreesPerSecond;

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
        public static float GetGlobalMaxTurnSpeedDegreesPerSecond(
            float definitionUnitsToDegreesPerSecond = TurnDefinitionToDegreesPerSecond)
        {
            float authored = ShipFamilyDefinition.GetGlobalMaxUpgradeTreeTurnSpeedAuthoredUnits();
            return authored * definitionUnitsToDegreesPerSecond;
        }

        /// <summary>Each additional engine/thruster contributes moveSpeedPerLevel × this factor (0.5 = half).</summary>
        public const float AdditionalPropulsionMoveSpeedPerLevelFactor = 0.5f;

        /// <summary>Scan/auto-populate move speed for engine/thruster version 1 (Engine_1), before <see cref="OverallPropulsionSpeedMultiplier"/>.</summary>
        public const float SuggestedPropulsionMoveSpeedV1 = 6f;

        /// <summary>Move speed added per version tier (v2 = 8, v3 = 10, …), before global propulsion scale.</summary>
        public const float SuggestedPropulsionMoveSpeedPerVersion = 2f;

        /// <summary>Acceleration cap as a fraction of suggested move speed for that version.</summary>
        public const float SuggestedPropulsionAccelerationFractionOfMoveSpeed = 0.5f;

        /// <summary>Engine/thruster move speed from version: v1=6, v2=8, v3=10, …</summary>
        public static float GetSuggestedPropulsionMoveSpeed(int version)
        {
            int v = Mathf.Max(1, version);
            return SuggestedPropulsionMoveSpeedV1 + (v - 1) * SuggestedPropulsionMoveSpeedPerVersion;
        }

        /// <summary>Engine/thruster acceleration cap from version (half of move speed by default).</summary>
        public static float GetSuggestedPropulsionAccelerationCap(int version)
        {
            return GetSuggestedPropulsionMoveSpeed(version) * SuggestedPropulsionAccelerationFractionOfMoveSpeed;
        }

        /// <summary>moveSpeedPerLevel for scan/auto-populate (20% of base move speed for that version).</summary>
        public static float GetSuggestedPropulsionMoveSpeedPerLevel(int version)
        {
            return GetSuggestedPropulsionMoveSpeed(version) * PropulsionPerLevelFractionOfBase;
        }

        /// <summary>accelerationCapPerLevel for scan/auto-populate (20% of base acceleration for that version).</summary>
        public static float GetSuggestedPropulsionAccelerationCapPerLevel(int version)
        {
            return GetSuggestedPropulsionAccelerationCap(version) * PropulsionPerLevelFractionOfBase;
        }

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

        /// <summary>
        /// Applies a per-level mobility drag: <c>stat - stat × penaltyFraction × levelsAfterFirst</c>.
        /// [TITAN-ORBIT] Fraction comes from <see cref="ShipCargoMobilitySettings"/> (0 = no effect).
        /// Capacity tax in <see cref="ShipMobilityResolution"/> stacks after this when writing motor config.
        /// </summary>
        /// <param name="baseStat">Pre-penalty value (usually already includes *PerLevel growth).</param>
        /// <param name="levelsAfterFirst">shipLevel − 1 (0 at level 1).</param>
        /// <param name="penaltyFractionPerLevel">
        /// From settings (e.g. 0.11). When ≤ 0, returns <paramref name="baseStat"/> unchanged.
        /// </param>
        public static float ApplyShipLevelMobilityScale(
            float baseStat,
            int levelsAfterFirst,
            float penaltyFractionPerLevel)
        {
            if (levelsAfterFirst <= 0 || baseStat <= 0f || penaltyFractionPerLevel <= 0f)
                return baseStat;
            return baseStat - (baseStat * penaltyFractionPerLevel) * levelsAfterFirst;
        }

        /// <summary>
        /// Overload using cached <see cref="ShipCargoMobilitySettings"/> MaxSpeed level penalty
        /// (legacy callers that only scaled move).
        /// </summary>
        public static float ApplyShipLevelMobilityScale(float baseStat, int levelsAfterFirst)
        {
            float fraction = ShipCargoMobilitySettingsCache.ResolveOrDefault()
                .levelMaxSpeedPenaltyFractionPerLevel;
            return ApplyShipLevelMobilityScale(baseStat, levelsAfterFirst, fraction);
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

            // --- Pick primary engine/thruster (highest base moveSpeed counts once) ---
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
                // [TITAN-ORBIT] Level MaxSpeed drag from ShipCargoMobilitySettings (0 = off).
                float levelSpeedPenalty = ShipCargoMobilitySettingsCache.ResolveOrDefault()
                    .levelMaxSpeedPenaltyFractionPerLevel;
                float primaryMove = ApplyShipLevelMobilityScale(
                    perComponentStats[result.primaryIndex].moveSpeed,
                    levelsAfterFirst,
                    levelSpeedPenalty);

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

            // --- Sum acceleration from every propulsion part (each instance on the prefab counts) ---
            for (int i = 0; i < count; i++)
            {
                if (!ShipComponentAbilityStats.IsPropulsionComponent(componentIds[i]))
                    continue;

                ShipComponentAbilityStats comp = perComponentStats[i];
                result.sumAcceleration += Mathf.Max(
                    0f,
                    GetPropulsionAccelerationContribution(comp, levelsAfterFirst));
            }

            // [TITAN-ORBIT] Optional level accel drag (default 0 — legacy had no accel level penalty).
            float levelAccelPenalty = ShipCargoMobilitySettingsCache.ResolveOrDefault()
                .levelAccelPenaltyFractionPerLevel;
            result.sumAcceleration = ApplyShipLevelMobilityScale(
                result.sumAcceleration,
                levelsAfterFirst,
                levelAccelPenalty);

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
        /// After Scan / Populate, rebalance Energy stats on weapon components from fire power + rate.
        /// Cannons: cap ≈ one max Fire Power attribute shot. Bullets: short burst. Regen always
        /// slower than sustained drain. Called from the ShipFamilyDefinition editor.
        /// </summary>
        public static void BalanceWeaponEnergyForComponents(List<ShipFamilyComponentEntry> components)
        {
            if (components == null)
                return;

            for (int i = 0; i < components.Count; i++)
            {
                ShipFamilyComponentEntry entry = components[i];
                if (entry == null || string.IsNullOrWhiteSpace(entry.componentId))
                    continue;
                if (!ShipComponentAbilityStats.IsWeaponComponent(entry.componentId))
                    continue;

                ShipComponentAbilityStats stats = entry.stats;
                string partType = ShipFamilyPartTypes.Normalize(
                    ShipComponentAbilityStats.ResolvePartTypeForSuggestedStats(entry.componentId),
                    entry.componentId);

                if (string.Equals(partType, ShipFamilyPartTypes.WeaponCannon, System.StringComparison.OrdinalIgnoreCase))
                    ShipComponentWeaponSuggestions.ApplyCannonBalancedEnergy(ref stats);
                else
                    ShipComponentWeaponSuggestions.ApplyBulletBalancedEnergy(ref stats);

                entry.stats = stats;
            }
        }
    }
}
