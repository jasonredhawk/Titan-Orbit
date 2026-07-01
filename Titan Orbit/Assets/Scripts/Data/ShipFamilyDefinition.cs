using System;
using System.Collections.Generic;
using TitanOrbit.Core;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Runtime subset of the ship-family ScriptableObject used for chassis prefabs and team materials.
    /// Full stat/upgrade-tree authoring data lives in the asset YAML; only visual helpers are implemented here.
    /// </summary>
    [CreateAssetMenu(fileName = "ShipFamily", menuName = "Titan Orbit/Ship Family Definition")]
    public class ShipFamilyDefinition : ScriptableObject
    {
        public string familyId;

        [Header("Upgrade Tree")]
        public List<ShipFamilyChassisTierEntry> upgradeTree = new List<ShipFamilyChassisTierEntry>();

        [Header("Team Materials")]
        public List<ShipFamilyTeamMaterialSet> teamMaterials = new List<ShipFamilyTeamMaterialSet>();

        public List<Material> GetMaterialsForTeam(TeamId team)
        {
            if (teamMaterials == null || teamMaterials.Count == 0)
                return null;

            for (int i = 0; i < teamMaterials.Count; i++)
            {
                var set = teamMaterials[i];
                if (set == null || set.materials == null || set.materials.Count == 0)
                    continue;
                if (set.team == team)
                    return set.materials;
            }

            return null;
        }

        /// <summary>
        /// Picks the best chassis prefab for a ship level (starter lock-in, then highest tier unlocked).
        /// </summary>
        public bool TryGetVisualPrefabForLevel(int shipLevel, out GameObject prefab)
        {
            prefab = null;
            if (upgradeTree == null || upgradeTree.Count == 0)
                return false;

            shipLevel = Mathf.Max(1, shipLevel);

            for (int i = 0; i < upgradeTree.Count; i++)
            {
                var tier = upgradeTree[i];
                if (tier?.prefab == null)
                    continue;
                if (tier.lockedInUpgradeTree && tier.minHomePlanetLevel == shipLevel)
                {
                    prefab = tier.prefab;
                    return true;
                }
            }

            ShipFamilyChassisTierEntry best = null;
            for (int i = 0; i < upgradeTree.Count; i++)
            {
                var tier = upgradeTree[i];
                if (tier?.prefab == null)
                    continue;
                if (tier.minHomePlanetLevel > shipLevel)
                    continue;
                if (best == null || tier.minHomePlanetLevel > best.minHomePlanetLevel)
                    best = tier;
            }

            if (best != null)
            {
                prefab = best.prefab;
                return true;
            }

            for (int i = 0; i < upgradeTree.Count; i++)
            {
                if (upgradeTree[i]?.prefab != null)
                {
                    prefab = upgradeTree[i].prefab;
                    return true;
                }
            }

            return false;
        }
    }

    [Serializable]
    public class ShipFamilyChassisTierEntry
    {
        public string chassisId;
        public string upgradeTreeShipName;
        public GameObject prefab;
        public int minHomePlanetLevel = 1;
        public bool lockedInUpgradeTree;
    }

    [Serializable]
    public class ShipFamilyTeamMaterialSet
    {
        public string variantName;
        public TeamId team = TeamId.None;
        public List<Material> materials = new List<Material>();
    }
}
