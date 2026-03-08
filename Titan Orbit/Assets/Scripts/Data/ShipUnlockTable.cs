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

        [Tooltip("Gem cost to purchase this chassis at its tier. Uses formula 20 * Level^2 by default.")]
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
        /// Returns entries for the given planet's ship family (when planetShipFamilyConfig is set). Same tier progression (1-20 ships, levels 1-6).
        /// </summary>
        public List<ShipUnlockEntry> GetUnlockedEntriesForPlanet(int homePlanetLevel, int planetId)
        {
            var result = new List<ShipUnlockEntry>();
            if (planetShipFamilyConfig == null || planetShipFamilyConfig.families == null || planetShipFamilyConfig.families.Count == 0)
            {
                return GetUnlockedEntries(homePlanetLevel);
            }
            var family = planetShipFamilyConfig.GetFamilyForPlanet(planetId);
            if (family == null || string.IsNullOrEmpty(family.familyName)) return result;

            int[] minLevelByShipIndex = { 1, 2, 2, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 5, 6, 6, 6, 6, 6 };
            int count = family.prefabs != null ? Mathf.Min(20, family.prefabs.Length) : 20;
            for (int v = 0; v < count; v++)
            {
                int minLevel = minLevelByShipIndex[Mathf.Min(v, minLevelByShipIndex.Length - 1)];
                if (homePlanetLevel < minLevel) continue;
                var chassis = ScriptableObject.CreateInstance<ShipChassisDefinition>();
                int num = v + 1;
                chassis.chassisId = $"{family.familyName}_{num:D2}";
                chassis.shipFamily = family.familyName;
                chassis.displayName = $"{family.familyName} {num}";
                chassis.originPlanetId = planetId;
                chassis.minHomePlanetLevel = minLevel;
                result.Add(new ShipUnlockEntry { chassis = chassis, minHomePlanetLevel = minLevel, gemCost = 0f });
            }
            return result;
        }

        /// <summary>
        /// Returns all chassis that are unlocked at the given home planet level.
        /// </summary>
        public List<ShipChassisDefinition> GetUnlockedChassis(int homePlanetLevel)
        {
            var result = new List<ShipChassisDefinition>();
            foreach (var entry in GetUnlockedEntries(homePlanetLevel))
            {
                if (entry?.chassis != null) result.Add(entry.chassis);
            }
            return result;
        }

        /// <summary>
        /// Utility to compute the gem cost for a tier level using the agreed formula: 20 * Level^2.
        /// </summary>
        public static float GetTierCost(int level)
        {
            if (level <= 0) level = 1;
            return 20f * level * level;
        }

        /// <summary>First chassis index for a given tier (0-based). Tier 1=0, 2=1, 3=3, 4=6, 5=10, 6=15.</summary>
        public static int GetFirstChassisIndexForTier(int tier)
        {
            if (tier <= 1) return 0;
            return (tier * (tier - 1)) / 2;
        }

        /// <summary>Returns the chassis at the given index in the entries list, or null if out of range.</summary>
        public ShipChassisDefinition GetChassisByIndex(int index)
        {
            if (entries == null || index < 0 || index >= entries.Count) return null;
            return entries[index]?.chassis;
        }

        /// <summary>Returns the index of the entry whose chassis has the given chassisId, or -1 if not found.</summary>
        public int GetIndexForChassisId(string chassisId)
        {
            if (entries == null || string.IsNullOrEmpty(chassisId)) return -1;
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i]?.chassis != null && entries[i].chassis.chassisId == chassisId)
                    return i;
            }
            return -1;
        }
    }
}

