using System;
using System.Collections.Generic;
using UnityEngine;
using TitanOrbit.Data;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Attach to a ship prefab (optionally alongside Starship) to preview the summed ability stats
    /// of all child components whose names follow the pattern Family_ComponentId (e.g. AstroEagle_Cockpit).
    /// Stats are looked up from a ShipFamilyDefinition ScriptableObject.
    /// </summary>
    [ExecuteAlways]
    public class ShipFamilyStatsPreview : MonoBehaviour
    {
        [Header("Config")]
        [Tooltip("Ship family definition asset describing all component stats for this family.")]
        [SerializeField] private ShipFamilyDefinition shipFamily;

        [Tooltip("If empty, uses shipFamily.familyId. Override to preview with a different family id prefix.")]
        [SerializeField] private string familyIdOverride;

        [Header("Aggregated Stats (read-only)")]
        [SerializeField] private ShipComponentAbilityStats totalStats;

        [Tooltip("Optional: the unique component ids found under this prefab, for debugging.")]
        [SerializeField] private List<string> matchedComponentIds = new List<string>();

        public ShipComponentAbilityStats TotalStats => totalStats;
        public IReadOnlyList<string> MatchedComponentIds => matchedComponentIds;

        private void OnEnable()
        {
            RecalculateFromChildren();
        }

        private void OnTransformChildrenChanged()
        {
            RecalculateFromChildren();
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            // Keep preview up-to-date when editing in inspector
            RecalculateFromChildren();
        }

        private void Update()
        {
            if (!Application.isPlaying)
            {
                // In edit mode, keep preview in sync with rename/move operations.
                RecalculateFromChildren();
            }
        }
#endif

        /// <summary>
        /// Scan all child transforms, parse names of the form Family_ComponentId, and sum their stats.
        /// </summary>
        public void RecalculateFromChildren()
        {
            totalStats = default;
            matchedComponentIds.Clear();

            if (shipFamily == null)
                return;

            string familyId = !string.IsNullOrWhiteSpace(familyIdOverride)
                ? familyIdOverride.Trim()
                : shipFamily.familyId != null ? shipFamily.familyId.Trim() : string.Empty;

            if (string.IsNullOrEmpty(familyId))
                return;

            var transforms = GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < transforms.Length; i++)
            {
                var t = transforms[i];
                if (t == null || t == transform) continue;

                string name = t.name;
                if (string.IsNullOrEmpty(name)) continue;

                // Expect "Family_ComponentId"
                if (!name.StartsWith(familyId + "_", StringComparison.OrdinalIgnoreCase))
                    continue;

                string componentId = name.Substring(familyId.Length + 1);
                if (string.IsNullOrWhiteSpace(componentId))
                    continue;

                if (shipFamily.TryGetStatsForComponent(componentId, out var stats))
                {
                    totalStats.AddInPlace(stats);
                    if (!matchedComponentIds.Contains(componentId))
                        matchedComponentIds.Add(componentId);
                }
            }
        }
    }
}

