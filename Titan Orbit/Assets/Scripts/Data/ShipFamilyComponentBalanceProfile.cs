using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace TitanOrbit.Data
{
    [Serializable]
    public class ShipFamilyComponentBalanceRule
    {
        [Tooltip("Canonical component id after normalization (e.g. EngineComp1).")]
        public string componentId;

        [Tooltip("Inferred part type used for fallback balancing (Weapon, Wing, Engine, Thruster, Cockpit, etc.).")]
        public string partType;

        [Tooltip("Authoritative stat template for this canonical component id.")]
        public ShipComponentAbilityStats stats;

        [Tooltip("Optional aliases collapsed into this canonical id (e.g. EngineComp1 (1), EngineComp1_Mirrored).")]
        public List<string> aliases = new List<string>();
    }

    [Serializable]
    public class ShipFamilyPartTypeBalanceRule
    {
        [Tooltip("Part type key (Weapon, Wing, Engine, Thruster, Cockpit, Hull, Fin, Part, Utility, Other).")]
        public string partType;

        [Tooltip("Default stats for this part type when no exact component rule exists.")]
        public ShipComponentAbilityStats stats;
    }

    [CreateAssetMenu(fileName = "ShipComponentBalanceProfile", menuName = "Titan Orbit/Ship Family Component Balance Profile")]
    public class ShipFamilyComponentBalanceProfile : ScriptableObject
    {
        [Tooltip("Optional label for this profile.")]
        public string profileId;

        [Tooltip("Exact canonical component-id rules.")]
        public List<ShipFamilyComponentBalanceRule> componentRules = new List<ShipFamilyComponentBalanceRule>();

        [Tooltip("Part-type fallback rules.")]
        public List<ShipFamilyPartTypeBalanceRule> partTypeRules = new List<ShipFamilyPartTypeBalanceRule>();

        [Tooltip("Used when neither component nor part-type rule exists.")]
        public ShipComponentAbilityStats defaultFallbackStats;

        private static readonly Regex CloneSuffixRegex = new Regex(@"\s+\(\d+\)$", RegexOptions.Compiled);

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

        public bool TryGetStats(string componentIdRaw, string partType, out ShipComponentAbilityStats stats)
        {
            string componentId = NormalizeComponentId(componentIdRaw);
            if (!string.IsNullOrEmpty(componentId) && componentRules != null)
            {
                for (int i = 0; i < componentRules.Count; i++)
                {
                    var r = componentRules[i];
                    if (r == null || string.IsNullOrWhiteSpace(r.componentId))
                        continue;
                    if (string.Equals(NormalizeComponentId(r.componentId), componentId, StringComparison.OrdinalIgnoreCase))
                    {
                        stats = r.stats;
                        return true;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(partType) && partTypeRules != null)
            {
                for (int i = 0; i < partTypeRules.Count; i++)
                {
                    var r = partTypeRules[i];
                    if (r == null || string.IsNullOrWhiteSpace(r.partType))
                        continue;
                    if (string.Equals(r.partType.Trim(), partType.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        stats = r.stats;
                        return true;
                    }
                }
            }

            stats = defaultFallbackStats;
            return false;
        }
    }
}
