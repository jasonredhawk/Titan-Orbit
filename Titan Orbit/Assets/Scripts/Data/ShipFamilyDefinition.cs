using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using TitanOrbit.Core;
using UnityEngine;

namespace TitanOrbit.Data
{
    public static class ShipFamilyDefaultFallbackStats
    {
        public static ShipComponentAbilityStats CreateBaseline()
        {
            return new ShipComponentAbilityStats
            {
                firePower = 3f,
                firePowerPerLevel = 0.75f,
                bulletSpeed = 12f,
                bulletSpeedPerLevel = 3f,
                fireRate = 3f,
                fireRatePerLevel = 0f,
                rammingPower = 1f,
                rammingPowerPerLevel = 0.25f,
                healthCap = 6.3f,
                healthCapPerLevel = 1.575f,
                healthRegen = 0.225f,
                healthRegenPerLevel = 0.05625f,
                energyCap = 18f,
                energyCapPerLevel = 4.5f,
                energyRegen = 3f,
                energyRegenPerLevel = 0.75f,
                moveSpeed = 9f,
                moveSpeedPerLevel = 1.8f,
                accelerationCap = 2.4f,
                accelerationCapPerLevel = 0.6f,
                turnSpeed = 14f,
                turnSpeedPerLevel = 3.5f,
                maxGems = 8f,
                maxGemsPerLevel = 2f,
                tractorBeamDistance = 3f,
                tractorBeamDistancePerLevel = 0.75f,
                tractorBeamPower = 4f,
                tractorBeamPowerPerLevel = 1f,
                maxPeople = 2f,
                maxPeoplePerLevel = 0f,
            };
        }
    }

