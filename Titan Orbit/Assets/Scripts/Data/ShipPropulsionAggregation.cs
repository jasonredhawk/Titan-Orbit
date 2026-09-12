using System.Collections.Generic;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Engine and thruster move-speed and acceleration rules shared by legacy <see cref="Entities.Starship"/>,
    /// ECS motor, and editor previews.
    /// <para>
    /// [TITAN-ORBIT] Move Speed is engines only. Acceleration is thrusters only.
    /// Each part still uses <b>its own</b> Base and PerExtraLevel (an AstroEagle engine
    /// PerExtra is not reused on a CosmicShark thruster). Extra Level evaluates each
    /// contributor then sums. Newest moon-store extra in that role is the display primary.
    /// Thruster-only hulls (SpaceExcalibur) fall back: thrusters own Move. Engine-only
    /// hulls fall back: engines own Accel.
    /// </para>
    /// Paired with <see cref="ShipFamilyStatsCalculator"/>.
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

        /// <summary>Engine/thruster moveSpeedPerExtraLevel and accelerationCapPerExtraLevel are this fraction of base (20%).</summary>
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
        /// <see cref="ShipMobilityResolution"/> scales turn mass tax by the same factor so cargo
        /// bite stays in ratio with Speed/Accel (those stats are never ×10).
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
        /// Tuned on <see cref="ShipBankVisualSettings"/> (family asset, MEGA catalog asset, or Resources default).
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

        /// <summary>Nose-down Euler X (°) at the reference forward accel when sensitivity is 1.</summary>
        public const float VisualPitchDefaultMaxDownDegrees = 18f;

        /// <summary>Nose-up Euler X magnitude (°) at the reference forward accel when sensitivity is 1.</summary>
        public const float VisualPitchDefaultMaxUpDegrees = 12f;

        /// <summary>Forward accel (world u/s²) treated as “full” for the cruise pitch curve.</summary>
        public const float VisualPitchReferenceAccel = 6f;

        /// <summary>
        /// Target visual pitch (°): +X is nose down. Speed-up pitches the nose up;
        /// braking / slamming into a body pitches it down. Clamped to
        /// <paramref name="maxPitchDownDegrees"/> / <paramref name="maxPitchUpDegrees"/>.
        /// </summary>
        /// <param name="signedForwardAccel">Smoothed forward accel (u/s²); + = speeding up along heading.</param>
        /// <param name="maxPitchDownDegrees">Peak nose-down (°).</param>
        /// <param name="maxPitchUpDegrees">Peak nose-up (°).</param>
        /// <param name="referenceAccel">Accel that reaches peak pitch when sensitivity is 1.</param>
        /// <param name="sensitivity">Multiplier on accel fraction before clamp.</param>
        public static float ComputeVisualPitchTargetAngle(
            float signedForwardAccel,
            float maxPitchDownDegrees,
            float maxPitchUpDegrees,
            float referenceAccel,
            float sensitivity = 1f)
        {
            if (referenceAccel <= 0.01f || Mathf.Abs(signedForwardAccel) <= 0f)
                return 0f;

            float ratio = Mathf.Clamp01(
                Mathf.Abs(signedForwardAccel) / referenceAccel * Mathf.Max(0f, sensitivity));
            // Speed-up → nose up (negative Euler X). Brake / hit → nose down (positive Euler X).
            if (signedForwardAccel >= 0f)
                return -ratio * Mathf.Max(0f, maxPitchUpDegrees);
            return ratio * Mathf.Max(0f, maxPitchDownDegrees);
        }

        /// <summary>
        /// Sudden planar-speed change → signed Euler X impulse. Speed loss pitches
        /// the nose down; a shove that speeds the hull up pitches it up. Below the threshold, 0.
        /// </summary>
        /// <param name="forwardSpeedDelta">This-frame change in heading-aligned speed (u/s).</param>
        /// <param name="impactDeltaSpeedThreshold">|Δv| that counts as a hit, not cruise thrust.</param>
        /// <param name="degreesPerSpeed">Degrees of pitch per u/s of sudden Δv.</param>
        /// <param name="maxPitchDownDegrees">Nose-down clamp (°).</param>
        /// <param name="maxPitchUpDegrees">Nose-up clamp (°).</param>
        public static float ComputeVisualPitchImpactDelta(
            float forwardSpeedDelta,
            float impactDeltaSpeedThreshold,
            float degreesPerSpeed,
            float maxPitchDownDegrees,
            float maxPitchUpDegrees)
        {
            if (Mathf.Abs(forwardSpeedDelta) < Mathf.Max(0.01f, impactDeltaSpeedThreshold))
                return 0f;

            float impulse = -forwardSpeedDelta * Mathf.Max(0f, degreesPerSpeed);
            return ClampVisualPitchDegrees(impulse, maxPitchDownDegrees, maxPitchUpDegrees);
        }

        /// <summary>Clamps Euler X pitch to [−maxUp, +maxDown].</summary>
        public static float ClampVisualPitchDegrees(
            float pitchDeg,
            float maxPitchDownDegrees,
            float maxPitchUpDegrees)
        {
            return Mathf.Clamp(pitchDeg, -Mathf.Max(0f, maxPitchUpDegrees), Mathf.Max(0f, maxPitchDownDegrees));
        }

        /// <summary>
        /// Client cosmetic pitch step shared by hybrid proxies and Entities Graphics.
        /// Uses planar speed magnitude so turning at constant speed does not fake a slam.
        /// Cruise accel is smoothed; collision-sized Δv punches a decaying impact term.
        /// Combined result is clamped to the authored min/max (down / up).
        /// </summary>
        public static float StepVisualPitch(
            float planarSpeed,
            float dt,
            float maxPitchDownDegrees,
            float maxPitchUpDegrees,
            float referenceAccel,
            float sensitivity,
            float smoothing,
            float impactDeltaSpeed,
            float impactDegreesPerSpeed,
            float impactDecay,
            ref float prevForwardSpeed,
            ref bool speedInitialized,
            ref float smoothedAccel,
            ref float accelPitchDeg,
            ref float impactPitchDeg)
        {
            dt = Mathf.Max(1e-5f, dt);
            if (!speedInitialized)
            {
                prevForwardSpeed = planarSpeed;
                speedInitialized = true;
                smoothedAccel = 0f;
                accelPitchDeg = 0f;
                impactPitchDeg = 0f;
                return 0f;
            }

            float speedDelta = planarSpeed - prevForwardSpeed;
            prevForwardSpeed = planarSpeed;
            float instantAccel = speedDelta / dt;
            // Kill interpolation / rest jitter so cruise pitch does not fidget at idle.
            if (Mathf.Abs(instantAccel) < 0.35f)
                instantAccel = 0f;

            float impact = ComputeVisualPitchImpactDelta(
                speedDelta,
                impactDeltaSpeed,
                impactDegreesPerSpeed,
                maxPitchDownDegrees,
                maxPitchUpDegrees);
            if (impact != 0f)
            {
                impactPitchDeg += impact;
                impactPitchDeg = ClampVisualPitchDegrees(
                    impactPitchDeg, maxPitchDownDegrees, maxPitchUpDegrees);
                smoothedAccel = 0f;
            }
            else
            {
                float accelT = 1f - Mathf.Exp(-Mathf.Max(0.01f, smoothing) * dt);
                smoothedAccel = Mathf.Lerp(smoothedAccel, instantAccel, accelT);
            }

            float decayT = 1f - Mathf.Exp(-Mathf.Max(0.01f, impactDecay) * dt);
            impactPitchDeg = Mathf.Lerp(impactPitchDeg, 0f, decayT);

            float targetAccelPitch = ComputeVisualPitchTargetAngle(
                smoothedAccel,
                maxPitchDownDegrees,
                maxPitchUpDegrees,
                referenceAccel,
                sensitivity);
            float pitchT = 1f - Mathf.Exp(-Mathf.Max(0.01f, smoothing) * dt);
            accelPitchDeg = Mathf.Lerp(accelPitchDeg, targetAccelPitch, pitchT);

            return ClampVisualPitchDegrees(
                accelPitchDeg + impactPitchDeg,
                maxPitchDownDegrees,
                maxPitchUpDegrees);
        }

        /// <summary>Global max ship turn speed in °/s for visual banking (family definition units × scale).</summary>
        public static float GetGlobalMaxTurnSpeedDegreesPerSecond(
            float definitionUnitsToDegreesPerSecond = TurnDefinitionToDegreesPerSecond)
        {
            float authored = ShipFamilyDefinition.GetGlobalMaxUpgradeTreeTurnSpeedAuthoredUnits();
            return authored * definitionUnitsToDegreesPerSecond;
        }

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

        /// <summary>moveSpeedPerExtraLevel for scan/auto-populate (20% of base move speed for that version).</summary>
        public static float GetSuggestedPropulsionMoveSpeedPerLevel(int version)
        {
            return GetSuggestedPropulsionMoveSpeed(version) * PropulsionPerLevelFractionOfBase;
        }

        /// <summary>accelerationCapPerExtraLevel for scan/auto-populate (20% of base acceleration for that version).</summary>
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
            /// <summary>
            /// Extra Level top speed from <b>engines</b> (thrusters only when the hull has none):
            /// primary Move Base + each contributing part’s PerExtra × shipLevel, then optional
            /// level mobility drag.
            /// </summary>
            public float topMoveSpeed;

            /// <summary>
            /// Extra Level accel from <b>thrusters</b> (engines only when the hull has none):
            /// primary Accel Base + each contributing part’s PerExtra × shipLevel.
            /// </summary>
            public float sumAcceleration;

            /// <summary>Same as <see cref="movePrimaryIndex"/> — kept for older HUD / preview binders.</summary>
            public int primaryIndex;

            /// <summary>Engine (or fallback thruster) that owns Move Base.</summary>
            public int movePrimaryIndex;

            /// <summary>Thruster (or fallback engine) that owns Accel Base.</summary>
            public int accelPrimaryIndex;

            /// <summary>How many propulsion parts participated in Move or Accel (0 if none).</summary>
            public int propulsionCount;

            /// <summary>How many parts contributed to Move (engines, or fallback thrusters).</summary>
            public int moveCount;

            /// <summary>How many parts contributed to Accel (thrusters, or fallback engines).</summary>
            public int accelCount;

            /// <summary>
            /// Move contributed by non-primary propulsion parts (their own Base + PerExtra at this ship level).
            /// </summary>
            public float extraMoveSpeedFromAdditional;

            /// <summary>
            /// [LEGACY] Same as <see cref="extraMoveSpeedFromAdditional"/> — kept so previews that
            /// still bind the old field name keep compiling.
            /// </summary>
            public float extraMoveSpeedFromPerLevel
            {
                get => extraMoveSpeedFromAdditional;
                set => extraMoveSpeedFromAdditional = value;
            }

            /// <summary>Primary Move PerExtraLevel step (one step per Extra Level / ability buy).</summary>
            public float moveSpeedPerExtraLevel;

            /// <summary>Primary Accel PerExtraLevel step (one step per Extra Level / ability buy).</summary>
            public float accelerationCapPerExtraLevel;
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
        /// Per-part acceleration contribution (authored Accel, or derived from Move when unset).
        /// Extra Level aggregation uses this only on the primary part.
        /// </summary>
        public static float GetPropulsionAccelerationContribution(
            ShipComponentAbilityStats comp,
            int levelsAfterFirst)
        {
            // [TITAN-ORBIT] Extra Level / mobility drag are applied by the caller — not here.
            _ = levelsAfterFirst;
            float authored = comp.accelerationCap;
            if (authored > 0f)
                return authored;

            if (comp.moveSpeed <= 0f)
                return 0f;

            return comp.moveSpeed * SuggestedPropulsionAccelerationFractionOfMoveSpeed;
        }

        /// <summary>
        /// True when this hull has at least one non-cosmetic engine (power plant) mount.
        /// Used with <see cref="HasThrusters"/> so Move / Accel can fall back on single-role hulls.
        /// </summary>
        /// <param name="componentIds">Prefab + store part ids (cosmetics are skipped).</param>
        public static void ClassifyPropulsionRoles(
            IReadOnlyList<string> componentIds,
            out bool hasEngines,
            out bool hasThrusters)
        {
            hasEngines = false;
            hasThrusters = false;
            if (componentIds == null)
                return;

            for (int i = 0; i < componentIds.Count; i++)
            {
                string id = componentIds[i];
                if (string.IsNullOrWhiteSpace(id))
                    continue;
                if (ShipFamilyPartCalcProfileSet.IsCosmeticPartName(id))
                    continue;

                if (ShipFamilyPartTypes.IsEngineLikeName(id))
                    hasEngines = true;
                else if (ShipFamilyPartTypes.IsThrusterLikeName(id))
                    hasThrusters = true;

                if (hasEngines && hasThrusters)
                    return;
            }
        }

        /// <summary>
        /// Move Speed contributors: engines, or thrusters when the hull has no engines.
        /// Cosmetic Place / Cover mounts never contribute.
        /// </summary>
        /// <param name="componentId">Prefab child or store catalog id.</param>
        /// <param name="hasEngines">From <see cref="ClassifyPropulsionRoles"/>.</param>
        public static bool ContributesMoveSpeed(string componentId, bool hasEngines)
        {
            if (string.IsNullOrWhiteSpace(componentId))
                return false;
            if (ShipFamilyPartCalcProfileSet.IsCosmeticPartName(componentId))
                return false;
            if (!ShipComponentAbilityStats.IsPropulsionComponent(componentId))
                return false;

            // [TITAN-ORBIT] Engines own cruise. Thrusters only inherit Move on thruster-only hulls.
            if (ShipFamilyPartTypes.IsEngineLikeName(componentId))
                return true;
            return !hasEngines && ShipFamilyPartTypes.IsThrusterLikeName(componentId);
        }

        /// <summary>
        /// Acceleration contributors: thrusters, or engines when the hull has no thrusters.
        /// </summary>
        /// <param name="componentId">Prefab child or store catalog id.</param>
        /// <param name="hasThrusters">From <see cref="ClassifyPropulsionRoles"/>.</param>
        public static bool ContributesAcceleration(string componentId, bool hasThrusters)
        {
            if (string.IsNullOrWhiteSpace(componentId))
                return false;
            if (ShipFamilyPartCalcProfileSet.IsCosmeticPartName(componentId))
                return false;
            if (!ShipComponentAbilityStats.IsPropulsionComponent(componentId))
                return false;

            // [TITAN-ORBIT] Thrusters own thrust. Engines only inherit Accel on engine-only hulls.
            if (ShipFamilyPartTypes.IsThrusterLikeName(componentId))
                return true;
            return !hasThrusters && ShipFamilyPartTypes.IsEngineLikeName(componentId);
        }

        /// <summary>
        /// Zeros Move or Accel on a part that does not own that role for this hull.
        /// Energy / turn / OVERDRIVE stay as authored — those already have their own owners.
        /// </summary>
        /// <param name="componentId">Part being masked.</param>
        /// <param name="stats">Extra-Leveled or primary snapshot for that part.</param>
        /// <param name="hasEngines">Hull has at least one engine.</param>
        /// <param name="hasThrusters">Hull has at least one thruster.</param>
        public static ShipComponentAbilityStats MaskAbilityStatsForRole(
            string componentId,
            in ShipComponentAbilityStats stats,
            bool hasEngines,
            bool hasThrusters)
        {
            var masked = stats;
            if (!ContributesMoveSpeed(componentId, hasEngines))
            {
                masked.moveSpeed = 0f;
                masked.moveSpeedPerExtraLevel = 0f;
            }

            if (!ContributesAcceleration(componentId, hasThrusters))
            {
                masked.accelerationCap = 0f;
                masked.accelerationCapPerExtraLevel = 0f;
            }

            return masked;
        }

        /// <summary>
        /// Newest store extra (else highest role score) among parts that own Move or Accel.
        /// </summary>
        static int PickRolePrimaryGlobalIndex(
            IReadOnlyList<string> componentIds,
            IReadOnlyList<ShipComponentAbilityStats> perComponentStats,
            int storeExtraStartIndex,
            bool hasEngines,
            bool hasThrusters,
            bool forMove)
        {
            if (componentIds == null || perComponentStats == null)
                return -1;

            int count = Mathf.Min(componentIds.Count, perComponentStats.Count);
            var members = new List<int>(4);
            for (int i = 0; i < count; i++)
            {
                string id = componentIds[i];
                bool include = forMove
                    ? ContributesMoveSpeed(id, hasEngines)
                    : ContributesAcceleration(id, hasThrusters);
                if (!include)
                    continue;
                members.Add(i);
            }

            if (members.Count == 0)
                return -1;

            // Score Move primaries by cruise; Accel primaries by thrust.
            string poolKey = forMove
                ? ShipComponentStackAggregation.EnginePoolKey
                : ShipComponentStackAggregation.ThrusterPoolKey;
            int local = ShipComponentStackAggregation.PickPrimaryLocalIndex(
                poolKey, members, perComponentStats, storeExtraStartIndex);
            return members[local];
        }

        /// <summary>
        /// Computes Move from engines and Accel from thrusters using Extra Level.
        /// Each contributing part uses <b>its own</b> PerExtra; results are summed.
        /// Ability purchases are 0 here — HUD/sim pass them via
        /// <see cref="ShipComponentExtraLevelMath.AggregateAndEvaluate"/>.
        /// </summary>
        /// <param name="storeExtraStartIndex">First moon-store extra index (newest extra in that role becomes primary).</param>
        public static Result ComputeThrusterPropulsion(
            IReadOnlyList<string> componentIds,
            IReadOnlyList<ShipComponentAbilityStats> perComponentStats,
            int shipLevel,
            int storeExtraStartIndex = int.MaxValue)
        {
            var result = new Result
            {
                primaryIndex = -1,
                movePrimaryIndex = -1,
                accelPrimaryIndex = -1,
            };
            if (componentIds == null || perComponentStats == null)
                return result;

            int count = Mathf.Min(componentIds.Count, perComponentStats.Count);
            if (count == 0)
                return result;

            int levelsAfterFirst = Mathf.Max(0, shipLevel - 1);
            ShipCargoMobilitySettings mobility = ShipCargoMobilitySettingsCache.ResolveOrDefault();
            float speedPenalty = mobility != null ? mobility.levelMaxSpeedPenaltyFractionPerLevel : 0f;
            float accelPenalty = mobility != null ? mobility.levelAccelPenaltyFractionPerLevel : 0f;

            // --- Who owns each role on this hull (plus SpaceExcalibur-style fallbacks) ---
            ClassifyPropulsionRoles(componentIds, out bool hasEngines, out bool hasThrusters);
            result.movePrimaryIndex = PickRolePrimaryGlobalIndex(
                componentIds, perComponentStats, storeExtraStartIndex, hasEngines, hasThrusters, forMove: true);
            result.accelPrimaryIndex = PickRolePrimaryGlobalIndex(
                componentIds, perComponentStats, storeExtraStartIndex, hasEngines, hasThrusters, forMove: false);
            result.primaryIndex = result.movePrimaryIndex;
            if (result.movePrimaryIndex < 0 && result.accelPrimaryIndex < 0)
                return result;

            // --- Extra Level each owner with that part’s PerExtra, then sum ---
            int propulsionCount = 0;
            int moveCount = 0;
            int accelCount = 0;
            float moveRaw = 0f;
            float accelRaw = 0f;
            float movePerSum = 0f;
            float accelPerSum = 0f;
            float extraMove = 0f;
            for (int i = 0; i < count; i++)
            {
                string id = componentIds[i];
                bool forMove = ContributesMoveSpeed(id, hasEngines);
                bool forAccel = ContributesAcceleration(id, hasThrusters);
                if (!forMove && !forAccel)
                    continue;

                ShipComponentAbilityStats part = perComponentStats[i];
                float movePer = Mathf.Max(0f, part.moveSpeedPerExtraLevel);
                float accelPer = Mathf.Max(0f, part.accelerationCapPerExtraLevel);
                if (accelPer <= 0.0001f && movePer > 0f)
                    accelPer = movePer * SuggestedPropulsionAccelerationFractionOfMoveSpeed;

                if (forMove)
                {
                    bool includeMoveBase = i == result.movePrimaryIndex;
                    float partMove = ShipComponentExtraLevelMath.Evaluate(
                        Mathf.Max(0f, part.moveSpeed),
                        movePer,
                        shipLevel,
                        abilityLevel: 0,
                        componentCount: 1,
                        includeExtraComponentLevels: true,
                        includeBase: includeMoveBase);
                    moveRaw += partMove;
                    movePerSum += movePer;
                    if (!includeMoveBase)
                        extraMove += partMove;
                    moveCount++;
                }

                if (forAccel)
                {
                    bool includeAccelBase = i == result.accelPrimaryIndex;
                    float partAccel = ShipComponentExtraLevelMath.Evaluate(
                        Mathf.Max(0f, GetPropulsionAccelerationContribution(part, 0)),
                        accelPer,
                        shipLevel,
                        abilityLevel: 0,
                        componentCount: 1,
                        includeExtraComponentLevels: true,
                        includeBase: includeAccelBase);
                    accelRaw += partAccel;
                    accelPerSum += accelPer;
                    accelCount++;
                }

                propulsionCount++;
            }

            if (propulsionCount <= 0)
                return result;

            float topMove = ApplyShipLevelMobilityScale(moveRaw, levelsAfterFirst, speedPenalty);
            float sumAccel = ApplyShipLevelMobilityScale(accelRaw, levelsAfterFirst, accelPenalty);

            result.propulsionCount = propulsionCount;
            result.moveCount = moveCount;
            result.accelCount = accelCount;
            result.topMoveSpeed = Mathf.Max(0.1f, topMove);
            result.sumAcceleration = Mathf.Max(0f, sumAccel);
            result.extraMoveSpeedFromAdditional = Mathf.Max(0f, extraMove);
            result.moveSpeedPerExtraLevel = movePerSum;
            result.accelerationCapPerExtraLevel = accelPerSum;
            return result;
        }

        /// <summary>
        /// Rebuilds hull totals with <see cref="ShipComponentStackAggregation.AggregateAllPools"/>
        /// (primary-per-pool only; extras contribute later via Extra Level count).
        /// Prefer calling that directly; kept for upgrade-tree / legacy callers that passed a naive sum.
        /// </summary>
        public static ShipComponentAbilityStats ApplyPropulsionToSummedStats(
            ShipComponentAbilityStats total,
            IReadOnlyList<string> componentIds,
            IReadOnlyList<ShipComponentAbilityStats> perComponentStats,
            int shipLevel = 1)
        {
            _ = total;
            _ = shipLevel;
            return ShipComponentStackAggregation.AggregateAllPools(componentIds, perComponentStats);
        }

        /// <summary>
        /// Sustained energy drain per second when firing (fireRate × damagePerBullet; damage equals fire power at runtime).
        /// </summary>
        public static float ComputeWeaponSustainedEnergyDrain(ShipComponentAbilityStats weaponStats, int firePowerUpgrades = 0)
        {
            float firePower = weaponStats.firePower + weaponStats.firePowerPerExtraLevel * Mathf.Max(0, firePowerUpgrades);
            float fireRate = Mathf.Max(0.01f, weaponStats.fireRate + weaponStats.fireRatePerExtraLevel * Mathf.Max(0, firePowerUpgrades));
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
                stats.energyRegenPerExtraLevel = 0f;
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
                    stats.energyRegenPerExtraLevel = 0f;
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
                thrusterStats.energyCapPerExtraLevel = 0f;
                thrusterStats.energyRegen = 0f;
                thrusterStats.energyRegenPerExtraLevel = 0f;
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
                stats.energyCapPerExtraLevel = stats.energyCap * PerLevelFractionOfBase;
                stats.energyRegenPerExtraLevel = stats.energyRegen * PerLevelFractionOfBase;
                // Engines do not author turn — clear leftover turn from older scans.
                // Thruster-only fallback keeps turn (ApplyThrusterTurn already wrote it).
                if (ShipFamilyPartTypes.IsEngineLikeName(entry.componentId))
                {
                    stats.turnSpeed = 0f;
                    stats.turnSpeedPerExtraLevel = 0f;
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
                stats.turnSpeedPerExtraLevel = ShipComponentTurnSpeedSuggestions.GetSuggestedTurnSpeedPerLevel(stats.turnSpeed);
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
                bool needsDrainPerAbility = overwriteExisting || stats.extraSpeedEnergyDrainPerExtraLevel <= 0.0001f;

                if (needsSpeed)
                    stats.extraSpeedPercent = ShipFamilyOverdriveAbility.DefaultExtraSpeedPercent;
                if (needsDrain)
                    stats.extraSpeedEnergyDrain = ShipFamilyOverdriveAbility.DefaultExtraSpeedEnergyDrain;
                // [TITAN-ORBIT] ExtraSpeedPercent ability step stays 0 unless designers opt in.
                // ExtraSpeedEnergyDrain PerExtraLevel matches moveSpeed's fraction (Move Speed HUD).
                if (overwriteExisting || stats.extraSpeedPercentPerExtraLevel < 0f)
                    stats.extraSpeedPercentPerExtraLevel = 0f;
                if (needsDrainPerAbility)
                {
                    float drainBase = stats.extraSpeedEnergyDrain > 0.0001f
                        ? stats.extraSpeedEnergyDrain
                        : ShipFamilyOverdriveAbility.DefaultExtraSpeedEnergyDrain;
                    stats.extraSpeedEnergyDrainPerExtraLevel =
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
                    // [TITAN-ORBIT] Ship-tier growth uses family fraction — not *PerExtraLevel.
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
