using System;
using System.Collections.Generic;
using UnityEngine;
using TitanOrbit.Core;

namespace TitanOrbit.Data
{
    /// <summary>
    /// ScriptableObject mapping each planet to a <see cref="ShipFamilyDefinition"/> and optional planet skin.
    /// Home planet (id 0) always resolves to AstroEagle; neutrals / captured planets use indices 1–11 rolled
    /// at spawn into <c>PlanetState.ShipFamilyConfigIndex</c>. Prefabs, chassis ids, unlock tiers, and gem
    /// costs come from each family's <c>upgradeTree</c>. <see cref="ShipFamilyEntry.planetMaterial"/> ties
    /// that family to a recognizable CW PLANETS surface so players can spot the ship tree from the planet
    /// look even when neutral placement is randomized. Paired with <see cref="PlanetShipFamilyAssignment"/>.
    /// </summary>
    [CreateAssetMenu(fileName = "PlanetShipFamilyConfig", menuName = "Titan Orbit/Planet Ship Family Config")]
    public class PlanetShipFamilyConfig : ScriptableObject
    {
        /// <summary>
        /// One config-list slot: ship family + optional world-skin material for neutrals that roll this index.
        /// Index 0 is reserved for home / AstroEagle (homes still use tropical water materials by team).
        /// </summary>
        [Serializable]
        public class ShipFamilyEntry
        {
            [Tooltip("List-slot label only (0 = home). Runtime neutrals use ShipFamilyConfigIndex, not this id.")]
            public int planetId;

            [Tooltip("Ship family definition. Prefabs and unlock tiers come from its upgradeTree.")]
            public ShipFamilyDefinition shipFamilyDefinition;

            [Tooltip("Optional display name; when empty, familyId from shipFamilyDefinition is used.")]
            public string familyName;

            [Tooltip(
                "Neutral / captured planet surface material for this family. Homes ignore this and use " +
                "PlanetMaterialPool water materials by team. Leave empty to fall back to planetId % pool.")]
            public Material planetMaterial;

            [Tooltip(
                "Optional override for this list slot's turret recipe. Prefer authoring " +
                "ShipFamilyDefinition.planetaryDefense instead. Leave empty to use the family " +
                "definition's recipe, then Resources/PlanetaryDefenseConfig.")]
            public PlanetaryDefenseConfig defenseConfig;
        }

        [Tooltip("Ordered list: index 0 = home planet family, index 1 = planet 1, etc.")]
        public List<ShipFamilyEntry> families = new List<ShipFamilyEntry>();

        /// <summary>
        /// Linear index into <see cref="ShipFamilyDefinition.upgradeTree"/>: sum of per-level slot counts for levels &lt; L, plus branch.
        /// Level 7 has 3 slots (MEGA); levels 1–6 have L slots each → total 24 tiers before repeating.
        /// </summary>
        public static int GetLadderLinearIndex(int level, int branchIndex)
        {
            // --- Compute value ---
            if (level < 1 || level > 7) return -1;
            int count = UpgradeTree.GetShipCountForLevel(level);
            if (branchIndex < 0 || branchIndex >= count) return -1;
            int offset = 0;
            for (int L = 1; L < level; L++)
                offset += UpgradeTree.GetShipCountForLevel(L);
            return offset + branchIndex;
        }

        /// <summary>Gets the family entry for the given planet.</summary>
        public ShipFamilyEntry GetFamilyForPlanet(int planetId) =>
            GetFamilyForPlanet(planetId, isHomePlanet: false, shipFamilyConfigIndex: -1);

        /// <summary>Resolves ship family using ECS planet state when available.</summary>
        public ShipFamilyEntry GetFamilyForPlanet(int planetId, bool isHomePlanet, int shipFamilyConfigIndex = -1)
        {
            int configIndex = ResolveConfigIndex(planetId, isHomePlanet, shipFamilyConfigIndex);
            return GetFamilyByConfigIndex(configIndex);
        }

        /// <summary>Gets a family entry by config list index (0 = home / AstroEagle).</summary>
        public ShipFamilyEntry GetFamilyByConfigIndex(int configIndex)
        {
            // --- Compute value ---
            if (families == null || families.Count == 0)
                return null;
            configIndex = Mathf.Clamp(configIndex, 0, families.Count - 1);
            return families[configIndex];
        }

