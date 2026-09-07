using System;
using System.Collections.Generic;
using TitanOrbit.Core;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Per-stat ceilings for the Orbit Menu upgrade-tree power bar. Each of the ten
    /// ability slots fills as <c>thisShip / poolMax</c>, so Health Regen is readable
    /// next to Health Cap.
    /// <para>
    /// Regular hulls (levels 1–6) share one pool: every family's chassis at that
    /// chassis's tree level with every HUD ability maxed. MEGA hulls (level 7) share
    /// a second pool: every armed catalog MEGA. The two pools never mix — a MEGA's
    /// firepower would otherwise squash regular bars, and regular maxes would flatten
    /// every MEGA bar to full. Slot 0 is sustained DPS (<c>firePower × fireRate</c>).
    /// Paired with <see cref="UI.ShipUpgradeTreePowerBarUI"/>.
    /// </para>
    /// </summary>
    public struct ShipPowerBarStatMaxes
    {
        public const int StatCount = ShipFamilyPowerScoreBreakdown.DisplayStatCount;

        /// <summary>
        /// Highest sustained DPS in this pool (<c>firePower × fireRate</c>), not raw
        /// damage per shot. NightAye cannons and AstroEagle machineguns share this ceiling.
        /// </summary>
        public float firePower;
        public float bulletSpeed;
        public float healthCap;
        public float healthRegen;
        public float energyCap;
        public float energyRegen;
        public float moveSpeed;
        public float turnSpeed;
        public float gemCap;
        public float peopleCap;

        /// <summary>Floor so a missing catalog never divides by zero (empty bar stays empty).</summary>
        public const float MinDenominator = 0.001f;

        /// <summary>All zeros — caller must <see cref="Absorb"/> real ships or <see cref="EnsureMinimum"/>.</summary>
        public static ShipPowerBarStatMaxes CreateEmpty() => default;

        /// <summary>Raises each slot to at least <see cref="MinDenominator"/> so fill ratios stay defined.</summary>
        public void EnsureMinimum()
        {
            firePower = Mathf.Max(firePower, MinDenominator);
            bulletSpeed = Mathf.Max(bulletSpeed, MinDenominator);
            healthCap = Mathf.Max(healthCap, MinDenominator);
            healthRegen = Mathf.Max(healthRegen, MinDenominator);
            energyCap = Mathf.Max(energyCap, MinDenominator);
            energyRegen = Mathf.Max(energyRegen, MinDenominator);
            moveSpeed = Mathf.Max(moveSpeed, MinDenominator);
            turnSpeed = Mathf.Max(turnSpeed, MinDenominator);
            gemCap = Mathf.Max(gemCap, MinDenominator);
            peopleCap = Mathf.Max(peopleCap, MinDenominator);
        }

        /// <summary>Keeps the higher value per display stat from <paramref name="breakdown"/>.</summary>
        public void Absorb(in ShipFamilyPowerScoreBreakdown breakdown)
        {
            firePower = Mathf.Max(firePower, breakdown.GetDisplayStatValue(0));
            bulletSpeed = Mathf.Max(bulletSpeed, breakdown.GetDisplayStatValue(1));
            healthCap = Mathf.Max(healthCap, breakdown.GetDisplayStatValue(2));
            healthRegen = Mathf.Max(healthRegen, breakdown.GetDisplayStatValue(3));
            energyCap = Mathf.Max(energyCap, breakdown.GetDisplayStatValue(4));
            energyRegen = Mathf.Max(energyRegen, breakdown.GetDisplayStatValue(5));
            moveSpeed = Mathf.Max(moveSpeed, breakdown.GetDisplayStatValue(6));
            turnSpeed = Mathf.Max(turnSpeed, breakdown.GetDisplayStatValue(7));
            gemCap = Mathf.Max(gemCap, breakdown.GetDisplayStatValue(8));
            peopleCap = Mathf.Max(peopleCap, breakdown.GetDisplayStatValue(9));
        }

        /// <summary>Pool max for display stat index 0–9 (DPS … Troop Cap).</summary>
        public float Get(int statIndex)
        {
            switch (statIndex)
            {
                case 0: return firePower;
                case 1: return bulletSpeed;
                case 2: return healthCap;
                case 3: return healthRegen;
                case 4: return energyCap;
                case 5: return energyRegen;
                case 6: return moveSpeed;
                case 7: return turnSpeed;
                case 8: return gemCap;
                case 9: return peopleCap;
                default: return MinDenominator;
            }
        }
    }

    /// <summary>
    /// The chassis that currently owns one power-bar slot's catalog max.
    /// Regular-family and MEGA pools keep separate winners so a 90-gun MEGA cannot
    /// steal RANK 1 from NightAye on the L1–L6 tree.
    /// <para>
    /// [TITAN-ORBIT] Identity only — combat never reads this. Hover tips in
    /// <c>ShipPowerBarStatTooltip</c> show the hull as a small RANK 1 line.
    /// </para>
    /// </summary>
    public struct ShipPowerBarStatLeader
    {
        /// <summary>Family folder id (<c>AstroEagle</c>) or <c>MEGA</c>.</summary>
        public string familyId;

        /// <summary>Orbit-menu hull name (<see cref="ShipFamilyChassisTierEntry.upgradeTreeShipName"/> or MEGA display name).</summary>
        public string hullName;

        /// <summary>Stable chassis id (<c>NightAye_16</c>, <c>MEGA_007</c>).</summary>
        public string chassisId;

        /// <summary>Tree level 1–6 for family hulls. MEGAs use 7.</summary>
        public int treeLevel;

        /// <summary>Winning display-stat value (same units as the bar fill numerator).</summary>
        public float value;

        /// <summary>Existing menu thumbnail. Null when the tier has no preview baked.</summary>
        public Sprite previewSprite;

        /// <summary>True when a real hull won this slot (not the empty cache default).</summary>
        public bool IsValid =>
            value > ShipPowerBarStatMaxes.MinDenominator
            && (!string.IsNullOrEmpty(chassisId) || !string.IsNullOrEmpty(hullName));

        /// <summary>
        /// True when <paramref name="otherChassisId"/> is this winner.
        /// Used so a hovered card can say "this hull" instead of repeating its own name.
        /// </summary>
        public bool MatchesChassis(string otherChassisId)
        {
            if (string.IsNullOrEmpty(chassisId) || string.IsNullOrEmpty(otherChassisId))
                return false;
            return string.Equals(chassisId, otherChassisId, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Resolves upgrade-tree power-bar stats: Extra Level at ship level with every HUD
    /// ability maxed (<see cref="ShipAbilityLevelCounts.Maxed"/>). Same formulas as live
    /// ships — non-weapons use <c>(ship−1) + ability + (N−1)</c>; weapons omit N;
    /// weapon bullet speed is ability-only. Live prefab sums are cached per session.
    /// Also walks the regular-family catalog and the MEGA catalog for two separate
    /// ten-stat max pools, and remembers which hull set each slot's ceiling
    /// (RANK 1 for power-bar hover tips).
    /// </summary>
    public static class ShipFamilyPowerBarNorm
    {
        /// <summary>
        /// MEGA tree column / ship level. Family upgrade trees stop at 6; slot 7 is catalog MEGAs.
        /// </summary>
        public const int MegaTreeLevel = 7;

        static readonly Dictionary<string, ShipFamilyPowerScoreBreakdown> s_liveCache =
            new Dictionary<string, ShipFamilyPowerScoreBreakdown>();

        static ShipPowerBarStatMaxes s_cachedRegularMaxes;
        static ShipPowerBarStatMaxes s_cachedMegaMaxes;
        static ShipPowerBarStatLeader[] s_cachedRegularLeaders;
        static ShipPowerBarStatLeader[] s_cachedMegaLeaders;
        static bool s_hasCachedRegularMaxes;
        static bool s_hasCachedMegaMaxes;

        /// <summary>
        /// Extra Level at the tier's tree level with every HUD ability maxed.
        /// Writes <see cref="ShipFamilyChassisTierEntry.powerScoreBreakdownAtShipLevel"/>.
        /// Editor bake path — call after the tier's ship level is assigned.
        /// </summary>
        public static void BakeAtShipLevel(ShipFamilyChassisTierEntry entry, ShipFamilyDefinition family)
        {
            if (entry == null)
                return;

            if (entry.prefab == null || family == null)
            {
                entry.powerScoreBreakdownAtShipLevel = default;
                return;
            }

            int shipLevel = Mathf.Max(1, entry.minHomePlanetLevel);
            ShipAbilityLevelCounts maxed = ShipAbilityLevelCounts.Maxed(shipLevel);
            if (TryBuildBreakdown(
                    entry.prefab, family, shipLevel, in maxed,
                    out _, out ShipFamilyPowerScoreBreakdown breakdown))
            {
                entry.powerScoreBreakdownAtShipLevel = breakdown;
                return;
            }

            entry.powerScoreBreakdownAtShipLevel = default;
        }

        /// <summary>
        /// Display breakdown for a chassis at <paramref name="shipLevel"/> with every HUD
        /// ability maxed. Live-sums the prefab (session-cached) so bars stay correct even
        /// when the editor bake still has the older ability-0 values.
        /// </summary>
        public static ShipFamilyPowerScoreBreakdown GetBreakdownAtShipLevel(
            ShipFamilyDefinition family,
            ShipFamilyChassisTierEntry tier,
            int shipLevel)
        {
            if (tier == null)
                return default;

            shipLevel = Mathf.Max(1, shipLevel);
            string cacheKey = (tier.chassisId ?? string.Empty) + "@" + shipLevel + "@max";
            if (s_liveCache.TryGetValue(cacheKey, out ShipFamilyPowerScoreBreakdown cached))
                return cached;

            ShipAbilityLevelCounts maxed = ShipAbilityLevelCounts.Maxed(shipLevel);
            if (tier.prefab != null &&
                family != null &&
                TryBuildBreakdown(
                    tier.prefab, family, shipLevel, in maxed,
                    out _, out ShipFamilyPowerScoreBreakdown live))
            {
                s_liveCache[cacheKey] = live;
                return live;
            }

            // Last resort: level-1 bake so the bar is not blank if the prefab cannot be summed.
            return tier.powerScoreBreakdown;
        }

        /// <summary>
        /// Extra Level the prefab, then stamp all-gun DPS onto the breakdown
        /// (every mount's <c>firePower × fireRate</c>, plus family offense muls).
        /// Used by power-bar bake, live tree paint, and upgrade-tree resort.
        /// </summary>
        public static bool TryBuildBreakdown(
            GameObject prefab,
            ShipFamilyDefinition family,
            int shipLevel,
            in ShipAbilityLevelCounts abilities,
            out ShipComponentAbilityStats stats,
            out ShipFamilyPowerScoreBreakdown breakdown)
        {
            stats = default;
            breakdown = default;
            if (!ShipFamilyStatsCalculator.TrySumFromPrefab(
                    prefab, family, shipLevel, in abilities, out stats,
                    out ShipFamilyStatsCalculator.SumResult raw))
                return false;

            float dps = ShipWeaponDpsMath.SumAllGunDps(
                raw.MatchedComponentIds, raw.PerComponentStats, shipLevel, in abilities);
            dps = ShipWeaponDpsMath.ApplyFamilyOffenseMuls(dps, family);
            breakdown = ShipFamilyPowerScoreBreakdown.FromEvaluatedHull(stats, dps);
            return true;
        }

        /// <summary>
        /// Highest value of each of the ten display stats across every regular-family
        /// chassis (levels 1–6), each evaluated at that chassis's tree level with every
        /// HUD ability maxed. MEGA catalog hulls are excluded. Cached until
        /// <see cref="InvalidateCache"/>.
        /// </summary>
        /// <param name="config">Optional planet-family list. Null scans every loaded family asset.</param>
        public static ShipPowerBarStatMaxes GetGlobalMaxPerStat(PlanetShipFamilyConfig config = null)
        {
            if (s_hasCachedRegularMaxes)
                return s_cachedRegularMaxes;

            // --- Regular-family pool (L1–L6, all families) ---
            // [TITAN-ORBIT] One AstroEagle L3 Health Cap must be readable next to a
            // HyperFalcon L6 Health Cap. That only works if every regular chassis
            // shares the same denominator — and MEGAs stay out of that denominator.
            var maxes = ShipPowerBarStatMaxes.CreateEmpty();
            ShipPowerBarStatLeader[] leaders = CreateEmptyLeaders();
            IEnumerable<ShipFamilyDefinition> families = EnumerateFamilies(config);
            if (families != null)
            {
                foreach (ShipFamilyDefinition def in families)
                {
                    if (def?.upgradeTree == null)
                        continue;

                    for (int i = 0; i < def.upgradeTree.Count; i++)
                    {
                        ShipFamilyChassisTierEntry tier = def.upgradeTree[i];
                        if (tier == null)
                            continue;

                        // Skip leftover L7 family rows and any MEGA_### chassis id.
                        // Family assets normally stop at level 6; this is the safety net.
                        int level = Mathf.Max(1, tier.minHomePlanetLevel);
                        if (IsMegaTreeLevel(level) || MegaShipCatalog.IsMegaChassisId(tier.chassisId))
                            continue;

                        ShipFamilyPowerScoreBreakdown breakdown = GetBreakdownAtShipLevel(def, tier, level);
                        maxes.Absorb(breakdown);

                        // --- Remember who set each slot's max ---
                        // [TITAN-ORBIT] Same numbers as the bar fill. First hull keeps a
                        // tie so RANK 1 is stable across refreshes.
                        // Blank upgradeTreeShipName fills from the prefab
                        // (SpaceExcalibur_7 → Space Excalibur 7).
                        AbsorbLeaders(
                            leaders,
                            breakdown,
                            def.familyId,
                            ResolveTierShipDisplayName(tier, def.familyId),
                            tier.chassisId,
                            level,
                            tier.GetMenuPreviewSprite());
                    }
                }
            }

            maxes.EnsureMinimum();
            s_cachedRegularMaxes = maxes;
            s_cachedRegularLeaders = leaders;
            s_hasCachedRegularMaxes = true;
            return s_cachedRegularMaxes;
        }

        /// <summary>
        /// Highest value of each of the ten display stats across every armed MEGA hull
        /// in <see cref="MegaShipCatalog"/>. Regular-family chassis are excluded.
        /// Cached until <see cref="InvalidateCache"/>.
        /// </summary>
        public static ShipPowerBarStatMaxes GetMegaMaxPerStat()
        {
            if (s_hasCachedMegaMaxes)
                return s_cachedMegaMaxes;

            // --- MEGA-only pool ---
            // [TITAN-ORBIT] MEGA firepower is an order of magnitude above L6 family
            // hulls. Comparing MEGAs to each other needs a MEGA-only ceiling so a
            // mid-pack hull does not paint every segment full.
            var maxes = ShipPowerBarStatMaxes.CreateEmpty();
            ShipPowerBarStatLeader[] leaders = CreateEmptyLeaders();
            MegaShipCatalog catalog = MegaShipCatalog.Load();
            if (catalog?.entries != null)
            {
                for (int i = 0; i < catalog.entries.Count; i++)
                {
                    // Unarmed editor rows stay in the catalog but never appear on the
                    // tree — skip them so a 0-gun hull cannot set a bogus health max.
                    if (!catalog.IsEligibleForMatch(i))
                        continue;

                    ShipFamilyPowerScoreBreakdown breakdown = catalog.GetPowerBreakdown(i);
                    maxes.Absorb(breakdown);

                    MegaShipCatalogEntry entry = catalog.entries[i];
                    // Visual line (Craizan Star / Galactic Leopard / Galactic Okamoto) —
                    // same "family" slot regular hulls use for AstroEagle. Not the
                    // literal "MEGA" pool tag; RANK 1 must name the ship and its line.
                    string visualFamily = entry != null
                        ? DisplayNameFormatting.SplitCamelCase(entry.visualFamily.ToString())
                        : "MEGA";
                    string megaShipName = DisplayNameFormatting.FormatPrefabShipName(
                        catalog.GetDisplayName(i));
                    if (string.IsNullOrWhiteSpace(megaShipName))
                        megaShipName = MegaShipCatalog.FormatChassisId(i);

                    AbsorbLeaders(
                        leaders,
                        breakdown,
                        visualFamily,
                        megaShipName,
                        MegaShipCatalog.FormatChassisId(i),
                        MegaTreeLevel,
                        entry != null ? entry.GetMenuPreviewSprite() : null);
                }
            }

            maxes.EnsureMinimum();
            s_cachedMegaMaxes = maxes;
            s_cachedMegaLeaders = leaders;
            s_hasCachedMegaMaxes = true;
            return s_cachedMegaMaxes;
        }

        /// <summary>
        /// True for the MEGA column (level 7) or any <c>MEGA_###</c> chassis id.
        /// Regular family hulls always use levels 1–6.
        /// </summary>
        /// <param name="treeLevel">Upgrade-tree slot level (1–7).</param>
        /// <param name="chassisId">Optional chassis id; MEGA prefix wins even if level is stale.</param>
        public static bool UsesMegaPowerBarPool(int treeLevel, string chassisId = null)
        {
            return IsMegaTreeLevel(treeLevel) || MegaShipCatalog.IsMegaChassisId(chassisId);
        }

        /// <summary>
        /// Regular-family maxes for L1–L6 nodes; MEGA catalog maxes for L7 / MEGA hulls.
        /// Pass already-resolved regular maxes so tree refresh does not recompute them.
        /// </summary>
        /// <param name="treeLevel">Node or current-ship level.</param>
        /// <param name="regularMaxes">Precomputed <see cref="GetGlobalMaxPerStat"/> result.</param>
        /// <param name="chassisId">Optional; forces the MEGA pool when the id is <c>MEGA_###</c>.</param>
        public static ShipPowerBarStatMaxes ResolveForTreeLevel(
            int treeLevel,
            in ShipPowerBarStatMaxes regularMaxes,
            string chassisId = null)
        {
            return UsesMegaPowerBarPool(treeLevel, chassisId)
                ? GetMegaMaxPerStat()
                : regularMaxes;
        }

        /// <summary>True when this upgrade-tree level is the MEGA column.</summary>
        public static bool IsMegaTreeLevel(int treeLevel) => treeLevel >= MegaTreeLevel;

        /// <summary>
        /// Hull that owns the catalog max for one display slot (0–9).
        /// Uses the same pool as the bar fill: regular families for L1–L6, MEGAs for L7.
        /// Builds the matching max cache on first call.
        /// </summary>
        /// <param name="statIndex">Power-bar slot (0 = DPS … 9 = Troop Cap).</param>
        /// <param name="megaPool">True for MEGA catalog winners; false for regular families.</param>
        public static ShipPowerBarStatLeader GetStatLeader(int statIndex, bool megaPool)
        {
            if (statIndex < 0 || statIndex >= ShipPowerBarStatMaxes.StatCount)
                return default;

            // --- Ensure the walk that fills maxes also filled leaders ---
            if (megaPool)
                GetMegaMaxPerStat();
            else
                GetGlobalMaxPerStat();

            ShipPowerBarStatLeader[] leaders = megaPool ? s_cachedMegaLeaders : s_cachedRegularLeaders;
            if (leaders == null || statIndex >= leaders.Length)
                return default;
            return leaders[statIndex];
        }

        /// <summary>
        /// Drops live-sum, regular-family max, MEGA max, and RANK 1 leader caches after
        /// an editor rebake or catalog rebuild.
        /// </summary>
        public static void InvalidateCache()
        {
            s_hasCachedRegularMaxes = false;
            s_hasCachedMegaMaxes = false;
            s_cachedRegularMaxes = default;
            s_cachedMegaMaxes = default;
            s_cachedRegularLeaders = null;
            s_cachedMegaLeaders = null;
            s_liveCache.Clear();
        }

        /// <summary>
        /// Player-facing ship name for one upgrade-tree chassis.
        /// Prefers the authored Orbit Menu name, then the formatted prefab
        /// (<c>SpaceExcalibur_7</c> → Space Excalibur 7), then the chassis id.
        /// Never returns only the family when a hull is known.
        /// </summary>
        public static string ResolveTierShipDisplayName(ShipFamilyChassisTierEntry tier, string familyId)
        {
            if (tier == null)
                return familyId ?? string.Empty;

            string name = tier.ResolveUpgradeTreeShipName();
            return !string.IsNullOrWhiteSpace(name) ? name : (familyId ?? string.Empty);
        }

        /// <summary>Allocates ten empty leader slots (one per power-bar stat).</summary>
        static ShipPowerBarStatLeader[] CreateEmptyLeaders()
        {
            return new ShipPowerBarStatLeader[ShipPowerBarStatMaxes.StatCount];
        }

        /// <summary>
        /// Keeps the higher value per display stat and records that hull as RANK 1.
        /// Ties keep the first winner so the tip does not flip between equal ships.
        /// </summary>
        static void AbsorbLeaders(
            ShipPowerBarStatLeader[] leaders,
            in ShipFamilyPowerScoreBreakdown breakdown,
            string familyId,
            string hullName,
            string chassisId,
            int treeLevel,
            Sprite preview)
        {
            if (leaders == null)
                return;

            for (int stat = 0; stat < leaders.Length; stat++)
            {
                // One iteration = one ODEMC slot (DPS, Bullet Speed, … Troop Cap).
                float value = breakdown.GetDisplayStatValue(stat);
                if (value <= leaders[stat].value)
                    continue;

                leaders[stat] = new ShipPowerBarStatLeader
                {
                    familyId = familyId ?? string.Empty,
                    hullName = hullName ?? string.Empty,
                    chassisId = chassisId ?? string.Empty,
                    treeLevel = treeLevel,
                    value = value,
                    previewSprite = preview
                };
            }
        }

        static IEnumerable<ShipFamilyDefinition> EnumerateFamilies(PlanetShipFamilyConfig config)
        {
            if (config?.families != null && config.families.Count > 0)
            {
                var list = new List<ShipFamilyDefinition>(config.families.Count);
                for (int i = 0; i < config.families.Count; i++)
                {
                    ShipFamilyDefinition def = config.families[i]?.shipFamilyDefinition;
                    if (def != null)
                        list.Add(def);
                }

                if (list.Count > 0)
                    return list;
            }

            return Resources.FindObjectsOfTypeAll<ShipFamilyDefinition>();
        }
    }
}
