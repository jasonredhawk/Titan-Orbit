using System;
using System.Collections.Generic;
using TitanOrbit.Core;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Per-stat ceilings for Orbit Menu power bars. Each of the ten ability slots
    /// fills as <c>thisCard / poolMax</c>, so Health Regen is readable next to Health Cap.
    /// <para>
    /// Three pools, never mixed. Regular hulls (levels 1–6) share one ceiling:
    /// every family's chassis at that chassis's tree level. Titans (level 7) share
    /// a second ceiling: every armed catalog MEGA. Gear shares a third, and that
    /// third is split by part profile: engines with engines, weapons with weapons,
    /// wings with wings, at the card's ship level. A weapon's energy cap does not
    /// set the ceiling for an engine. Drones, rockets, and mines stay on the
    /// unsplit gear ceiling. A Titan's guns would squash regular bars, and a
    /// whole ship's health would squash a single part.
    /// Slot 0 on a ship is sustained DPS from the barrels that fire. On gear that
    /// lane stays empty: a bought weapon unlocks a bullet type, and ramming is not DPS.
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

        /// <summary>
        /// Same lanes as <see cref="Absorb"/>, but slot 0 stays empty.
        /// Gear does not add ship DPS. Ship bars must keep <see cref="Absorb"/> —
        /// a hull’s Fire Power lane is the guns that are actually firing.
        /// </summary>
        public void AbsorbComponentCompare(in ShipFamilyPowerScoreBreakdown breakdown)
        {
            firePower = Mathf.Max(firePower, breakdown.GetComponentCompareStatValue(0));
            bulletSpeed = Mathf.Max(bulletSpeed, breakdown.GetComponentCompareStatValue(1));
            healthCap = Mathf.Max(healthCap, breakdown.GetComponentCompareStatValue(2));
            healthRegen = Mathf.Max(healthRegen, breakdown.GetComponentCompareStatValue(3));
            energyCap = Mathf.Max(energyCap, breakdown.GetComponentCompareStatValue(4));
            energyRegen = Mathf.Max(energyRegen, breakdown.GetComponentCompareStatValue(5));
            moveSpeed = Mathf.Max(moveSpeed, breakdown.GetComponentCompareStatValue(6));
            turnSpeed = Mathf.Max(turnSpeed, breakdown.GetComponentCompareStatValue(7));
            gemCap = Mathf.Max(gemCap, breakdown.GetComponentCompareStatValue(8));
            peopleCap = Mathf.Max(peopleCap, breakdown.GetComponentCompareStatValue(9));
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
    /// Which catalog a power bar is measuring against. The three pools never share
    /// a ceiling: a Titan’s guns would flatten every regular hull, and a whole
    /// ship’s health would flatten every single component.
    /// </summary>
    public enum ShipPowerBarComparisonPool
    {
        /// <summary>Caller has not chosen yet. Treated as <see cref="RegularShips"/>.</summary>
        Unset = 0,

        /// <summary>Upgrade-tree levels 1–6. Every family’s regular chassis.</summary>
        RegularShips = 1,

        /// <summary>Upgrade-tree level 7. Armed Titans (catalog MEGAs) only.</summary>
        Titans = 2,

        /// <summary>
        /// Orbit Menu gear. Purchasable components on every ship family, scored
        /// at one ship level, compared inside one part profile (Engine, Wing, …).
        /// Not whole hulls. A null profile on this pool is the unsplit gear ceiling
        /// used by drones, rockets, and mines.
        /// </summary>
        Components = 3
    }

    /// <summary>
    /// The chassis or component that currently owns one power-bar slot's catalog max.
    /// Regular ships, Titans, and gear each keep their own winners so a 90-gun Titan
    /// cannot steal RANK 1 from a regular hull, and a whole ship cannot steal it from a part.
    /// <para>
    /// [TITAN-ORBIT] Identity only — combat never reads this. Hover tips in
    /// <c>ShipPowerBarStatTooltip</c> show the winner as a small RANK 1 line.
    /// For gear, <see cref="hullName"/> is the part name and <see cref="chassisId"/>
    /// is <see cref="ShipFamilyPowerBarNorm.FormatComponentLeaderKey"/>.
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

        /// <summary>
        /// Planet fleet multiplier already inside <see cref="value"/> for this lane.
        /// Gear only. 0 on ship and Titan lines (those do not name a world).
        /// 1 is a real home-world roll and still prints as 1.00×.
        /// </summary>
        public float planetBonusMul;

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
    /// Resolves upgrade-tree power-bar stats: Extra Level at the chassis's ship
    /// level with zero ability purchases — the hull the moment you buy it, before
    /// any bottom-HUD upgrade. Same formulas as live ships — non-weapons use
    /// <c>shipLevel + ability</c> per part; weapons omit N; weapon bullet speed is
    /// ability-only. Ability counts stay 0, so a Fire Power purchase is not added
    /// on top of the ship level. Live prefab sums are cached per session.
    /// Also walks three separate ten-stat max pools and remembers who set each
    /// slot’s ceiling (RANK 1 for power-bar hover tips):
    /// regular hulls (L1–L6), Titans (armed MEGAs), and gear components from every family.
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
        /// One gear ceiling per ship level. Extra Level changes component numbers, so
        /// an L2 engine must not share a denominator with an L6 engine.
        /// <para>
        /// <see cref="maxes"/> / <see cref="leaders"/> are every part type together
        /// (drones, rockets, mines). <see cref="byCategory"/> is the card-gear
        /// comparison: one ceiling per part profile.
        /// </para>
        /// </summary>
        struct ComponentComparePool
        {
            public int bankSignature;
            public ShipPowerBarStatMaxes maxes;
            public ShipPowerBarStatLeader[] leaders;
            public Dictionary<string, ComponentCategoryCeiling> byCategory;
        }

        /// <summary>
        /// Highest gear lane and who owns it, among parts of one profile
        /// (all engines, or all wings) at one ship level.
        /// </summary>
        sealed class ComponentCategoryCeiling
        {
            public ShipPowerBarStatMaxes maxes;
            public ShipPowerBarStatLeader[] leaders;
        }

        static readonly Dictionary<int, ComponentComparePool> s_componentPools =
            new Dictionary<int, ComponentComparePool>(8);

        /// <summary>
        /// Last bank lookup the Orbit Menu passed in. Hover tips rebuild from this
        /// if the menu has not painted a level yet. Data code cannot see live planets.
        /// </summary>
        static Func<int, int> s_componentBankResolver;

        /// <summary>
        /// Extra Level at the tier's tree level with zero ability purchases.
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
            // Purchase-time hull: ship level counts, bottom-HUD ability levels do not.
            ShipAbilityLevelCounts fresh = default;
            if (TryBuildBreakdown(
                    entry.prefab, family, shipLevel, in fresh,
                    out _, out ShipFamilyPowerScoreBreakdown breakdown))
            {
                entry.powerScoreBreakdownAtShipLevel = breakdown;
                return;
            }

            entry.powerScoreBreakdownAtShipLevel = default;
        }

        /// <summary>
        /// Display breakdown for a chassis at <paramref name="shipLevel"/> with zero
        /// ability purchases. This is the minimum on the card the moment the hull
        /// is bought, before any bottom-HUD upgrade. Live-sums the prefab
        /// (session-cached) so the bar matches that fresh ship even when an older
        /// bake still stored the fully upgraded ceiling.
        /// </summary>
        public static ShipFamilyPowerScoreBreakdown GetBreakdownAtShipLevel(
            ShipFamilyDefinition family,
            ShipFamilyChassisTierEntry tier,
            int shipLevel)
        {
            if (tier == null)
                return default;

            shipLevel = Mathf.Max(1, shipLevel);
            string cacheKey = (tier.chassisId ?? string.Empty) + "@" + shipLevel + "@base";
            if (s_liveCache.TryGetValue(cacheKey, out ShipFamilyPowerScoreBreakdown cached))
                return cached;

            // --- Zero ability purchases ---
            // [TITAN-ORBIT] Ship level still grows the hull (a level-3 ship is stronger
            // than a level-1 ship). Ability counts stay 0 so one Fire Power buy is not
            // added on top. Level-1 Astro Eagle Hawk is 11.25 DPS/s here, not 13.5.
            ShipAbilityLevelCounts fresh = default;
            if (tier.prefab != null &&
                family != null &&
                TryBuildBreakdown(
                    tier.prefab, family, shipLevel, in fresh,
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
        /// chassis (levels 1–6), each evaluated at that chassis's tree level with zero
        /// ability purchases. MEGA catalog hulls are excluded. Cached until
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
                    // literal Titan pool tag; RANK 1 must name the ship and its line.
                    string visualFamily = entry != null
                        ? DisplayNameFormatting.SplitCamelCase(entry.visualFamily.ToString())
                        : MegaShipCatalog.DisplayClassShort;
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
        /// Part-profile bucket for a gear card (Engine, Weapon Bullet, Wing, …).
        /// An Astro Eagle engine and a Hyper Falcon engine share this key.
        /// A weapon never shares it with an engine. Idempotent on a key this
        /// method already returned, so hover lookup can pass the stored label.
        /// </summary>
        public static string ResolveComponentCompareCategory(string componentId)
        {
            string raw = ShipComponentAbilityStats.ResolvePartTypeForSuggestedStats(componentId);
            string normalized = ShipFamilyPartTypes.Normalize(raw, componentId);
            if (string.IsNullOrWhiteSpace(normalized)
                || string.Equals(normalized, ShipFamilyPartTypes.Unmapped, StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, ShipFamilyPartTypes.Ignore, StringComparison.OrdinalIgnoreCase))
                return ShipFamilyPartTypes.Hull;
            return normalized;
        }

        /// <summary>
        /// Highest value of each of the ten gear lanes across every purchasable
        /// component on every ship family, scored at <paramref name="shipLevel"/>.
        /// Pass <paramref name="partCategory"/> (Engine, Wing, Weapon Bullet, …)
        /// so a card is measured against the same profile only. Null keeps the
        /// unsplit ceiling (drones, rockets, mines). Not a finished hull.
        /// Cached per ship level until the families’ bullet banks change or
        /// <see cref="InvalidateCache"/> runs.
        /// </summary>
        /// <param name="shipLevel">Extra Level used on the card being painted (at least 1).</param>
        /// <param name="bankForFamilyConfigIndex">
        /// Optional. Given a family-list index, returns that family’s live bullet bank
        /// (Laserbolt, Lightning, …). Null uses each part’s authored default gun.
        /// The Orbit Menu passes the match’s rolled banks so the ceiling matches the card.
        /// </param>
        /// <param name="partCategory">
        /// Same-profile bucket from <see cref="ResolveComponentCompareCategory"/>.
        /// Null returns the mixed ceiling across every part type.
        /// </param>
        public static ShipPowerBarStatMaxes GetComponentMaxPerStat(
            int shipLevel,
            Func<int, int> bankForFamilyConfigIndex = null,
            string partCategory = null)
        {
            shipLevel = Mathf.Max(1, shipLevel);
            if (bankForFamilyConfigIndex != null)
                s_componentBankResolver = bankForFamilyConfigIndex;
            else
                bankForFamilyConfigIndex = s_componentBankResolver;

            // --- Reuse this level when the rolled guns have not changed ---
            PlanetShipFamilyConfig config = PlanetShipFamilyConfig.LoadDefault();
            int bankSignature = ComputeFamilyBankSignature(config, bankForFamilyConfigIndex);
            if (!s_componentPools.TryGetValue(shipLevel, out ComponentComparePool cached)
                || cached.bankSignature != bankSignature
                || cached.leaders == null
                || cached.byCategory == null)
            {
                cached = BuildComponentComparePool(shipLevel, config, bankForFamilyConfigIndex, bankSignature);
                s_componentPools[shipLevel] = cached;
            }

            // Null category: drones / rockets / mines still share one gear ceiling.
            if (string.IsNullOrWhiteSpace(partCategory))
                return cached.maxes;

            string categoryKey = ResolveComponentCompareCategory(partCategory);
            if (cached.byCategory.TryGetValue(categoryKey, out ComponentCategoryCeiling ceiling))
                return ceiling.maxes;

            ShipPowerBarStatMaxes empty = ShipPowerBarStatMaxes.CreateEmpty();
            empty.EnsureMinimum();
            return empty;
        }

        /// <summary>
        /// One pass over every family’s store parts. Fills the mixed ceiling and
        /// one ceiling per part profile so hover can name the best engine without
        /// reading a weapon that happens to hold more energy.
        /// </summary>
        static ComponentComparePool BuildComponentComparePool(
            int shipLevel,
            PlanetShipFamilyConfig config,
            Func<int, int> bankForFamilyConfigIndex,
            int bankSignature)
        {
            // --- Walk every family’s parts at this Extra Level ---
            // [TITAN-ORBIT] An Astro Eagle engine is readable next to a Hyper Falcon
            // engine because they share a denominator. A cannon’s energy cap is a
            // different profile and does not shrink that engine’s bar. Whole ships
            // stay out of this walk.
            var maxes = ShipPowerBarStatMaxes.CreateEmpty();
            ShipPowerBarStatLeader[] leaders = CreateEmptyLeaders();
            var byCategory = new Dictionary<string, ComponentCategoryCeiling>(8, StringComparer.Ordinal);
            if (config?.families != null)
            {
                for (int familyIndex = 0; familyIndex < config.families.Count; familyIndex++)
                {
                    PlanetShipFamilyConfig.ShipFamilyEntry familySlot = config.families[familyIndex];
                    ShipFamilyDefinition def = familySlot?.shipFamilyDefinition;
                    if (def?.components == null)
                        continue;

                    int bank = bankForFamilyConfigIndex != null
                        ? bankForFamilyConfigIndex(familyIndex)
                        : -1;
                    string familyLabel = ResolveFamilyLabel(familySlot, def);

                    for (int c = 0; c < def.components.Count; c++)
                    {
                        // One iteration = one store part (engine, gun, wing, …).
                        ShipFamilyComponentEntry entry = def.components[c];
                        if (entry == null || string.IsNullOrWhiteSpace(entry.componentId))
                            continue;

                        // GetPowerBreakdown already multiplies by this family's live planet
                        // roll, so a +40% energy world can beat a higher base engine.
                        ShipFamilyPowerScoreBreakdown breakdown =
                            ShipComponentStoreData.GetPowerBreakdown(entry, shipLevel, def, bank);
                        ShipFamilySpecialBonuses planetBonuses = def.ResolveSpecialBonuses();
                        string partName = ShipComponentStoreData.GetDisplayName(entry);
                        string leaderKey = FormatComponentLeaderKey(def.familyId, entry.componentId);
                        Sprite preview = ShipComponentStoreData.GetMenuPreviewSprite(def, entry);
                        maxes.AbsorbComponentCompare(breakdown);
                        AbsorbLeaders(
                            leaders,
                            breakdown,
                            familyLabel,
                            partName,
                            leaderKey,
                            treeLevel: 0,
                            preview,
                            useComponentCompare: true,
                            recordPlanetBonus: true,
                            planetBonuses: planetBonuses);

                        string category = ResolveComponentCompareCategory(entry.componentId);
                        if (!byCategory.TryGetValue(category, out ComponentCategoryCeiling ceiling))
                        {
                            ceiling = new ComponentCategoryCeiling
                            {
                                maxes = ShipPowerBarStatMaxes.CreateEmpty(),
                                leaders = CreateEmptyLeaders()
                            };
                            byCategory[category] = ceiling;
                        }

                        // Class field: assign back so the struct ceiling is the one we keep.
                        ShipPowerBarStatMaxes catMax = ceiling.maxes;
                        catMax.AbsorbComponentCompare(breakdown);
                        ceiling.maxes = catMax;
                        AbsorbLeaders(
                            ceiling.leaders,
                            breakdown,
                            familyLabel,
                            partName,
                            leaderKey,
                            treeLevel: 0,
                            preview,
                            useComponentCompare: true,
                            recordPlanetBonus: true,
                            planetBonuses: planetBonuses);
                    }
                }
            }

            maxes.EnsureMinimum();
            foreach (KeyValuePair<string, ComponentCategoryCeiling> pair in byCategory)
            {
                ShipPowerBarStatMaxes catMax = pair.Value.maxes;
                catMax.EnsureMinimum();
                pair.Value.maxes = catMax;
            }

            return new ComponentComparePool
            {
                bankSignature = bankSignature,
                maxes = maxes,
                leaders = leaders,
                byCategory = byCategory
            };
        }

        /// <summary>
        /// Stable id for “this part is RANK 1”. Family plus component id, because two
        /// families can both sell a part whose short name is Thrusters.
        /// </summary>
        public static string FormatComponentLeaderKey(string familyId, string componentId)
        {
            return (familyId ?? string.Empty) + "|" + (componentId ?? string.Empty);
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
        /// Hull or part that owns the catalog max for one display slot (0–9).
        /// Regular families for L1–L6, Titans for L7, same-profile components for gear cards.
        /// Builds the matching cache on first call.
        /// </summary>
        /// <param name="statIndex">Power-bar slot (0 = DPS … 9 = Troop Cap).</param>
        /// <param name="pool">Which ceiling this bar was painted against.</param>
        /// <param name="shipLevel">Gear only. Extra Level of the component pool.</param>
        /// <param name="partCategory">
        /// Gear only. Part profile of the hovered card (Engine, Wing, …).
        /// Null uses the mixed gear ceiling (drones, rockets, mines).
        /// </param>
        public static ShipPowerBarStatLeader GetStatLeader(
            int statIndex,
            ShipPowerBarComparisonPool pool,
            int shipLevel = 1,
            string partCategory = null)
        {
            if (statIndex < 0 || statIndex >= ShipPowerBarStatMaxes.StatCount)
                return default;

            if (pool == ShipPowerBarComparisonPool.Unset)
                pool = ShipPowerBarComparisonPool.RegularShips;

            // --- Ensure the walk that fills maxes also filled leaders ---
            ShipPowerBarStatLeader[] leaders;
            if (pool == ShipPowerBarComparisonPool.Titans)
            {
                GetMegaMaxPerStat();
                leaders = s_cachedMegaLeaders;
            }
            else if (pool == ShipPowerBarComparisonPool.Components)
            {
                shipLevel = Mathf.Max(1, shipLevel);
                if (!s_componentPools.TryGetValue(shipLevel, out ComponentComparePool componentPool)
                    || componentPool.leaders == null
                    || componentPool.byCategory == null)
                {
                    GetComponentMaxPerStat(shipLevel, s_componentBankResolver);
                    s_componentPools.TryGetValue(shipLevel, out componentPool);
                }

                // Hover on an engine names the best engine, not the best weapon.
                leaders = componentPool.leaders;
                if (!string.IsNullOrWhiteSpace(partCategory) && componentPool.byCategory != null)
                {
                    string categoryKey = ResolveComponentCompareCategory(partCategory);
                    if (componentPool.byCategory.TryGetValue(categoryKey, out ComponentCategoryCeiling ceiling)
                        && ceiling?.leaders != null)
                        leaders = ceiling.leaders;
                    else
                        leaders = null;
                }
            }
            else
            {
                GetGlobalMaxPerStat();
                leaders = s_cachedRegularLeaders;
            }

            if (leaders == null || statIndex >= leaders.Length)
                return default;
            return leaders[statIndex];
        }

        /// <summary>
        /// Hull that owns the catalog max for one display slot (0–9).
        /// Uses the same pool as the bar fill: regular families for L1–L6, Titans for L7.
        /// </summary>
        /// <param name="statIndex">Power-bar slot (0 = DPS … 9 = Troop Cap).</param>
        /// <param name="megaPool">True for Titan catalog winners; false for regular families.</param>
        public static ShipPowerBarStatLeader GetStatLeader(int statIndex, bool megaPool)
        {
            return GetStatLeader(
                statIndex,
                megaPool ? ShipPowerBarComparisonPool.Titans : ShipPowerBarComparisonPool.RegularShips);
        }

        /// <summary>
        /// Drops live-sum, regular-family max, Titan max, gear-component max, RANK 1
        /// leader, and weapon-roster caches after an editor rebake or catalog rebuild.
        /// </summary>
        public static void InvalidateCache()
        {
            s_hasCachedRegularMaxes = false;
            s_hasCachedMegaMaxes = false;
            s_cachedRegularMaxes = default;
            s_cachedMegaMaxes = default;
            s_cachedRegularLeaders = null;
            s_cachedMegaLeaders = null;
            s_componentPools.Clear();
            s_liveCache.Clear();
            // Weapon-roster cache walks the same prefabs / catalog rows.
            ShipWeaponLoadout.InvalidateCache();
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
        /// Keeps the higher value per display stat and records that hull or part as RANK 1.
        /// Ties keep the first winner so the tip does not flip between equals.
        /// </summary>
        /// <param name="useComponentCompare">
        /// True for gear. Slot 0 stays empty (no added DPS). Ship walks leave this false.
        /// </param>
        static void AbsorbLeaders(
            ShipPowerBarStatLeader[] leaders,
            in ShipFamilyPowerScoreBreakdown breakdown,
            string familyId,
            string hullName,
            string chassisId,
            int treeLevel,
            Sprite preview,
            bool useComponentCompare = false,
            bool recordPlanetBonus = false,
            ShipFamilySpecialBonuses planetBonuses = default)
        {
            if (leaders == null)
                return;

            for (int stat = 0; stat < leaders.Length; stat++)
            {
                // One iteration = one ODEMC slot (DPS, Bullet Speed, … Troop Cap).
                float value = useComponentCompare
                    ? breakdown.GetComponentCompareStatValue(stat)
                    : breakdown.GetDisplayStatValue(stat);
                if (value <= leaders[stat].value)
                    continue;

                leaders[stat] = new ShipPowerBarStatLeader
                {
                    familyId = familyId ?? string.Empty,
                    hullName = hullName ?? string.Empty,
                    chassisId = chassisId ?? string.Empty,
                    treeLevel = treeLevel,
                    value = value,
                    previewSprite = preview,
                    // 0 hides the world name on ship lines. Gear stores the roll
                    // that is already inside the value (1 = home world).
                    planetBonusMul = recordPlanetBonus
                        ? planetBonuses.MultiplierForPowerBarStat(stat)
                        : 0f
                };
            }
        }

        /// <summary>
        /// Player-facing family name for a gear RANK 1 line. Prefers the config label,
        /// then splits CamelCase (<c>AstroEagle</c> → Astro Eagle).
        /// </summary>
        static string ResolveFamilyLabel(
            PlanetShipFamilyConfig.ShipFamilyEntry familySlot,
            ShipFamilyDefinition def)
        {
            if (familySlot != null && !string.IsNullOrWhiteSpace(familySlot.familyName))
                return familySlot.familyName.Trim();
            if (def == null || string.IsNullOrWhiteSpace(def.familyId))
                return string.Empty;
            return DisplayNameFormatting.SplitCamelCase(def.familyId);
        }

        /// <summary>
        /// Fingerprint of each family’s live bullet bank. A new roll (Lightning instead
        /// of Laserbolt) changes weapon ceilings, so the gear cache must rebuild.
        /// </summary>
        static int ComputeFamilyBankSignature(
            PlanetShipFamilyConfig config,
            Func<int, int> bankForFamilyConfigIndex)
        {
            int count = config?.families != null ? config.families.Count : 0;
            int hash = 17;
            for (int i = 0; i < count; i++)
            {
                int bank = bankForFamilyConfigIndex != null ? bankForFamilyConfigIndex(i) : -1;
                hash = unchecked(hash * 31 + bank + 1);
            }

            // Planet rolls change who wins a lane. A new match seed must not
            // keep the previous game's ceilings.
            hash = unchecked(hash * 31 + (int)ShipFamilyMatchBonuses.ActiveMatchSeed);
            return unchecked(hash * 31 + count);
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