        /// <summary>
        /// Planet surface material authored on the family entry at <paramref name="configIndex"/>.
        /// Returns null for missing entries or when the designer left <see cref="ShipFamilyEntry.planetMaterial"/> empty
        /// (caller should fall back to <see cref="PlanetMaterialPool"/>).
        /// </summary>
        /// <param name="configIndex">Index into <see cref="families"/> (same as ghosted ShipFamilyConfigIndex).</param>
        public Material GetPlanetMaterialForConfigIndex(int configIndex)
        {
            // --- Resolve authored skin ---
            ShipFamilyEntry entry = GetFamilyByConfigIndex(configIndex);
            return entry != null ? entry.planetMaterial : null;
        }

        /// <summary>Resolves config list index for a planet. Home planets always use AstroEagle (index 0).</summary>
        public int ResolveConfigIndex(int planetId, bool isHomePlanet, int shipFamilyConfigIndex = -1)
        {
            // --- Resolve value ---
            if (families == null || families.Count == 0)
                return 0;

            if (isHomePlanet || planetId == 0)
                return GetHomeFamilyConfigIndex();

            if (shipFamilyConfigIndex > 0 && shipFamilyConfigIndex < families.Count)
                return shipFamilyConfigIndex;

            if (planetId >= 100)
                return GetNonHomeFamilyConfigIndex(planetId - 100);

            for (int i = 0; i < families.Count; i++)
            {
                var f = families[i];
                if (f != null && f.planetId == planetId)
                    return i;
            }

            return GetNonHomeFamilyConfigIndex(Mathf.Abs(planetId));
        }

        /// <summary>Config list index for home planets (AstroEagle). [TITAN-ORBIT] Home always uses index 0.</summary>
        public int GetHomeFamilyConfigIndex()
        {
            // --- Compute value ---
            if (families == null || families.Count == 0)
                return 0;

            for (int i = 0; i < families.Count; i++)
            {
                if (families[i]?.planetId == 0)
                    return i;
            }

            return 0;
        }

        /// <summary>Number of captured-planet families (excludes home entry at index 0).</summary>
        public int GetNonHomeFamilyCount() => families == null ? 0 : Mathf.Max(0, families.Count - 1);

        /// <summary>Wraps a procedural ordinal into a non-home config index (1-based slot).</summary>
        public int GetNonHomeFamilyConfigIndex(int ordinal)
        {
            // --- Compute value ---
            int nonHomeCount = GetNonHomeFamilyCount();
            if (nonHomeCount <= 0)
                return GetHomeFamilyConfigIndex();
            return 1 + (Mathf.Abs(ordinal) % nonHomeCount);
        }

        /// <summary>Chassis ID at the ladder slot for this planet's resolved ship family.</summary>
        public string GetChassisIdForLadderSlot(int planetId, int level, int branchIndex, bool isHomePlanet = false, int shipFamilyConfigIndex = -1)
        {
            // --- Compute value ---
            int idx = GetLadderLinearIndex(level, branchIndex);
            if (idx < 0)
                return null;
            return GetChassisIdForPlanetAndIndex(planetId, idx, isHomePlanet, shipFamilyConfigIndex);
        }

        /// <summary>Legacy lookup by planet id only — prefer overload with isHomePlanet / config index.</summary>
        public ShipFamilyEntry GetFamilyForPlanetLegacy(int planetId)
        {
            // --- Compute value ---
            if (families == null || families.Count == 0) return null;
            for (int i = 0; i < families.Count; i++)
            {
                var f = families[i];
                if (f != null && f.planetId == planetId)
                    return f;
            }
            int safeId = planetId < 0 ? 0 : planetId;
            int index = safeId % families.Count;
            return families[index];
        }

        /// <summary>
        /// World-space / minimap planet label from this planet's family.
        /// Prefers designer <see cref="ShipFamilyEntry.familyName"/>, then camel-splits <c>familyId</c>
        /// (AstroEagle → "Astro Eagle"). Same string the world label and minimap hover tip show.
        /// </summary>
        /// <param name="planetId">Stable <c>PlanetState.PlanetId</c>.</param>
        public string GetPlanetDisplayNameFromFamilyId(int planetId) =>
            GetPlanetDisplayName(planetId, isHomePlanet: false, shipFamilyConfigIndex: -1);

