using System;
using System.Collections.Generic;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Maps each planet to a ship family. Prefabs and unlock tiers come from each entry's ShipFamilyDefinition upgradeTree.
    /// </summary>
    [CreateAssetMenu(fileName = "PlanetShipFamilyConfig", menuName = "Titan Orbit/Planet Ship Family Config")]
    public class PlanetShipFamilyConfig : ScriptableObject
    {
        [Serializable]
        public class ShipFamilyEntry
        {
            [Tooltip("Planet ID this family is for. 0 = home. 1, 2, 3... = captured planets.")]
            public int planetId;

            [Tooltip("Ship family definition. Prefabs and unlock tiers come from its upgradeTree.")]
            public ShipFamilyDefinition shipFamilyDefinition;

            [Tooltip("Optional display name; when empty, familyId from shipFamilyDefinition is used.")]
            public string familyName;
        }

        [Tooltip("Ordered list: index 0 = home planet family, index 1 = planet 1, etc.")]
        public List<ShipFamilyEntry> families = new List<ShipFamilyEntry>();

        /// <summary>Gets the family entry for the given planet.</summary>
        public ShipFamilyEntry GetFamilyForPlanet(int planetId)
        {
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

        /// <summary>Gets the ship prefab for chassisId and planet. Resolves from the entry's ShipFamilyDefinition upgradeTree.</summary>
        public GameObject GetPrefabForChassisAndPlanet(string chassisId, int planetId)
        {
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

        /// <summary>Gets chassis ID for the given planet and ship index (0-based). Uses the entry's ShipFamilyDefinition upgradeTree.</summary>
        public string GetChassisIdForPlanetAndIndex(int planetId, int index)
        {
            ShipFamilyEntry family = GetFamilyForPlanet(planetId);
            if (family?.shipFamilyDefinition?.upgradeTree == null) return null;
            if (index < 0 || index >= family.shipFamilyDefinition.upgradeTree.Count) return null;

            var tier = family.shipFamilyDefinition.upgradeTree[index];
            if (tier != null) return tier.chassisId;
            return null;
        }

        /// <summary>Gets the chassis at the given index for the planet (from that planet's ShipFamilyDefinition upgrade tree).</summary>
        public ShipChassisDefinition GetChassisByIndex(int planetId, int index)
        {
            if (index < 0) return null;
            ShipFamilyEntry family = GetFamilyForPlanet(planetId);
            if (family?.shipFamilyDefinition?.upgradeTree == null) return null;
            if (index >= family.shipFamilyDefinition.upgradeTree.Count) return null;

            var tier = family.shipFamilyDefinition.upgradeTree[index];
            if (tier == null) return null;

            var chassis = ScriptableObject.CreateInstance<ShipChassisDefinition>();
            chassis.chassisId = tier.chassisId;
            chassis.shipFamily = family.shipFamilyDefinition.familyId;
            chassis.displayName = tier.chassisId;
            chassis.basePrefab = tier.prefab;
            chassis.originPlanetId = planetId;
            chassis.minHomePlanetLevel = tier.minHomePlanetLevel;
            return chassis;
        }

        /// <summary>Gets the index of the chassis in the given planet's upgrade tree, or -1.</summary>
        public int GetIndexForChassisIdForPlanet(string chassisId, int planetId)
        {
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
                        chassis.displayName = tier.chassisId;
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
                chassis.displayName = tier.chassisId;
                chassis.basePrefab = tier.prefab;
                chassis.originPlanetId = planetId;
                chassis.minHomePlanetLevel = tier.minHomePlanetLevel;

                result.Add(new ShipUnlockEntry
                {
                    chassis = chassis,
                    minHomePlanetLevel = tier.minHomePlanetLevel,
                    gemCost = ShipUnlockTable.GetTierCost(tier.minHomePlanetLevel)
                });
            }
            return result;
        }

        /// <summary>Returns all chassis unlocked at the given home planet level for the home planet (planet 0).</summary>
        public List<ShipChassisDefinition> GetUnlockedChassis(int homePlanetLevel)
        {
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
