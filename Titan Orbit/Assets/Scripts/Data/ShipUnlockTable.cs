using System;
using System.Collections.Generic;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Entry describing one chassis unlock at a given home planet level.
    /// </summary>
    [Serializable]
    public class ShipUnlockEntry
    {
        public ShipChassisDefinition chassis;

        [Tooltip("Minimum home planet level required to purchase this chassis.")]
        public int minHomePlanetLevel = 1;

        [Tooltip("Gem cost to purchase this chassis (2× gem cap, L1→L6 gradient at tier level). Set when unlock entries are built.")]
        public float gemCost = 20f;
    }

    /// <summary>
    /// Table of chassis unlocks for all families. Replaces the old UpgradeTree for ship selection.
    /// </summary>
    [CreateAssetMenu(fileName = "ShipUnlockTable", menuName = "Titan Orbit/Ship Unlock Table")]
    public class ShipUnlockTable : ScriptableObject
    {
        [Tooltip("Planet-to-ship-family mapping. When set, each planet gets its own ship collection from ModularExamples.")]
        public PlanetShipFamilyConfig planetShipFamilyConfig;

        [Tooltip("Optional ShipFamilyDefinition used for the home planet (planetId 0). Uses its upgradeTree to determine which ships unlock when.")]
        public ShipFamilyDefinition homeShipFamilyDefinition;
        [Tooltip("All chassis that can be purchased (legacy AstroEagle when planetShipFamilyConfig is null).")]
        public List<ShipUnlockEntry> entries = new List<ShipUnlockEntry>();

        /// <summary>
        /// Returns all entries that are unlocked at the given home planet level (for UI: show tier + cost per entry).
        /// All home planets use the same AstroEagle family (1 ship at L1, +2 at L2, +3 at L3, +4 at L4, +5 at L5, +5 at L6 = 20 total).
        /// If the table is empty or has wrong content, populates/overwrites with the default 20 AstroEagle variants.
        /// </summary>
        public List<ShipUnlockEntry> GetUnlockedEntries(int homePlanetLevel)
        {
            EnsureDefaultAstroEagleEntries();
            var result = new List<ShipUnlockEntry>();
            if (entries == null) return result;
            foreach (var entry in entries)
            {
                if (entry?.chassis == null) continue;
                if (homePlanetLevel >= entry.minHomePlanetLevel)
                    result.Add(entry);
            }
            if (result.Count == 0 && homePlanetLevel >= 1)
            {
                entries.Clear();
                EnsureDefaultAstroEagleEntries();
                foreach (var entry in entries)
                {
                    if (entry?.chassis == null) continue;
                    if (homePlanetLevel >= entry.minHomePlanetLevel)
                        result.Add(entry);
                }
            }
            return result;
        }

        /// <summary>
        /// Ensures the table has exactly 20 AstroEagle chassis with correct tier progression for home planets.
        /// Level 1: 1 ship (AstroEagle_01). Level 2: +2 (3 total). Level 3: +3 (6 total). Level 4: +4 (10 total). Level 5: +5 (15 total). Level 6: +5 (20 total).
        /// Replaces existing entries if table is empty or doesn't match this progression (so home store is never broken).
        /// </summary>
        private void EnsureDefaultAstroEagleEntries()
        {
            if (entries == null) entries = new List<ShipUnlockEntry>();
            // If we already have the correct 20 AstroEagle entries, keep them
            if (entries.Count == 20 && entries[0]?.chassis != null && entries[0].chassis.chassisId == "AstroEagle_01")
                return;
            entries.Clear();

            const string family = "AstroEagle";
            // Level 1: ship 1. Level 2: ships 2,3. Level 3: ships 4,5,6. Level 4: 7-10. Level 5: 11-15. Level 6: 16-20.
            int[] minLevelByShipIndex = { 1, 2, 2, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 5, 6, 6, 6, 6, 6 };

            for (int v = 0; v < 20; v++)
            {
                var chassis = ScriptableObject.CreateInstance<ShipChassisDefinition>();
                int num = v + 1;
                chassis.chassisId = family + "_" + num.ToString("00");
                chassis.shipFamily = family;
                chassis.displayName = family + " " + num;
                chassis.originPlanetId = 0;
                int minLevel = minLevelByShipIndex[v];
                chassis.minHomePlanetLevel = minLevel;

                entries.Add(new ShipUnlockEntry
                {
                    chassis = chassis,
                    minHomePlanetLevel = minLevel,
                    gemCost = 0f
                });
            }
        }

        /// <summary>
        /// Returns entries for the given planet's ship family. Uses ShipFamilyDefinition when the family entry has one, else legacy prefab list.
        /// </summary>
        public List<ShipUnlockEntry> GetUnlockedEntriesForPlanet(int homePlanetLevel, int planetId)
        {
            // Home planet: prefer ShipFamilyDefinition on table when available
            if (planetId == 0 && homeShipFamilyDefinition != null)
            {
                return GetUnlockedEntriesFromShipFamily(homePlanetLevel, homeShipFamilyDefinition, planetId);
            }

            var result = new List<ShipUnlockEntry>();
            if (planetShipFamilyConfig == null || planetShipFamilyConfig.families == null || planetShipFamilyConfig.families.Count == 0)
            {
                return GetUnlockedEntries(homePlanetLevel);
            }

            var family = planetShipFamilyConfig.GetFamilyForPlanet(planetId);
            if (family == null) return result;

            if (family.shipFamilyDefinition == null) return result;

            return GetUnlockedEntriesFromShipFamily(homePlanetLevel, family.shipFamilyDefinition, planetId);
        }

        private List<ShipUnlockEntry> GetUnlockedEntriesFromShipFamily(int homePlanetLevel, ShipFamilyDefinition familyDef, int planetId)
        {
            var result = new List<ShipUnlockEntry>();
            if (familyDef == null || familyDef.upgradeTree == null || familyDef.upgradeTree.Count == 0)
                return result;

            foreach (var tier in familyDef.upgradeTree)
            {
                if (tier == null) continue;
                if (homePlanetLevel < tier.minHomePlanetLevel) continue;

                var chassis = ScriptableObject.CreateInstance<ShipChassisDefinition>();
                chassis.chassisId = tier.chassisId;
                chassis.shipFamily = familyDef.familyId;
                chassis.displayName = tier.chassisId;
                chassis.basePrefab = tier.prefab;
                chassis.originPlanetId = planetId;
                chassis.minHomePlanetLevel = tier.minHomePlanetLevel;

                var entry = new ShipUnlockEntry
                {
                    chassis = chassis,
                    minHomePlanetLevel = tier.minHomePlanetLevel,
                    gemCost = ShipFamilyPowerScoreBreakdown.GetPurchaseGemCost(tier, tier.minHomePlanetLevel)
                };
                result.Add(entry);
            }

            return result;
        }

        /// <summary>
        /// Returns all chassis that are unlocked at the given home planet level.
        /// When homeShipFamilyDefinition is set, returns chassis from its upgrade tree (for home planet).
        /// </summary>
        public List<ShipChassisDefinition> GetUnlockedChassis(int homePlanetLevel)
        {
            if (homeShipFamilyDefinition != null)
            {
                var entries = GetUnlockedEntriesFromShipFamily(homePlanetLevel, homeShipFamilyDefinition, 0);
                var result = new List<ShipChassisDefinition>();
                foreach (var e in entries)
                {
                    if (e?.chassis != null) result.Add(e.chassis);
                }
                return result;
            }
            var fallback = new List<ShipChassisDefinition>();
            foreach (var entry in GetUnlockedEntries(homePlanetLevel))
            {
                if (entry?.chassis != null) fallback.Add(entry.chassis);
            }
            return fallback;
        }

        /// <summary>
        /// Gem cost for a chassis at the given ship level (2× gem cap, L1→L6 gradient).
        /// </summary>
        public static int GetPurchaseGemCost(ShipFamilyChassisTierEntry tier, int shipLevel)
        {
            return ShipFamilyPowerScoreBreakdown.GetPurchaseGemCost(tier, shipLevel);
        }

        /// <summary>First chassis index for a given tier (0-based). Tier 1=0, 2=1, 3=3, 4=6, 5=10, 6=15.</summary>
        public static int GetFirstChassisIndexForTier(int tier)
        {
            if (tier <= 1) return 0;
            return (tier * (tier - 1)) / 2;
        }

        /// <summary>Returns the chassis at the given index in the entries list, or null if out of range.
        /// When homeShipFamilyDefinition is set, resolves from its upgrade tree (so prefabs match the family asset).
        /// When only PlanetShipFamilyConfig is set, resolves home planet (index 0) from the config's ShipFamilyDefinition.</summary>
        public ShipChassisDefinition GetChassisByIndex(int index)
        {
            if (index < 0) return null;

            if (homeShipFamilyDefinition != null && homeShipFamilyDefinition.upgradeTree != null
                && index < homeShipFamilyDefinition.upgradeTree.Count)
            {
                var tier = homeShipFamilyDefinition.upgradeTree[index];
                if (tier != null)
                {
                    var chassis = ScriptableObject.CreateInstance<ShipChassisDefinition>();
                    chassis.chassisId = tier.chassisId;
                    chassis.shipFamily = homeShipFamilyDefinition.familyId;
                    chassis.displayName = tier.chassisId;
                    chassis.basePrefab = tier.prefab;
                    chassis.originPlanetId = 0;
                    chassis.minHomePlanetLevel = tier.minHomePlanetLevel;
                    return chassis;
                }
            }

            // Home planet from config when homeShipFamilyDefinition is not set
            if (planetShipFamilyConfig != null)
            {
                var family = planetShipFamilyConfig.GetFamilyForPlanet(0);
                if (family?.shipFamilyDefinition?.upgradeTree != null && index < family.shipFamilyDefinition.upgradeTree.Count)
                {
                    var tier = family.shipFamilyDefinition.upgradeTree[index];
                    if (tier != null)
                    {
                        var chassis = ScriptableObject.CreateInstance<ShipChassisDefinition>();
                        chassis.chassisId = tier.chassisId;
                        chassis.shipFamily = family.shipFamilyDefinition.familyId;
                        chassis.displayName = tier.chassisId;
                        chassis.basePrefab = tier.prefab;
                        chassis.originPlanetId = 0;
                        chassis.minHomePlanetLevel = tier.minHomePlanetLevel;
                        return chassis;
                    }
                }
            }

            if (entries == null || index >= entries.Count) return null;
            return entries[index]?.chassis;
        }

        /// <summary>Returns the chassis for the given chassis ID. When homeShipFamilyDefinition is set, resolves from its upgrade tree first. Else checks each planet family's ShipFamilyDefinition.</summary>
        public ShipChassisDefinition GetChassisByChassisId(string chassisId)
        {
            if (string.IsNullOrEmpty(chassisId)) return null;
            if (homeShipFamilyDefinition != null && homeShipFamilyDefinition.upgradeTree != null)
            {
                for (int i = 0; i < homeShipFamilyDefinition.upgradeTree.Count; i++)
                {
                    var tier = homeShipFamilyDefinition.upgradeTree[i];
                    if (tier != null && tier.chassisId == chassisId)
                    {
                        var chassis = ScriptableObject.CreateInstance<ShipChassisDefinition>();
                        chassis.chassisId = tier.chassisId;
                        chassis.shipFamily = homeShipFamilyDefinition.familyId;
                        chassis.displayName = tier.chassisId;
                        chassis.basePrefab = tier.prefab;
                        chassis.originPlanetId = 0;
                        chassis.minHomePlanetLevel = tier.minHomePlanetLevel;
                        return chassis;
                    }
                }
            }
            if (planetShipFamilyConfig != null && planetShipFamilyConfig.families != null)
            {
                foreach (var f in planetShipFamilyConfig.families)
                {
                    if (f?.shipFamilyDefinition == null || f.shipFamilyDefinition.upgradeTree == null) continue;
                    var def = f.shipFamilyDefinition;
                    for (int i = 0; i < def.upgradeTree.Count; i++)
                    {
                        var tier = def.upgradeTree[i];
                        if (tier != null && tier.chassisId == chassisId)
                        {
                            var chassis = ScriptableObject.CreateInstance<ShipChassisDefinition>();
                            chassis.chassisId = tier.chassisId;
                            chassis.shipFamily = def.familyId;
                            chassis.displayName = tier.chassisId;
                            chassis.basePrefab = tier.prefab;
                            chassis.originPlanetId = f.planetId;
                            chassis.minHomePlanetLevel = tier.minHomePlanetLevel;
                            return chassis;
                        }
                    }
                }
            }
            if (entries == null) return null;
            foreach (var entry in entries)
            {
                if (entry?.chassis != null && entry.chassis.chassisId == chassisId)
                    return entry.chassis;
            }
            return null;
        }

        /// <summary>Returns the ship prefab for the given chassis ID. Uses homeShipFamilyDefinition upgrade tree for home, else PlanetShipFamilyConfig.</summary>
        public GameObject GetPrefabForChassisId(string chassisId)
        {
            if (string.IsNullOrEmpty(chassisId)) return null;
            if (homeShipFamilyDefinition != null && homeShipFamilyDefinition.upgradeTree != null)
            {
                foreach (var tier in homeShipFamilyDefinition.upgradeTree)
                {
                    if (tier != null && tier.chassisId == chassisId && tier.prefab != null)
                        return tier.prefab;
                }
            }
            if (planetShipFamilyConfig != null)
            {
                GameObject prefab = planetShipFamilyConfig.GetPrefabByChassisId(chassisId);
                if (prefab != null) return prefab;
            }
            return null;
        }

        /// <summary>Returns the index of the entry whose chassis has the given chassisId, or -1 if not found.
        /// When homeShipFamilyDefinition is set, resolves from its upgrade tree first. Else tries PlanetShipFamilyConfig home planet.</summary>
        public int GetIndexForChassisId(string chassisId)
        {
            if (string.IsNullOrEmpty(chassisId)) return -1;
            if (homeShipFamilyDefinition != null && homeShipFamilyDefinition.upgradeTree != null)
            {
                for (int i = 0; i < homeShipFamilyDefinition.upgradeTree.Count; i++)
                {
                    var tier = homeShipFamilyDefinition.upgradeTree[i];
                    if (tier != null && tier.chassisId == chassisId)
                        return i;
                }
            }
            if (planetShipFamilyConfig != null)
            {
                var family = planetShipFamilyConfig.GetFamilyForPlanet(0);
                if (family?.shipFamilyDefinition?.upgradeTree != null)
                {
                    for (int i = 0; i < family.shipFamilyDefinition.upgradeTree.Count; i++)
                    {
                        var tier = family.shipFamilyDefinition.upgradeTree[i];
                        if (tier != null && tier.chassisId == chassisId)
                            return i;
                    }
                }
            }
            if (entries == null) return -1;
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i]?.chassis != null && entries[i].chassis.chassisId == chassisId)
                    return i;
            }
            return -1;
        }

        /// <summary>Returns the chassis index for the given chassisId within the given planet's unlock list (from that planet's ShipFamilyDefinition upgrade tree when set).</summary>
        public int GetIndexForChassisIdForPlanet(string chassisId, int planetId)
        {
            if (string.IsNullOrEmpty(chassisId)) return -1;
            if (planetId == 0 && homeShipFamilyDefinition != null && homeShipFamilyDefinition.upgradeTree != null)
            {
                for (int i = 0; i < homeShipFamilyDefinition.upgradeTree.Count; i++)
                {
                    if (homeShipFamilyDefinition.upgradeTree[i] != null && homeShipFamilyDefinition.upgradeTree[i].chassisId == chassisId)
                        return i;
                }
                return -1;
            }
            if (planetShipFamilyConfig == null || planetShipFamilyConfig.families == null) return -1;
            var family = planetShipFamilyConfig.GetFamilyForPlanet(planetId);
            if (family?.shipFamilyDefinition?.upgradeTree == null) return -1;
            var tree = family.shipFamilyDefinition.upgradeTree;
            for (int i = 0; i < tree.Count; i++)
            {
                if (tree[i] != null && tree[i].chassisId == chassisId)
                    return i;
            }
            return -1;
        }
    }
}

