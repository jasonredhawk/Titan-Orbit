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
        [Tooltip("All chassis that can be purchased, with their minimum home planet levels and gem costs.")]
        public List<ShipUnlockEntry> entries = new List<ShipUnlockEntry>();

        /// <summary>
        /// Returns all entries that are unlocked at the given home planet level (for UI: show tier + cost per entry).
        /// If the table has no entries, populates default 20 AstroEagle variants (tiers 1–6) so the Ships tab always has options.
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
            return result;
        }

        /// <summary>
        /// When the table is empty, creates 20 AstroEagle chassis (versions 1–20) with one entry each.
        /// Tiers 1–5: 3 ships each (ships 1–15). Tier 6: 5 ships (ships 16–20). Total 20 ships.
        /// Home level 1 shows 3 ships; level 2 shows 6; … level 5 shows 15; level 6 shows all 20 (5 new at tier 6).
        /// </summary>
        private void EnsureDefaultAstroEagleEntries()
        {
            if (entries == null) entries = new List<ShipUnlockEntry>();
            if (entries.Count > 0) return;

            const string family = "AstroEagle";
            // Tier 1 = ships 1-3, tier 2 = 4-6, tier 3 = 7-9, tier 4 = 10-12, tier 5 = 13-15, tier 6 = 16-20
            int[] tierByShipIndex = { 1, 1, 1, 2, 2, 2, 3, 3, 3, 4, 4, 4, 5, 5, 5, 6, 6, 6, 6, 6 };

            for (int v = 0; v < 20; v++)
            {
                var chassis = ScriptableObject.CreateInstance<ShipChassisDefinition>();
                int num = v + 1;
                chassis.chassisId = family + "_" + num.ToString("00");
                chassis.shipFamily = family;
                chassis.displayName = family + " " + num;
                chassis.originPlanetId = 0;
                chassis.minHomePlanetLevel = 1;

                int tier = tierByShipIndex[v];
                entries.Add(new ShipUnlockEntry
                {
                    chassis = chassis,
                    minHomePlanetLevel = tier,
                    gemCost = 0f
                });
            }
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