        /// <summary>
        /// Resolves the player-facing planet name using home flag + ghosted family index.
        /// Homes always use the AstroEagle slot; neutrals use the index rolled at spawn.
        /// </summary>
        /// <param name="planetId">Stable planet id (homes are 0).</param>
        /// <param name="isHomePlanet">True for team home worlds — forces config index 0.</param>
        /// <param name="shipFamilyConfigIndex">Ghosted <c>PlanetState.ShipFamilyConfigIndex</c> (−1 = infer).</param>
        /// <returns>Display name, or empty when the config list has no family for this planet.</returns>
        public string GetPlanetDisplayName(int planetId, bool isHomePlanet, int shipFamilyConfigIndex = -1)
        {
            // --- Resolve family entry ---
            ShipFamilyEntry entry = GetFamilyForPlanet(planetId, isHomePlanet, shipFamilyConfigIndex);
            if (entry == null)
                return string.Empty;

            // Designer override wins (Inspector "familyName" on the config row).
            if (!string.IsNullOrWhiteSpace(entry.familyName))
                return entry.familyName.Trim();

            // Fallback: split the ScriptableObject familyId so AstroEagle reads as "Astro Eagle".
            string familyId = entry.shipFamilyDefinition != null ? entry.shipFamilyDefinition.familyId : null;
            if (string.IsNullOrWhiteSpace(familyId))
                return string.Empty;
            return Core.DisplayNameFormatting.SplitCamelCase(familyId.Trim());
        }

