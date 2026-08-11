using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Session-level balance anchors for Titan Orbit economy and ship part curves.
    /// Used by editor balance reports (<c>TitanOrbit → Balance → …</c>) and by
    /// <see cref="ShipFamilyPartCalcSuggestionSeeds"/> / ProfileSet defaults so designers can
    /// audit numbers against the same targets that drive Scan seeds.
    /// <para>
    /// Design session: 2–5 teams × up to 20 players, open-ended domination (~0.5–2 hours).
    /// Capture goal: about <see cref="CaptureShipCount"/> ships at shipLevel ≈ planetLevel should
    /// drain a full same-level home planet in roughly <see cref="CaptureTargetUnloadBatches"/>
    /// full cargo unload cycles (orbit dwell is separate).
    /// </para>
    /// </summary>
    public static class GameBalanceTargets
    {
        // --- Session shape ---

        /// <summary>Minimum teams the map recipe supports (code allows 2–5).</summary>
        public const int MinTeams = 2;

        /// <summary>Maximum teams; live MapGenerationSettings often locks to 5.</summary>
        public const int MaxTeams = 5;

        /// <summary>Soft cap players per team (GameBootstrap / join).</summary>
        public const int PlayersPerTeam = 20;

        /// <summary>Short end of a healthy domination match (minutes of active play).</summary>
        public const float MatchLengthMinutesMin = 30f;

        /// <summary>Long end of a healthy domination match (minutes of active play).</summary>
        public const float MatchLengthMinutesMax = 120f;

        // --- Capture (people) ---

        /// <summary>
        /// How many ships of similar level should be enough to capture a same-level planet.
        /// [TITAN-ORBIT] Designer request: ~3 average ships, not a 10-ship zerg.
        /// </summary>
        public const int CaptureShipCount = 3;

        /// <summary>
        /// Target full-cargo unload cycles (per ship fleet) to empty a full same-level planet.
        /// Mid of the plan’s 4–6 batch window.
        /// </summary>
        public const int CaptureTargetUnloadBatches = 5;

        /// <summary>Reference homeworld size used in capture peopleCap math (MapGenerationSettings).</summary>
        public const float ReferenceHomePlanetSize = 20f;

        /// <summary>Reference homeworld starting level (MapGenerationSettings).</summary>
        public const int ReferenceHomePlanetLevel = 3;

        /// <summary>
        /// [TITAN-ORBIT] Same exponent as <c>PlanetPopulationMath.PopulationLevelExponent</c>
        /// (kept here so Data does not reference Simulation).
        /// </summary>
        public const float PopulationLevelExponent = 1.7f;

        /// <summary>
        /// Target median people capacity at ship level 3 (post tier growth, no ability upgrades).
        /// Derived: homePop(size 20, L3) / (CaptureShipCount × CaptureTargetUnloadBatches).
        /// </summary>
        public static float TargetMedianPeopleCapAtShipLevel3 =>
            GetReferenceHomePopulation() / (CaptureShipCount * CaptureTargetUnloadBatches);

        /// <summary>
        /// Implied L1 peopleCap before default +10%/shipLevel tier growth
        /// (<c>base × (1 + (level−1)×0.10)</c>).
        /// </summary>
        public static float TargetMedianPeopleCapAtShipLevel1 =>
            TargetMedianPeopleCapAtShipLevel3 / GetShipTierGrowthMultiplier(3);

        /// <summary>
        /// Design assumption for median contributing Wing mounts per hull.
        /// [TITAN-ORBIT] Fleet composition report (241 chassis) measured median Wings = 2 —
        /// keep this in sync when re-running TitanOrbit → Balance → Export Fleet Composition.
        /// static readonly (not const) so Editor assemblies never keep a stale inlined value.
        /// </summary>
        public static readonly float ExpectedMedianWingCount = 2f;

        /// <summary>Expected contributing Cockpit mounts that author people/gems (usually 1).</summary>
        public static readonly float ExpectedMedianCockpitCount = 1f;

        /// <summary>Expected contributing cargo parts = cockpit + wings.</summary>
        public static float ExpectedMedianCargoPartCount =>
            ExpectedMedianCockpitCount + ExpectedMedianWingCount;

        /// <summary>
        /// Per cargo part people at version 1.
        /// Tuned so measured L3 median peopleCap stays near <see cref="TargetMedianPeopleCapAtShipLevel3"/>
        /// after fleet Scan (capture batches ≈ 4–6 with <see cref="CaptureShipCount"/> = 3).
        /// <para>
        /// [TITAN-ORBIT] Prefab sum collapses duplicate Wing/Cockpit meshes to one row per component
        /// id (same as CountParts unique-id semantics). That removed LOD multi-counts, so the
        /// per-part seed is higher than the old 1.7 full-sum tuning (~2.55 restores L3 ≈ 8.6).
        /// </para>
        /// Gems use a separate per-part formula below.
        /// </summary>
        public static readonly float AuthoredPeoplePerCargoPartV1 = 2.55f;

        /// <summary>People seed for Cockpit/Wing ProfileSet rows (capture-tuned, not gem-coupled).</summary>
        public static float SuggestedPeoplePerCargoPartV1 => AuthoredPeoplePerCargoPartV1;

        // --- Asteroid combat / mining loop ---

        /// <summary>Mid-band asteroid Size used for TTK / gem-fill checks.</summary>
        public const float MidAsteroidSize = 35f;

        /// <summary>Target time-to-kill (seconds) for mid rock at median L1 DPS (pure shooting).</summary>
        public const float MidAsteroidTtkSecondsMin = 8f;

        /// <summary>Upper target TTK (seconds) for mid rock at median L1 DPS.</summary>
        public const float MidAsteroidTtkSecondsMax = 12f;

        /// <summary>Ideal mid-rock TTK used when suggesting HealthPerSize from median DPS.</summary>
        public const float MidAsteroidTtkSecondsIdeal = 10f;

        /// <summary>
        /// Soft target seconds of mining+travel to fill a mid-tier gemCap before moon deposit.
        /// Includes travel; pure mining at <see cref="ReferenceMiningRateGemsPerSecond"/> is faster.
        /// </summary>
        public const float GemFillLoopSecondsMin = 45f;

        /// <summary>Soft upper bound for a gem fill loop (seconds).</summary>
        public const float GemFillLoopSecondsMax = 90f;

        /// <summary>Must match <c>GemEconomyConstants.MiningRate</c> for report math.</summary>
        public const float ReferenceMiningRateGemsPerSecond = 5f;

        /// <summary>
        /// Target median gemCap at ship L1 so chassis cost (<c>2 × gemCap</c>) is ~1–2 cargo trips
        /// and a mining fill sits near the short end of the gem-fill loop when including travel.
        /// </summary>
        public const float TargetMedianGemCapAtShipLevel1 = 40f;

        /// <summary>
        /// Cargo-part count used only for gem sizing (not people).
        /// [TITAN-ORBIT] L1 median hulls often have ~2 contributing cargo parts even when
        /// fleet-wide cargo median is 3 — size gems so L1 gemCap lands near
        /// <see cref="TargetMedianGemCapAtShipLevel1"/>.
        /// </summary>
        public static readonly float ExpectedMedianCargoPartsForGemSizing = 2f;

        /// <summary>
        /// Per cargo part maxGems at version 1 for a median L1 hull
        /// (<see cref="TargetMedianGemCapAtShipLevel1"/> / <see cref="ExpectedMedianCargoPartsForGemSizing"/>).
        /// </summary>
        public static float SuggestedGemsPerCargoPartV1 =>
            TargetMedianGemCapAtShipLevel1 / Mathf.Max(1f, ExpectedMedianCargoPartsForGemSizing);

        // --- Energy / weapons / hull durability ---

        /// <summary>
        /// Hull energy Cap should cover this many seconds of continuous fire
        /// (<c>energyCap ≈ firePower × fireRate × this</c>).
        /// [TITAN-ORBIT] Designer request: ~3 seconds of full firing before the bar empties.
        /// </summary>
        public const float EnergyBatterySecondsOfSustainedFire = 3f;

        /// <summary>
        /// Sustained fire regen fraction of drain — must match
        /// <see cref="ShipComponentWeaponSuggestions.EnergyRegenFractionOfSustainedDrain"/>.
        /// [TITAN-ORBIT] Designer request: ~30% of weapon consumption (holding fire still drains).
        /// </summary>
        public const float EnergyRegenFractionOfSustainedDrain = 0.30f;

        /// <summary>
        /// After aggregation, energyRegen below this fraction of sustained drain ⇒ insolvency flag.
        /// </summary>
        public const float EnergyInsolvencyRegenFractionOfDrain = 0.2f;

        /// <summary>Overdrive burst window (seconds) that must not fully starve the weapon battery.</summary>
        public const float OverdriveBurstSeconds = 2f;

        /// <summary>
        /// Target hull health as seconds of the ship's own DPS
        /// (<c>healthCap ≈ dps × this</c> at median L1).
        /// [TITAN-ORBIT] Designer request: ~3 seconds of average DPS to empty average health.
        /// </summary>
        public const float HealthSecondsOfOwnDps = 3f;

        /// <summary>
        /// Expected contributing health parts on a median L1 hull (cockpit + hull pieces).
        /// Used to derive <see cref="ShipComponentHealthSuggestions.HealthCapV1"/>.
        /// </summary>
        public static readonly float ExpectedMedianHealthPartCount = 4f;

        /// <summary>Reference L1 single Weapon Bullet DPS (firePower × fireRate seeds).</summary>
        public static float ReferenceL1BulletWeaponDps =>
            ShipComponentWeaponSuggestions.FirePowerV1 * ShipComponentWeaponSuggestions.FireRate;

        /// <summary>Target median L1 healthCap ≈ reference bullet DPS × <see cref="HealthSecondsOfOwnDps"/>.</summary>
        public static float TargetMedianHealthCapAtShipLevel1 =>
            ReferenceL1BulletWeaponDps * HealthSecondsOfOwnDps;

        /// <summary>Per health-part Cap at version 1 so median stacks near <see cref="TargetMedianHealthCapAtShipLevel1"/>.</summary>
        public static float SuggestedHealthCapPerPartV1 =>
            TargetMedianHealthCapAtShipLevel1 / Mathf.Max(1f, ExpectedMedianHealthPartCount);

        // --- Progression / costs ---

        /// <summary>
        /// Chassis purchase trips of full gem cargo implied by <c>cost = 2 × gemCap</c>.
        /// Already identity with the formula; reports flag if formula drifts.
        /// </summary>
        public const float ChassisCostCargoTripsTarget = 2f;

        /// <summary>Chassis gem cost multiplier on gemCap (must match GetPurchaseGemCost).</summary>
        public const float ChassisCostGemCapMultiplier = 2f;

        /// <summary>Bottom-bar attribute cost per purchase: shipLevel × this (ShipAttributeUpgradeLogic).</summary>
        public const int AttributeUpgradeCostPerShipLevel = 5;

        /// <summary>Default ship-tier stat growth fraction per level above 1 (+10%).</summary>
        public const float ShipLevelStatGrowthFraction = 0.1f;

        /// <summary>Dedicated players should reach L7 chassis in this active-minute band.</summary>
        public const float ChassisLadderMinutesMin = 30f;

        /// <summary>Upper active-minute band for L1→L7 chassis progression.</summary>
        public const float ChassisLadderMinutesMax = 90f;

        // --- Outlier thresholds ---

        /// <summary>People/gem cap above this × same-level median ⇒ cargo freak.</summary>
        public const float CargoFreakMultiplier = 2f;

        /// <summary>Propulsion/wings ratio below this × fleet median ⇒ propulsion starvation.</summary>
        public const float PropulsionStarvationRatioOfMedian = 0.45f;

        /// <summary>Wings at/above this percentile with propulsion at/below p10 ⇒ wing balloon.</summary>
        public const float WingBalloonPercentile = 0.9f;

        /// <summary>Move speed below this × same-level median ⇒ slow hull flag.</summary>
        public const float SlowHullMoveSpeedRatioOfMedian = 0.55f;

        /// <summary>
        /// Reference home population using the live size×level^1.7 formula (no triangle bonus).
        /// </summary>
        public static float GetReferenceHomePopulation()
        {
            float size = Mathf.Max(0.25f, ReferenceHomePlanetSize);
            int level = Mathf.Max(1, ReferenceHomePlanetLevel);
            return Mathf.RoundToInt(size * Mathf.Pow(level, PopulationLevelExponent));
        }

        /// <summary>
        /// Tier growth multiplier for ship level L: <c>1 + (L−1) × ShipLevelStatGrowthFraction</c>.
        /// </summary>
        /// <param name="shipLevel">1-based ship chassis level.</param>
        public static float GetShipTierGrowthMultiplier(int shipLevel)
        {
            int level = Mathf.Max(1, shipLevel);
            return 1f + (level - 1) * ShipLevelStatGrowthFraction;
        }

        /// <summary>
        /// Suggested asteroid HealthPerSize so mid rock TTK ≈ ideal seconds at the given L1 DPS.
        /// </summary>
        /// <param name="medianL1Dps">Fleet median firePower × fireRate at ship level 1.</param>
        public static float SuggestHealthPerSizeForMidRock(float medianL1Dps)
        {
            float dps = Mathf.Max(0.01f, medianL1Dps);
            float targetHp = dps * MidAsteroidTtkSecondsIdeal;
            return targetHp / Mathf.Max(0.01f, MidAsteroidSize);
        }

        /// <summary>
        /// Markdown / report header block listing every locked target (auditable).
        /// </summary>
        public static string FormatTargetsHeaderMarkdown()
        {
            return
                "# GameBalanceTargets\n\n" +
                $"- Session: {MinTeams}–{MaxTeams} teams × {PlayersPerTeam} players, " +
                $"~{MatchLengthMinutesMin:0}–{MatchLengthMinutesMax:0} min domination\n" +
                $"- Capture: {CaptureShipCount} ships × {CaptureTargetUnloadBatches} batches; " +
                $"home pop≈{GetReferenceHomePopulation():0} → L3 peopleCap≈{TargetMedianPeopleCapAtShipLevel3:0.0} " +
                $"(L1≈{TargetMedianPeopleCapAtShipLevel1:0.0})\n" +
                $"- Cargo parts assumption: {ExpectedMedianCockpitCount:0} cockpit + {ExpectedMedianWingCount:0} wings " +
                $"(median cargo≈{ExpectedMedianCargoPartCount:0}) → people/part V1={SuggestedPeoplePerCargoPartV1:0.00} (capture-tuned), " +
                $"gems/part V1≈{SuggestedGemsPerCargoPartV1:0.00} (gemCap target÷{ExpectedMedianCargoPartsForGemSizing:0} L1 cargo parts)\n" +
                $"- Mid asteroid Size {MidAsteroidSize}: TTK {MidAsteroidTtkSecondsMin:0}–{MidAsteroidTtkSecondsMax:0}s " +
                $"(ideal {MidAsteroidTtkSecondsIdeal:0}s); gem fill loop {GemFillLoopSecondsMin:0}–{GemFillLoopSecondsMax:0}s\n" +
                $"- Target L1 gemCap≈{TargetMedianGemCapAtShipLevel1:0}; chassis cost = {ChassisCostGemCapMultiplier:0}×gemCap " +
                $"(≈{ChassisCostCargoTripsTarget:0} cargo trips)\n" +
                $"- Energy: Cap ≈ {EnergyBatterySecondsOfSustainedFire:0}s of fire; " +
                $"regen fraction of sustained drain = {EnergyRegenFractionOfSustainedDrain:0.00}; " +
                $"insolvency below {EnergyInsolvencyRegenFractionOfDrain:0.00}\n" +
                $"- Health: ≈ {HealthSecondsOfOwnDps:0}s of own DPS " +
                $"(L1 target≈{TargetMedianHealthCapAtShipLevel1:0.#}, part V1≈{SuggestedHealthCapPerPartV1:0.##})\n" +
                $"- Attribute upgrade cost = shipLevel × {AttributeUpgradeCostPerShipLevel}\n";
        }
    }
}