    /// <summary>
    /// Ship family definition: component stat modifiers, upgrade tree, and visual helpers.
    /// </summary>
    [CreateAssetMenu(fileName = "ShipFamily", menuName = "Titan Orbit/Ship Family Definition")]
    public class ShipFamilyDefinition : ScriptableObject
    {
        static readonly Regex CloneSuffixRegex = new Regex(@"\(Clone\)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex PropulsionIdUnderscoreFormRegex = new Regex(@"^(Engine|Thruster)_(\d+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex PropulsionIdCompactFormRegex = new Regex(@"^(Engine|Thruster)(\d+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public string familyId;

        [Header("Default Stat Fallbacks")]
        public ShipComponentAbilityStats defaultFallbackStats;

        [Header("Bullets")]
        public int bulletPrefabIndex = 0;

        [Header("Components")]
        public List<ShipFamilyComponentEntry> components = new List<ShipFamilyComponentEntry>();

        [Header("Upgrade Tree")]
        public List<ShipFamilyChassisTierEntry> upgradeTree = new List<ShipFamilyChassisTierEntry>();

        [Header("Team Materials")]
        public List<ShipFamilyTeamMaterialSet> teamMaterials = new List<ShipFamilyTeamMaterialSet>();

        [NonSerialized] bool _lookupBuilt;
        [NonSerialized] readonly Dictionary<string, ShipComponentAbilityStats> _lookup = new Dictionary<string, ShipComponentAbilityStats>(StringComparer.OrdinalIgnoreCase);
        [NonSerialized] List<CardData> _runtimeProceduralCards;

        public ShipComponentAbilityStats GetEffectiveDefaultFallbackStats()
        {
            return ShipComponentAbilityStatsMath.IsAllZero(defaultFallbackStats)
                ? ShipFamilyDefaultFallbackStats.CreateBaseline()
                : defaultFallbackStats;
        }

        public ShipComponentAbilityStats ApplyStatFallbacks(ShipComponentAbilityStats summedStats)
        {
            return ShipComponentAbilityStatsMath.WithZeroStatFallbacks(summedStats, GetEffectiveDefaultFallbackStats());
        }

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

        public IReadOnlyList<CardData> GetUpgradeCards()
        {
            return _runtimeProceduralCards ??= CardDeckRuntimeDefaults.CreateProceduralDeck(familyId);
        }

        public void InvalidateComponentStatsLookup() => _lookupBuilt = false;

        public bool TryGetStatsForComponent(string componentId, out ShipComponentAbilityStats stats)
        {
            EnsureLookup();
            stats = default;
            if (string.IsNullOrWhiteSpace(componentId))
                return false;

            string raw = componentId.Trim();
            if (_lookup.TryGetValue(raw, out stats))
                return true;
            string canonical = NormalizeComponentId(raw);
            if (!string.IsNullOrEmpty(canonical) && _lookup.TryGetValue(canonical, out stats))
                return true;
            string alternate = GetAlternateComponentIdForm(raw);
            return !string.IsNullOrEmpty(alternate) && _lookup.TryGetValue(alternate, out stats);
        }

        public bool TryGetComponentEntry(string componentId, out ShipFamilyComponentEntry entry)
        {
            entry = null;
            if (components == null || string.IsNullOrWhiteSpace(componentId))
                return false;

            string id = componentId.Trim();
            string canonical = NormalizeComponentId(id);
            for (int i = 0; i < components.Count; i++)
            {
                if (components[i] == null) continue;
                string test = components[i].componentId?.Trim();
                if (string.Equals(test, id, StringComparison.OrdinalIgnoreCase))
                {
                    entry = components[i];
                    return true;
                }
                if (!string.IsNullOrEmpty(canonical) &&
                    string.Equals(NormalizeComponentId(test), canonical, StringComparison.OrdinalIgnoreCase))
                {
                    entry = components[i];
                    return true;
                }
            }

            return false;
        }

        public Sprite GetMenuPreviewSpriteForComponent(string componentId, TeamManager.Team team = TeamManager.Team.None)
        {
            return TryGetComponentEntry(componentId, out ShipFamilyComponentEntry entry) && entry != null
                ? entry.GetMenuPreviewSprite(team)
                : null;
        }

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

        public static string NormalizeComponentId(string rawId)
        {
            if (string.IsNullOrWhiteSpace(rawId))
                return string.Empty;

            string s = rawId.Trim();
            s = CloneSuffixRegex.Replace(s, string.Empty);
            if (s.EndsWith("_Mirrored", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(0, s.Length - "_Mirrored".Length);
            return s.Trim();
        }

        public static string GetAlternateComponentIdForm(string componentId)
        {
            if (string.IsNullOrWhiteSpace(componentId))
                return string.Empty;

            string s = NormalizeComponentId(componentId);
            Match underscored = PropulsionIdUnderscoreFormRegex.Match(s);
            if (underscored.Success)
                return underscored.Groups[1].Value + underscored.Groups[2].Value;

            Match compact = PropulsionIdCompactFormRegex.Match(s);
            if (compact.Success)
                return compact.Groups[1].Value + "_" + compact.Groups[2].Value;

            return string.Empty;
        }

        void EnsureLookup()
        {
            if (_lookupBuilt) return;
            _lookup.Clear();
            if (components != null)
            {
                foreach (var entry in components)
                {
                    if (entry == null || string.IsNullOrWhiteSpace(entry.componentId))
                        continue;
                    string raw = entry.componentId.Trim();
                    RegisterLookupKey(raw, entry.stats);
                    RegisterLookupKey(NormalizeComponentId(raw), entry.stats);
                    RegisterLookupKey(GetAlternateComponentIdForm(raw), entry.stats);
                }
            }

            _lookupBuilt = true;
        }

        void RegisterLookupKey(string key, ShipComponentAbilityStats stats)
        {
            if (string.IsNullOrWhiteSpace(key))
                return;
            _lookup[key.Trim()] = stats;
        }

        static float s_cachedGlobalMaxUpgradeTreeTurnSpeedAuthored = -1f;

        /// <summary>
        /// Highest level-1 turn speed among upgrade-tree chassis tiers
        /// (from <see cref="ShipFamilyChassisTierEntry.powerScoreBreakdown"/>).
        /// </summary>
        public static float GetMaxUpgradeTreeTurnSpeedAuthoredUnits(IEnumerable<ShipFamilyDefinition> families)
        {
            float max = 0f;
            if (families == null)
                return max;

            foreach (ShipFamilyDefinition def in families)
            {
                if (def?.upgradeTree == null)
                    continue;

                for (int i = 0; i < def.upgradeTree.Count; i++)
                {
                    ShipFamilyChassisTierEntry tier = def.upgradeTree[i];
                    if (tier == null)
                        continue;
                    max = Mathf.Max(max, tier.powerScoreBreakdown.turnSpeed);
                }
            }

            return max;
        }

        /// <summary>
        /// Cached global max turn speed (authored units, level 1) across every loaded ship family upgrade tree.
        /// </summary>
        public static float GetGlobalMaxUpgradeTreeTurnSpeedAuthoredUnits()
        {
            if (s_cachedGlobalMaxUpgradeTreeTurnSpeedAuthored >= 0f)
                return s_cachedGlobalMaxUpgradeTreeTurnSpeedAuthored;

            ShipFamilyDefinition[] all = Resources.FindObjectsOfTypeAll<ShipFamilyDefinition>();
            s_cachedGlobalMaxUpgradeTreeTurnSpeedAuthored = GetMaxUpgradeTreeTurnSpeedAuthoredUnits(all);

            return s_cachedGlobalMaxUpgradeTreeTurnSpeedAuthored > 0f
                ? s_cachedGlobalMaxUpgradeTreeTurnSpeedAuthored
                : ShipPropulsionAggregation.VisualBankReferenceMaxTurnSpeedAuthoredUnits;
        }

        /// <summary>Clears cached global max turn speed (call after upgrade-tree stat scans in the editor).</summary>
        public static void InvalidateGlobalMaxUpgradeTreeTurnSpeedCache()
        {
            s_cachedGlobalMaxUpgradeTreeTurnSpeedAuthored = -1f;
        }

#if UNITY_EDITOR
        void OnValidate()
        {
            InvalidateComponentStatsLookup();
            InvalidateGlobalMaxUpgradeTreeTurnSpeedCache();
            _runtimeProceduralCards = null;
        }
#endif
    }

    [Serializable]
    public class ShipFamilyChassisTierEntry
    {
        public string chassisId;
        public string upgradeTreeShipName;
        public GameObject prefab;
        public Sprite menuPreviewSprite;
        public List<ShipFamilyTeamMenuPreview> teamMenuPreviewSprites = new List<ShipFamilyTeamMenuPreview>();
        public int minHomePlanetLevel = 1;
        public float powerScore;
        public ShipFamilyPowerScoreBreakdown powerScoreBreakdown;
        public bool lockedInUpgradeTree;
    }

    [Serializable]
    public class ShipFamilyTeamMenuPreview
    {
        public string variantName;
        public TeamManager.Team team = TeamManager.Team.None;
        public Sprite sprite;
    }

    [Serializable]
    public class ShipFamilyTeamMaterialSet
    {
        public string variantName;
        public TeamId team = TeamId.None;
        public List<Material> materials = new List<Material>();
    }
}
