using System.Collections.Generic;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Engine and thruster move-speed and acceleration rules shared by legacy <see cref="Entities.Starship"/>,
    /// ECS motor, and editor previews. [TITAN-ORBIT] Engines/thrusters share one propulsion pool: the single
    /// best base moveSpeed plus half the sum of every other part's moveSpeedPerAbilityLevel.
    /// Acceleration caps sum across all propulsion parts. Paired with <see cref="ShipFamilyStatsCalculator"/>.
    /// </summary>
    public static class ShipPropulsionAggregation
    {
        /// <summary>
        /// [LEGACY] Old force-scale so F/m felt snappy. Flight accel is now chassis Accel after
        /// subtractive mass tax — do not multiply by this.
        /// </summary>
        [System.Obsolete("Flight uses acceleration directly after mass tax; do not scale by 10.")]
        public const float EngineThrustVisibility = 10f;

        /// <summary>Per-level terms for non-propulsion stats (~25% of base). Used when balancing weapon energy after scan.</summary>
        public const float PerLevelFractionOfBase = 0.25f;

        /// <summary>Engine/thruster moveSpeedPerAbilityLevel and accelerationCapPerAbilityLevel are this fraction of base (20%).</summary>
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
        /// Target visual bank angle (°): 0 turn rate → 0°, enough turn rate → <paramref name="maxBankDegrees"/>.
        /// <paramref name="sensitivity"/> scales how fast bank builds with yaw rate (1 = linear;
        /// &gt;1 reaches max bank sooner — feels more responsive while turning).
        /// </summary>
        /// <param name="signedAngularVelDegPerSec">Smoothed yaw rate (°/s); sign chooses bank direction.</param>
        /// <param name="maxBankDegrees">Peak roll at (or before) full turn.</param>
        /// <param name="globalMaxTurnDegPerSec">Reference max turn speed for the fleet (°/s).</param>
        /// <param name="sensitivity">
        /// Multiplier on turn fraction before clamp. Default 1 matches the old linear curve.
        /// Tuned live via <c>EcsWorldVisualizer</c> → Ship Banking.
        /// </param>
        public static float ComputeVisualBankTargetAngle(
            float signedAngularVelDegPerSec,
            float maxBankDegrees,
            float globalMaxTurnDegPerSec,
            float sensitivity = 1f)
        {
            // --- Guards ---
            // No reference turn speed, or not turning → stay flat.
            if (globalMaxTurnDegPerSec <= 0f || Mathf.Abs(signedAngularVelDegPerSec) <= 0f)
                return 0f;

            // --- Turn fraction → bank ---
            // [TITAN-ORBIT] sensitivity > 1 makes modest stick deflections lean harder without
            // raising the peak roll (maxBankDegrees still clamps the result).
            float turnRatio = Mathf.Clamp01(
                Mathf.Abs(signedAngularVelDegPerSec) / globalMaxTurnDegPerSec * Mathf.Max(0f, sensitivity));
            return Mathf.Sign(signedAngularVelDegPerSec) * turnRatio * maxBankDegrees;
        }

        /// <summary>Global max ship turn speed in °/s for visual banking (family definition units × scale).</summary>
        public static float GetGlobalMaxTurnSpeedDegreesPerSecond(
            float definitionUnitsToDegreesPerSecond = TurnDefinitionToDegreesPerSecond)
        {
            float authored = ShipFamilyDefinition.GetGlobalMaxUpgradeTreeTurnSpeedAuthoredUnits();
            return authored * definitionUnitsToDegreesPerSecond;
        }

        /// <summary>Each additional engine/thruster contributes moveSpeedPerAbilityLevel × this factor (0.5 = half).</summary>
        public const float AdditionalPropulsionMoveSpeedPerLevelFactor = 0.5f;

        /// <summary>Scan/auto-populate move speed for engine/thruster version 1 (Engine_1).</summary>
        public const float SuggestedPropulsionMoveSpeedV1 = 6f;

        /// <summary>Move speed added per version tier (v2 = 8, v3 = 10, …), before global propulsion scale.</summary>
        public const float SuggestedPropulsionMoveSpeedPerVersion = 2f;

        /// <summary>Acceleration cap as a fraction of suggested move speed for that version.</summary>
        public const float SuggestedPropulsionAccelerationFractionOfMoveSpeed = 0.5f;

        /// <summary>
        /// Fraction of v1 energy Cap/Regen added per engine version step — same ratio as moveSpeed
        /// (<see cref="SuggestedPropulsionMoveSpeedPerVersion"/> / <see cref="SuggestedPropulsionMoveSpeedV1"/> = 2/6).
        /// Engine_2 must not double Cap vs Engine_1.
        /// </summary>
        public static float EngineEnergyPerVersionFractionOfV1 =>
            SuggestedPropulsionMoveSpeedPerVersion / Mathf.Max(0.01f, SuggestedPropulsionMoveSpeedV1);

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

        /// <summary>moveSpeedPerAbilityLevel for scan/auto-populate (20% of base move speed for that version).</summary>
        public static float GetSuggestedPropulsionMoveSpeedPerLevel(int version)
        {
            return GetSuggestedPropulsionMoveSpeed(version) * PropulsionPerLevelFractionOfBase;
        }

        /// <summary>accelerationCapPerAbilityLevel for scan/auto-populate (20% of base acceleration for that version).</summary>
        public static float GetSuggestedPropulsionAccelerationCapPerLevel(int version)
        {
            return GetSuggestedPropulsionAccelerationCap(version) * PropulsionPerLevelFractionOfBase;
        }

        /// <summary>
        /// Gentle Cap/Regen share weight for engine version N — same curve as moveSpeed
        /// (v1 → 1.0, v2 → 8/6 ≈ 1.333, v3 → 10/6 ≈ 1.667). Not linear in version (that doubled Engine_2).
        /// </summary>
        public static float GetEngineEnergyVersionWeight(int version)
        {
            return GetSuggestedPropulsionMoveSpeed(version) / Mathf.Max(0.01f, SuggestedPropulsionMoveSpeedV1);
        }

        public struct Result
        {
            public float topMoveSpeed;
            public float sumAcceleration;
            /// <summary>Index into matched component lists for the part whose base moveSpeed was used once.</summary>
            public int primaryIndex;
            /// <summary>Effective extra top speed from non-primary parts (half the summed moveSpeedPerAbilityLevel).</summary>
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
        /// Per-part acceleration contribution. Uses authored <see cref="ShipComponentAbilityStats.accelerationCap"/>
        /// (summed for thrusters and engines). When cap is unset, derives from move speed using the same ratio as scan suggestions.
        /// </summary>
        public static float GetPropulsionAccelerationContribution(
            ShipComponentAbilityStats comp,
            int levelsAfterFirst)
        {
            // [TITAN-ORBIT] Ship-tier growth is applied later via family shipLevelStatGrowthFraction.
            // Do not multiply PerAbilityLevel here — those steps are bottom-HUD Move Speed only.
            _ = levelsAfterFirst;
            float authored = comp.accelerationCap;
            if (authored > 0f)
                return authored;

            if (comp.moveSpeed <= 0f)
                return 0f;

            return comp.moveSpeed * SuggestedPropulsionAccelerationFractionOfMoveSpeed;
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
                    summedExtraPerLevel += Mathf.Max(0f, perComponentStats[i].moveSpeedPerAbilityLevel);
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
                total.moveSpeedPerAbilityLevel -= s.moveSpeedPerAbilityLevel;
                total.accelerationCap -= s.accelerationCap;
                total.accelerationCapPerAbilityLevel -= s.accelerationCapPerAbilityLevel;
            }

            Result propulsion = ComputeThrusterPropulsion(componentIds, perComponentStats, shipLevel);
            total.moveSpeed = Mathf.Max(0f, total.moveSpeed) + propulsion.topMoveSpeed;
            total.accelerationCap = Mathf.Max(0f, total.accelerationCap) + propulsion.sumAcceleration;

            for (int i = 0; i < count; i++)
            {
                if (!ShipComponentAbilityStats.IsPropulsionComponent(componentIds[i]))
                    continue;
                total.accelerationCapPerAbilityLevel += perComponentStats[i].accelerationCapPerAbilityLevel;
            }

            if (propulsion.primaryIndex >= 0 && propulsion.primaryIndex < count)
            {
                total.moveSpeedPerAbilityLevel = Mathf.Max(0f, total.moveSpeedPerAbilityLevel)
                    + perComponentStats[propulsion.primaryIndex].moveSpeedPerAbilityLevel;
            }

            return total;
        }

        /// <summary>
        /// Sustained energy drain per second when firing (fireRate × damagePerBullet; damage equals fire power at runtime).
        /// </summary>
        public static float ComputeWeaponSustainedEnergyDrain(ShipComponentAbilityStats weaponStats, int firePowerUpgrades = 0)
        {
            float firePower = weaponStats.firePower + weaponStats.firePowerPerAbilityLevel * Mathf.Max(0, firePowerUpgrades);
            float fireRate = Mathf.Max(0.01f, weaponStats.fireRate + weaponStats.fireRatePerAbilityLevel * Mathf.Max(0, firePowerUpgrades));
            return firePower * fireRate;
        }

        /// <summary>
        /// [TITAN-ORBIT] Weapons hold Energy Cap (battery) but never Energy Regen — engines produce.
        /// Clears weapon regen after Scan so leftover authored regen cannot inflate hull regen.
        /// Does <b>not</b> clear Cap (use <see cref="ApplyWeaponEnergyCapSuggestionsForComponents"/> to seed).
        /// </summary>
        public static void ClearWeaponEnergyRegenForComponents(List<ShipFamilyComponentEntry> components)
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
                stats.energyRegen = 0f;
                stats.energyRegenPerAbilityLevel = 0f;
                entry.stats = stats;
            }
        }

        /// <summary>
        /// [LEGACY] Prefer <see cref="ClearWeaponEnergyRegenForComponents"/>.
        /// Still clears weapon regen only (Cap is kept as weapon battery storage).
        /// </summary>
        public static void ClearWeaponEnergyForComponents(List<ShipFamilyComponentEntry> components) =>
            ClearWeaponEnergyRegenForComponents(components);

        /// <summary>
        /// Seeds weapon <c>energyCap</c> as <c>firePower × fireRate</c> (1 sec of fire) when unset.
        /// Never writes energyRegen. Does not overwrite authored Cap &gt; 0 unless
        /// <paramref name="overwriteExisting"/> is true.
        /// </summary>
        public static void ApplyWeaponEnergyCapSuggestionsForComponents(
            List<ShipFamilyComponentEntry> components,
            bool overwriteExisting = false)
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
                if (!overwriteExisting && stats.energyCap > 0.0001f)
                {
                    // Still strip regen if a designer left it on a weapon row.
                    stats.energyRegen = 0f;
                    stats.energyRegenPerAbilityLevel = 0f;
                    entry.stats = stats;
                    continue;
                }

                ShipComponentWeaponSuggestions.ApplyWeaponBatteryCap(ref stats);
                entry.stats = stats;
            }
        }

        /// <summary>
        /// Scan / Recalculate / Rebalance: strip weapon Regen, ensure Energy category on weapons,
        /// then size Cap as firePower×fireRate (overwrite so Cap tracks Offense). Callers still run
        /// <see cref="BalanceEngineEnergyForComponents"/> for the engine power plant.
        /// </summary>
        public static void BalanceWeaponEnergyForComponents(List<ShipFamilyComponentEntry> components)
        {
            if (components == null)
                return;

            // --- Ensure Energy category so KeepOnlyAuthoringFields keeps Cap after Scan ---
            for (int i = 0; i < components.Count; i++)
            {
                ShipFamilyComponentEntry entry = components[i];
                if (entry == null || string.IsNullOrWhiteSpace(entry.componentId))
                    continue;
                if (!ShipComponentAbilityStats.IsWeaponComponent(entry.componentId))
                    continue;

                entry.EnsureStatCategories();
                if (!ShipFamilyComponentPartKey.ContainsStatCategory(
                        entry.statCategories, ShipComponentStatCategory.Energy))
                {
                    entry.statCategories.Add(ShipComponentStatCategory.Energy);
                }
            }

            ClearWeaponEnergyRegenForComponents(components);
            // overwriteExisting: Scan must keep Cap in sync with firePower after ProfileSet seeds.
            ApplyWeaponEnergyCapSuggestionsForComponents(components, overwriteExisting: true);
        }

        /// <summary>
        /// [LEGACY] Old "seconds of drain" engine budget — replaced by weapon-style shot-cap balancing.
        /// Kept so older comments/docs that cite the constant still compile if referenced.
        /// </summary>
        public const float EngineEnergyCapSecondsOfWeaponDrain = 4f;

        /// <summary>
        /// [TITAN-ORBIT] Engine regen uses the same fraction as the old weapon self-contained pool
        /// (<see cref="ShipComponentWeaponSuggestions.EnergyRegenFractionOfSustainedDrain"/> = 0.35).
        /// Holding fire still nets drain; thruster/overdrive compete for the same bar.
        /// </summary>
        public const float EngineEnergyRegenFractionOfWeaponDrain =
            ShipComponentWeaponSuggestions.EnergyRegenFractionOfSustainedDrain;

        /// <summary>
        /// Fallback Cap when a hull has engines but no weapons — one v1 bullet weapon's 1-sec pool
        /// (<c>FirePowerV1 × FireRate</c> = 3×3). Also the ProfileSet Engine baseAtVersion1 Cap.
        /// </summary>
        public const float EngineEnergyFallbackCapPerVersion = 9f;

        /// <summary>
        /// Fallback Regen when a hull has engines but no weapons — 35% of v1 bullet sustained drain (3×3).
        /// Also the ProfileSet Engine baseAtVersion1 Regen.
        /// </summary>
        public const float EngineEnergyFallbackRegenPerVersion = 3.15f;

        /// <summary>
        /// ProfileSet Engine perVersionIncrement Cap — moveSpeed-like step (2/6 of v1), not a full second plant.
        /// </summary>
        public static float EngineEnergyCapPerVersionIncrement =>
            EngineEnergyFallbackCapPerVersion * EngineEnergyPerVersionFractionOfV1;

        /// <summary>
        /// ProfileSet Engine perVersionIncrement Regen — same gentle fraction as Cap.
        /// </summary>
        public static float EngineEnergyRegenPerVersionIncrement =>
            EngineEnergyFallbackRegenPerVersion * EngineEnergyPerVersionFractionOfV1;

        /// <summary>
        /// After Scan / Populate, size Energy Cap/Regen on <b>engine-like</b> mounts from the
        /// hull's weapons: for each gun, Cap ≈ <c>firePower × fireRate</c> (1 sec of fire) and
        /// Regen ≈ 35% of that gun's sustained drain. Totals are split across engines by
        /// <b>gentle</b> version weight (moveSpeed curve: v1=1, v2≈1.33 — not v2=2).
        /// Also clears thruster Cap/Regen (thrusters do not own the power plant) and seeds
        /// OVERDRIVE ExtraSpeed knobs on engines when unset.
        /// <para>
        /// [TITAN-ORBIT] Weapon components separately author Cap-only batteries
        /// (<see cref="BalanceWeaponEnergyForComponents"/>). Hull <c>MaxEnergy</c> sums engine Cap
        /// + weapon Cap — weapons hold extra storage; only engines produce Regen.
        /// OD drain/sec = ExtraSpeedEnergyDrain on engines (absolute; not × speed %).
        /// </para>
        /// </summary>
        public static void BalanceEngineEnergyForComponents(List<ShipFamilyComponentEntry> components)
        {
            if (components == null)
                return;

            // --- Clear thruster Cap/Regen (role: maneuver, not power plant) ---
            for (int i = 0; i < components.Count; i++)
            {
                ShipFamilyComponentEntry entry = components[i];
                if (entry == null || string.IsNullOrWhiteSpace(entry.componentId))
                    continue;
                if (!ShipFamilyPartTypes.IsThrusterLikeName(entry.componentId))
                    continue;

                ShipComponentAbilityStats thrusterStats = entry.stats;
                thrusterStats.energyCap = 0f;
                thrusterStats.energyCapPerAbilityLevel = 0f;
                thrusterStats.energyRegen = 0f;
                thrusterStats.energyRegenPerAbilityLevel = 0f;
                entry.stats = thrusterStats;
            }

            // --- Seed OVERDRIVE ExtraSpeed knobs on engines when missing ---
            ApplyEngineOverdriveSuggestionsForComponents(components, overwriteExisting: false);

            // --- Sum what old weapon energy balancing would have put on each gun ---
            float totalCap = 0f;
            float totalRegen = 0f;
            float totalEngineVersionWeight = 0f;
            var engineIndices = new List<int>(4);

            for (int i = 0; i < components.Count; i++)
            {
                ShipFamilyComponentEntry entry = components[i];
                if (entry == null || string.IsNullOrWhiteSpace(entry.componentId))
                    continue;

                if (ShipComponentAbilityStats.IsWeaponComponent(entry.componentId))
                {
                    // Mirror ApplyWeaponBatteryCap / ApplyBalancedEnergy regen without writing the gun.
                    float firePower = Mathf.Max(0f, entry.stats.firePower);
                    if (firePower <= 0f)
                        continue;

                    float fireRate = Mathf.Max(0.01f, entry.stats.fireRate);
                    float sustained = ShipComponentWeaponSuggestions.ComputeSustainedEnergyDrain(firePower, fireRate);
                    totalCap += sustained; // 1 sec of fire — same as weapon Cap default
                    totalRegen += sustained * ShipComponentWeaponSuggestions.EnergyRegenFractionOfSustainedDrain;
                    continue;
                }

                if (!ShipFamilyPartTypes.IsEngineLikeName(entry.componentId))
                    continue;

                engineIndices.Add(i);
                int version = Mathf.Max(1, ShipFamilyPartCalcProfileSet.ExtractVersion(entry.componentId));
                totalEngineVersionWeight += GetEngineEnergyVersionWeight(version);
            }

            if (engineIndices.Count == 0)
            {
                // [TITAN-ORBIT] Thruster-only hulls (e.g. SpaceExcalibur): thrusters carry the
                // power plant when no Engine_* mounts exist, otherwise MaxEnergy stays 0.
                for (int i = 0; i < components.Count; i++)
                {
                    ShipFamilyComponentEntry entry = components[i];
                    if (entry == null || string.IsNullOrWhiteSpace(entry.componentId))
                        continue;
                    if (!ShipFamilyPartTypes.IsThrusterLikeName(entry.componentId))
                        continue;
                    if (ShipFamilyPartCalcProfileSet.IsCosmeticPartName(entry.componentId))
                        continue;

                    engineIndices.Add(i);
                    int version = Mathf.Max(1, ShipFamilyPartCalcProfileSet.ExtractVersion(entry.componentId));
                    totalEngineVersionWeight += GetEngineEnergyVersionWeight(version);
                }

                if (engineIndices.Count == 0)
                    return;
            }

            if (totalEngineVersionWeight <= 0.0001f)
                totalEngineVersionWeight = engineIndices.Count;

            // --- No weapons: one bullet-weapon-sized plant × gentle version weight per engine ---
            if (totalCap <= 0.0001f)
            {
                totalCap = 0f;
                totalRegen = 0f;
                for (int e = 0; e < engineIndices.Count; e++)
                {
                    int version = Mathf.Max(1, ShipFamilyPartCalcProfileSet.ExtractVersion(
                        components[engineIndices[e]].componentId));
                    float weight = GetEngineEnergyVersionWeight(version);
                    totalCap += EngineEnergyFallbackCapPerVersion * weight;
                    totalRegen += EngineEnergyFallbackRegenPerVersion * weight;
                }
            }

            // --- Split by gentle version weight (v1:v2 ≈ 1:1.33, not 1:2) ---
            for (int e = 0; e < engineIndices.Count; e++)
            {
                ShipFamilyComponentEntry entry = components[engineIndices[e]];
                int version = Mathf.Max(1, ShipFamilyPartCalcProfileSet.ExtractVersion(entry.componentId));
                float share = GetEngineEnergyVersionWeight(version) / totalEngineVersionWeight;

                ShipComponentAbilityStats stats = entry.stats;
                stats.energyCap = Mathf.Max(1f, totalCap * share);
                stats.energyRegen = Mathf.Max(0.1f, totalRegen * share);
                stats.energyCapPerAbilityLevel = stats.energyCap * PerLevelFractionOfBase;
                stats.energyRegenPerAbilityLevel = stats.energyRegen * PerLevelFractionOfBase;
                // Engines do not author turn — clear leftover turn from older scans.
                // Thruster-only fallback keeps turn (ApplyThrusterTurn already wrote it).
                if (ShipFamilyPartTypes.IsEngineLikeName(entry.componentId))
                {
                    stats.turnSpeed = 0f;
                    stats.turnSpeedPerAbilityLevel = 0f;
                }

                entry.stats = stats;

                // Ensure Energy category so EnforceComponentStatCategories keeps Cap/Regen
                // (normal engines already have it; thruster-only fallback needs it added).
                entry.EnsureStatCategories();
                if (entry.statCategories == null)
                    entry.statCategories = new List<ShipComponentStatCategory>();
                bool hasEnergy = false;
                for (int c = 0; c < entry.statCategories.Count; c++)
                {
                    if (entry.statCategories[c] == ShipComponentStatCategory.Energy)
                    {
                        hasEnergy = true;
                        break;
                    }
                }

                if (!hasEnergy)
                    entry.statCategories.Add(ShipComponentStatCategory.Energy);
            }
        }

        /// <summary>
        /// [TITAN-ORBIT] Thruster-like mounts author Fin-scale turn (Tail/Fin still add their own turn).
        /// Called after Scan/Recalculate because the Thruster profile may still need Fin-scale turn
        /// when an older Scan used the shared Engine/Thrust row (turnSpeed = 0).
        /// Skips cosmetic Place/Cover/Plate/Holder mounts.
        /// </summary>
        public static void ApplyThrusterTurnSuggestionsForComponents(List<ShipFamilyComponentEntry> components)
        {
            if (components == null)
                return;

            for (int i = 0; i < components.Count; i++)
            {
                ShipFamilyComponentEntry entry = components[i];
                if (entry == null || string.IsNullOrWhiteSpace(entry.componentId))
                    continue;
                if (!ShipFamilyPartTypes.IsThrusterLikeName(entry.componentId))
                    continue;
                if (ShipFamilyPartCalcProfileSet.IsCosmeticPartName(entry.componentId))
                    continue;

                int version = Mathf.Max(1, ShipFamilyPartCalcProfileSet.ExtractVersion(entry.componentId));
                ShipComponentAbilityStats stats = entry.stats;
                stats.turnSpeed = ShipComponentTurnSpeedSuggestions.GetSuggestedFinTurnSpeed(version);
                stats.turnSpeedPerAbilityLevel = ShipComponentTurnSpeedSuggestions.GetSuggestedTurnSpeedPerLevel(stats.turnSpeed);
                entry.stats = stats;
            }
        }

        /// <summary>
        /// Seeds OVERDRIVE <c>extraSpeedPercent</c> / <c>extraSpeedEnergyDrain</c> on engine-like
        /// mounts when unset (project defaults). Per-level stays 0 unless already authored.
        /// Thrusters never get these fields.
        /// </summary>
        public static void ApplyEngineOverdriveSuggestionsForComponents(
            List<ShipFamilyComponentEntry> components,
            bool overwriteExisting = false)
        {
            if (components == null)
                return;

            for (int i = 0; i < components.Count; i++)
            {
                ShipFamilyComponentEntry entry = components[i];
                if (entry == null || string.IsNullOrWhiteSpace(entry.componentId))
                    continue;
                if (!ShipFamilyPartTypes.IsEngineLikeName(entry.componentId))
                    continue;
                if (ShipFamilyPartCalcProfileSet.IsCosmeticPartName(entry.componentId))
                    continue;

                ShipComponentAbilityStats stats = entry.stats;
                // Seed missing speed and/or drain independently so one authored field does not skip the other.
                bool needsSpeed = overwriteExisting || stats.extraSpeedPercent <= 0.0001f;
                bool needsDrain = overwriteExisting || stats.extraSpeedEnergyDrain <= 0.0001f;
                bool needsDrainPerAbility = overwriteExisting || stats.extraSpeedEnergyDrainPerAbilityLevel <= 0.0001f;

                if (needsSpeed)
                    stats.extraSpeedPercent = ShipFamilyOverdriveAbility.DefaultExtraSpeedPercent;
                if (needsDrain)
                    stats.extraSpeedEnergyDrain = ShipFamilyOverdriveAbility.DefaultExtraSpeedEnergyDrain;
                // [TITAN-ORBIT] ExtraSpeedPercent ability step stays 0 unless designers opt in.
                // ExtraSpeedEnergyDrain PerAbilityLevel matches moveSpeed's fraction (Move Speed HUD).
                if (overwriteExisting || stats.extraSpeedPercentPerAbilityLevel < 0f)
                    stats.extraSpeedPercentPerAbilityLevel = 0f;
                if (needsDrainPerAbility)
                {
                    float drainBase = stats.extraSpeedEnergyDrain > 0.0001f
                        ? stats.extraSpeedEnergyDrain
                        : ShipFamilyOverdriveAbility.DefaultExtraSpeedEnergyDrain;
                    stats.extraSpeedEnergyDrainPerAbilityLevel =
                        drainBase * PropulsionPerLevelFractionOfBase;
                }
                entry.stats = stats;
            }
        }

        /// <summary>
        /// Resolves OVERDRIVE speed/thrust multipliers and absolute energy drain/sec from <b>engine</b>
        /// component rows at ship level, then × family Special Bonuses.
        /// <list type="bullet">
        /// <item>Speed/thrust mul = 1 + max(ExtraSpeedPercent × ship-tier growth) across engines</item>
        /// <item>Drain/sec = sum of ExtraSpeedEnergyDrain × ship-tier growth (family fraction, default 10%)</item>
        /// </list>
        /// Falls back to code defaults when no engine authors OVERDRIVE fields.
        /// </summary>
        public static void ResolveOverdriveFromEngines(
            ShipFamilyDefinition family,
            int shipLevel,
            in ShipFamilySpecialBonuses bonuses,
            out float speedMultiplier,
            out float thrustMultiplier,
            out float energyDrainPerSecond)
        {
            float maxEsp = 0f;
            float totalDrain = 0f;
            bool anyEngineOd = false;

            float speedFamilyMul = bonuses.extraSpeedPercentMul > 0.0001f ? bonuses.extraSpeedPercentMul : 1f;
            float energyFamilyMul = bonuses.extraSpeedEnergyDrainMul > 0.0001f
                ? bonuses.extraSpeedEnergyDrainMul
                : 1f;

            if (family?.components != null)
            {
                int levelsAfterFirst = Mathf.Max(0, shipLevel - 1);
                float growth = family.ResolveShipLevelStatGrowthFraction();
                float tierMul = 1f + levelsAfterFirst * growth;
                for (int i = 0; i < family.components.Count; i++)
                {
                    ShipFamilyComponentEntry entry = family.components[i];
                    if (entry == null || string.IsNullOrWhiteSpace(entry.componentId))
                        continue;
                    if (!ShipFamilyPartTypes.IsEngineLikeName(entry.componentId))
                        continue;
                    if (ShipFamilyPartCalcProfileSet.IsCosmeticPartName(entry.componentId))
                        continue;

                    ShipComponentAbilityStats stats = entry.stats;
                    // [TITAN-ORBIT] Ship-tier growth uses family fraction — not *PerAbilityLevel.
                    float esp = stats.extraSpeedPercent * tierMul;
                    float drain = stats.extraSpeedEnergyDrain * tierMul;
                    if (esp <= 0.0001f && drain <= 0.0001f)
                        continue;

                    anyEngineOd = true;
                    if (esp > maxEsp) maxEsp = esp;

                    // [TITAN-ORBIT] Absolute OD drain from this engine — use ExtraSpeedEnergyDrain as-is.
                    float engineDrain = drain > 0.0001f
                        ? drain
                        : ShipFamilyOverdriveAbility.DefaultExtraSpeedEnergyDrain;
                    totalDrain += Mathf.Max(0f, engineDrain * energyFamilyMul);
                }
            }

            if (!anyEngineOd)
            {
                ShipFamilyOverdriveAbility ability = ShipFamilyOverdriveAbility.Default.Resolved();
                bonuses.ResolveOverdrive(
                    ability, out speedMultiplier, out thrustMultiplier, out energyDrainPerSecond);
                return;
            }

            if (maxEsp <= 0.0001f)
                maxEsp = ShipFamilyOverdriveAbility.DefaultExtraSpeedPercent;

            float maxSpeedFraction = Mathf.Max(0f, maxEsp * speedFamilyMul);
            speedMultiplier = 1f + maxSpeedFraction;
            thrustMultiplier = speedMultiplier;
            energyDrainPerSecond = Mathf.Max(0f, totalDrain);
        }

        /// <summary>[LEGACY name] Prefer <see cref="ResolveOverdriveFromEngines"/> — third out is absolute drain/sec.</summary>
        public static void ResolveOverdriveMultipliersFromEngines(
            ShipFamilyDefinition family,
            int shipLevel,
            in ShipFamilySpecialBonuses bonuses,
            out float speedMultiplier,
            out float thrustMultiplier,
            out float energyDrainPerSecond)
        {
            ResolveOverdriveFromEngines(
                family,
                shipLevel,
                bonuses,
                out speedMultiplier,
                out thrustMultiplier,
                out energyDrainPerSecond);
        }
    }
}