        /// <summary>
        /// Resolves <see cref="ShipFamilyDefinition"/> from a chassis id prefix (e.g. <c>AstroEagle_01</c> → AstroEagle family).
        /// </summary>
        public ShipFamilyDefinition GetShipFamilyDefinitionForChassisId(string chassisId)
        {
            // --- Compute value ---
            if (string.IsNullOrEmpty(chassisId) || families == null) return null;
            int underscoreIdx = chassisId.IndexOf('_');
            if (underscoreIdx <= 0) return null;
            string familyNamePrefix = chassisId.Substring(0, underscoreIdx);

            foreach (var f in families)
            {
                if (f?.shipFamilyDefinition == null) continue;
                string entryFamilyName = f.shipFamilyDefinition.familyId;
                if (string.IsNullOrEmpty(entryFamilyName) || !entryFamilyName.Equals(familyNamePrefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                return f.shipFamilyDefinition;
            }

            return null;
        }

        /// <summary>Gets the ship prefab for chassisId and planet. Resolves from the entry's ShipFamilyDefinition upgradeTree.</summary>
        public GameObject GetPrefabForChassisAndPlanet(string chassisId, int planetId)
        {
            // --- Compute value ---
            if (string.IsNullOrEmpty(chassisId)) return null;
            ShipFamilyEntry family = GetFamilyForPlanet(planetId);
            if (family?.shipFamilyDefinition?.upgradeTree == null) return null;

            foreach (var tier in family.shipFamilyDefinition.upgradeTree)
            {
                if (tier != null && tier.chassisId == chassisId && tier.prefab != null)
                    return tier.prefab;
            }
            return null;
        }

        /// <summary>Gets prefab by chassisId. Resolves family by name prefix in chassisId from each entry's ShipFamilyDefinition.</summary>
        public GameObject GetPrefabByChassisId(string chassisId)
        {
            // --- Compute value ---
            if (string.IsNullOrEmpty(chassisId) || families == null) return null;
            int underscoreIdx = chassisId.IndexOf('_');
            if (underscoreIdx <= 0) return null;
            string familyNamePrefix = chassisId.Substring(0, underscoreIdx);

            foreach (var f in families)
            {
                if (f?.shipFamilyDefinition?.upgradeTree == null) continue;
                string entryFamilyName = f.shipFamilyDefinition.familyId;
                if (string.IsNullOrEmpty(entryFamilyName) || !entryFamilyName.Equals(familyNamePrefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var tier in f.shipFamilyDefinition.upgradeTree)
                {
                    if (tier != null && tier.chassisId == chassisId && tier.prefab != null)
                        return tier.prefab;
                }
                return null;
            }
            return null;
        }

        /// <summary>Menu thumbnail for this chassis. Prefers team-specific <see cref="ShipFamilyChassisTierEntry.teamMenuPreviewSprites"/>, then falls back to <see cref="ShipFamilyChassisTierEntry.menuPreviewSprite"/>.</summary>
        public Sprite GetMenuPreviewSpriteForChassisId(string chassisId, TeamManager.Team team = TeamManager.Team.None)
        {
            // --- Compute value ---
            if (string.IsNullOrEmpty(chassisId) || families == null) return null;
            int underscoreIdx = chassisId.IndexOf('_');
            if (underscoreIdx <= 0) return null;
            string familyNamePrefix = chassisId.Substring(0, underscoreIdx);

            foreach (var f in families)
            {
                if (f?.shipFamilyDefinition?.upgradeTree == null) continue;
                string entryFamilyName = f.shipFamilyDefinition.familyId;
                if (string.IsNullOrEmpty(entryFamilyName) || !entryFamilyName.Equals(familyNamePrefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var tier in f.shipFamilyDefinition.upgradeTree)
                {
                    if (tier == null || tier.chassisId != chassisId)
                        continue;

                    if (tier.teamMenuPreviewSprites != null && tier.teamMenuPreviewSprites.Count > 0)
                    {
                        for (int i = 0; i < tier.teamMenuPreviewSprites.Count; i++)
                        {
                            var v = tier.teamMenuPreviewSprites[i];
                            if (v == null || v.sprite == null) continue;
                            if (team != TeamManager.Team.None && v.team == team)
                                return v.sprite;
                        }
                    }

                    if (tier.menuPreviewSprite != null)
                        return tier.menuPreviewSprite;
                }
                return null;
            }
            return null;
        }

        /// <summary>
        /// Orbit Menu name for a chassis: authored <c>upgradeTreeShipName</c>, or the
        /// formatted prefab (SpaceExcalibur_7 → Space Excalibur 7) when that field is blank.
        /// </summary>
        public string GetUpgradeTreeShipNameForChassisId(string chassisId)
        {
            // --- Compute value ---
            if (string.IsNullOrEmpty(chassisId) || families == null) return null;
            int underscoreIdx = chassisId.IndexOf('_');
            if (underscoreIdx <= 0) return null;
            string familyNamePrefix = chassisId.Substring(0, underscoreIdx);

            foreach (var f in families)
            {
                if (f?.shipFamilyDefinition?.upgradeTree == null) continue;
                string entryFamilyName = f.shipFamilyDefinition.familyId;
                if (string.IsNullOrEmpty(entryFamilyName) || !entryFamilyName.Equals(familyNamePrefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var tier in f.shipFamilyDefinition.upgradeTree)
                {
                    if (tier != null && tier.chassisId == chassisId)
                    {
                        string resolved = tier.ResolveUpgradeTreeShipName();
                        return string.IsNullOrEmpty(resolved) ? null : resolved;
                    }
                }
                return null;
            }
            return null;
        }

        /// <summary>
        /// Chassis card title: formatted prefab/authored name, then chassis id if both are empty.
        /// </summary>
        static string ResolveChassisDisplayName(ShipFamilyChassisTierEntry tier)
        {
            if (tier == null)
                return string.Empty;

            string name = tier.ResolveUpgradeTreeShipName();
            return !string.IsNullOrWhiteSpace(name) ? name : (tier.chassisId ?? string.Empty);
        }

        /// <summary>Upgrade-tree tier entry for a chassis ID, or null.</summary>
        public ShipFamilyChassisTierEntry GetTierEntryForChassisId(string chassisId)
        {
            // --- Compute value ---
            if (string.IsNullOrEmpty(chassisId) || families == null) return null;
            int underscoreIdx = chassisId.IndexOf('_');
            if (underscoreIdx <= 0) return null;
            string familyNamePrefix = chassisId.Substring(0, underscoreIdx);

            foreach (var f in families)
            {
                if (f?.shipFamilyDefinition?.upgradeTree == null) continue;
                string entryFamilyName = f.shipFamilyDefinition.familyId;
                if (string.IsNullOrEmpty(entryFamilyName) || !entryFamilyName.Equals(familyNamePrefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var tier in f.shipFamilyDefinition.upgradeTree)
                {
                    if (tier != null && tier.chassisId == chassisId)
                        return tier;
                }
                return null;
            }
            return null;
        }

        /// <summary>Gem purchase cost for a chassis at the given ship level (2× gem cap, L1→L6 gradient).</summary>
        public int GetPurchaseGemCostForChassisId(string chassisId, int shipLevel)
        {
            return ShipFamilyPowerScoreBreakdown.GetPurchaseGemCost(GetTierEntryForChassisId(chassisId), shipLevel);
        }

        /// <summary>Power score breakdown for this chassis from baked data or live prefab sum.</summary>
        public ShipFamilyPowerScoreBreakdown GetPowerScoreBreakdownForChassisId(string chassisId)
        {
            // --- Compute value ---
            if (string.IsNullOrEmpty(chassisId) || families == null) return default;
            int underscoreIdx = chassisId.IndexOf('_');
            if (underscoreIdx <= 0) return default;
            string familyNamePrefix = chassisId.Substring(0, underscoreIdx);

            foreach (var f in families)
            {
                if (f?.shipFamilyDefinition?.upgradeTree == null) continue;
                string entryFamilyName = f.shipFamilyDefinition.familyId;
                if (string.IsNullOrEmpty(entryFamilyName) || !entryFamilyName.Equals(familyNamePrefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var tier in f.shipFamilyDefinition.upgradeTree)
                {
                    if (tier == null || tier.chassisId != chassisId)
                        continue;

                    if (tier.powerScoreBreakdown.HasDisplayStats)
                        return tier.powerScoreBreakdown;

                    if (tier.prefab != null &&
                        ShipFamilyStatsCalculator.TrySumFromPrefab(
                            tier.prefab,
                            f.shipFamilyDefinition,
                            shipLevel: 1,
                            out ShipComponentAbilityStats stats))
                    {
                        return ShipFamilyPowerScoreBreakdown.FromSummedShipStats(
                            ShipComponentStoreData.GetEffectiveStatsAtShipLevel(stats, 1));
                    }

                    return default;
                }
                return default;
            }
            return default;
        }

        /// <summary>
        /// Upgrade-tree power-bar breakdown at <paramref name="shipLevel"/> with every HUD ability maxed.
        /// Prefers the baked at-ship-level field; does not change level-1
        /// <see cref="GetPowerScoreBreakdownForChassisId"/>.
        /// </summary>
        public ShipFamilyPowerScoreBreakdown GetPowerScoreBreakdownForChassisIdAtShipLevel(
            string chassisId,
            int shipLevel)
        {
            ShipFamilyDefinition family = GetShipFamilyDefinitionForChassisId(chassisId);
            ShipFamilyChassisTierEntry tier = GetTierEntryForChassisId(chassisId);
            return ShipFamilyPowerBarNorm.GetBreakdownAtShipLevel(family, tier, shipLevel);
        }

        /// <summary>
        /// Ten regular-family maxes for equal-slot power bars (cached; all families ×
        /// L1–L6 chassis at tree level). MEGA hulls use
        /// <see cref="ShipFamilyPowerBarNorm.GetMegaMaxPerStat"/> instead.
        /// </summary>
        public ShipPowerBarStatMaxes GetGlobalPowerBarStatMaxes() =>
            ShipFamilyPowerBarNorm.GetGlobalMaxPerStat(this);

        /// <summary>Gets chassis ID for the given planet and ship index (0-based). Uses the entry's ShipFamilyDefinition upgradeTree.</summary>
        public string GetChassisIdForPlanetAndIndex(int planetId, int index, bool isHomePlanet = false, int shipFamilyConfigIndex = -1)
        {
            // --- Compute value ---
            ShipFamilyEntry family = GetFamilyForPlanet(planetId, isHomePlanet, shipFamilyConfigIndex);
            if (family?.shipFamilyDefinition?.upgradeTree == null) return null;
            if (index < 0 || index >= family.shipFamilyDefinition.upgradeTree.Count) return null;

            var tier = family.shipFamilyDefinition.upgradeTree[index];
            if (tier != null) return tier.chassisId;
            return null;
        }

        /// <summary>Gets the chassis at the given index for the planet (from that planet's ShipFamilyDefinition upgrade tree).</summary>
        public ShipChassisDefinition GetChassisByIndex(int planetId, int index)
        {
            // --- Compute value ---
            if (index < 0) return null;
            ShipFamilyEntry family = GetFamilyForPlanet(planetId);
            if (family?.shipFamilyDefinition?.upgradeTree == null) return null;
            if (index >= family.shipFamilyDefinition.upgradeTree.Count) return null;

            var tier = family.shipFamilyDefinition.upgradeTree[index];
            if (tier == null) return null;

            var chassis = ScriptableObject.CreateInstance<ShipChassisDefinition>();
            chassis.chassisId = tier.chassisId;
            chassis.shipFamily = family.shipFamilyDefinition.familyId;
            chassis.displayName = ResolveChassisDisplayName(tier);
            chassis.basePrefab = tier.prefab;
            chassis.originPlanetId = planetId;
            chassis.minHomePlanetLevel = tier.minHomePlanetLevel;
            return chassis;
        }

        /// <summary>Gets the index of the chassis in the given planet's upgrade tree, or -1.</summary>
        public int GetIndexForChassisIdForPlanet(string chassisId, int planetId)
        {
            // --- Compute value ---
            if (string.IsNullOrEmpty(chassisId)) return -1;
            ShipFamilyEntry family = GetFamilyForPlanet(planetId);
            if (family?.shipFamilyDefinition?.upgradeTree == null) return -1;
            var tree = family.shipFamilyDefinition.upgradeTree;
            for (int i = 0; i < tree.Count; i++)
            {
                if (tree[i] != null && tree[i].chassisId == chassisId)
                    return i;
            }
            return -1;
        }

        /// <summary>Gets the index for chassis ID in the home planet (planet 0) upgrade tree.</summary>
        public int GetIndexForChassisId(string chassisId)
        {
            return GetIndexForChassisIdForPlanet(chassisId, 0);
        }

        /// <summary>Gets chassis by ID by searching all families' upgrade trees.</summary>
        public ShipChassisDefinition GetChassisByChassisId(string chassisId)
        {
            // --- Compute value ---
            if (string.IsNullOrEmpty(chassisId) || families == null) return null;
            foreach (var f in families)
            {
                if (f?.shipFamilyDefinition?.upgradeTree == null) continue;
                foreach (var tier in f.shipFamilyDefinition.upgradeTree)
                {
                    if (tier != null && tier.chassisId == chassisId)
                    {
                        var chassis = ScriptableObject.CreateInstance<ShipChassisDefinition>();
                        chassis.chassisId = tier.chassisId;
                        chassis.shipFamily = f.shipFamilyDefinition.familyId;
                        chassis.displayName = ResolveChassisDisplayName(tier);
                        chassis.basePrefab = tier.prefab;
                        chassis.originPlanetId = f.planetId;
                        chassis.minHomePlanetLevel = tier.minHomePlanetLevel;
                        return chassis;
                    }
                }
            }
            return null;
        }

        /// <summary>Gets prefab for chassis ID (searches all families). Alias for GetPrefabByChassisId.</summary>
        public GameObject GetPrefabForChassisId(string chassisId)
        {
            return GetPrefabByChassisId(chassisId);
        }

        /// <summary>Builds unlock entries for the given planet at the given home planet level (tier filtering by minHomePlanetLevel).</summary>
        public List<ShipUnlockEntry> GetUnlockedEntriesForPlanet(int homePlanetLevel, int planetId)
        {
            // --- Compute value ---
            var result = new List<ShipUnlockEntry>();
            ShipFamilyEntry family = GetFamilyForPlanet(planetId);
            if (family?.shipFamilyDefinition?.upgradeTree == null) return result;

            foreach (var tier in family.shipFamilyDefinition.upgradeTree)
            {
                if (tier == null) continue;
                if (homePlanetLevel < tier.minHomePlanetLevel) continue;

                var chassis = ScriptableObject.CreateInstance<ShipChassisDefinition>();
                chassis.chassisId = tier.chassisId;
                chassis.shipFamily = family.shipFamilyDefinition.familyId;
                chassis.displayName = ResolveChassisDisplayName(tier);
                chassis.basePrefab = tier.prefab;
                chassis.originPlanetId = planetId;
                chassis.minHomePlanetLevel = tier.minHomePlanetLevel;

                result.Add(new ShipUnlockEntry
                {
                    chassis = chassis,
                    minHomePlanetLevel = tier.minHomePlanetLevel,
                    gemCost = ShipFamilyPowerScoreBreakdown.GetPurchaseGemCost(tier, tier.minHomePlanetLevel)
                });
            }
            return result;
        }

        /// <summary>Returns all chassis unlocked at the given home planet level for the home planet (planet 0).</summary>
        public List<ShipChassisDefinition> GetUnlockedChassis(int homePlanetLevel)
        {
            // --- Compute value ---
            var entries = GetUnlockedEntriesForPlanet(homePlanetLevel, 0);
            var result = new List<ShipChassisDefinition>();
            foreach (var e in entries)
            {
                if (e?.chassis != null) result.Add(e.chassis);
            }
            return result;
        }
    }
}
