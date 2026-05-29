using System;
using System.Collections.Generic;
using UnityEngine;
using TitanOrbit.Data;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// All starship prefabs should have this component on the root. Starship.cs reads it at runtime to get
    /// summed ability stats (health, energy, fire power, etc.) from the ShipFamilyDefinition and component scales.
    /// Engines and thrusters share one propulsion pool: top speed uses one base move speed (best engine/thruster)
    /// plus half the sum of each additional engine/thruster's moveSpeedPerLevel; acceleration caps sum across all propulsion parts.
    /// Attach to the prefab root; assign Ship Family to the matching ShipFamilyDefinition (e.g. AstroEagle).
    /// Child names must follow Family_ComponentId (e.g. AstroEagle_Cockpit, AstroEagle_Weapon_1).
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

        [Tooltip("Optional: component ids found under this prefab (one per matched transform).")]
        [SerializeField] private List<string> matchedComponentIds = new List<string>();

        [Tooltip("Average scale (x+y+z)/3 per matched component; same order as Matched Component Ids.")]
        [SerializeField] private List<float> matchedScaleFactors = new List<float>();

        [Tooltip("Scaled stats per matched component; same order as Matched Component Ids.")]
        [SerializeField] private List<ShipComponentAbilityStats> perComponentStats = new List<ShipComponentAbilityStats>();

        [Header("Propulsion preview (thrusters only, ship level 1 base)")]
        [Tooltip("Sum of thruster Acceleration Cap — matches Starship thrust numerator (stacked before F/m).")]
        [SerializeField] private float previewSumPropulsionAcceleration;
        [Tooltip("Sum of thruster Acceleration Cap / Level (per-level terms, ship level 1 adds 0).")]
        [SerializeField] private float previewSumPropulsionAccelerationPerLevel;
        [Tooltip("Extra top speed from non-primary engines/thrusters: half the sum of their moveSpeedPerLevel.")]
        [SerializeField] private float previewExtraThrusterMoveSpeed;
        [Tooltip("Primary thruster base move speed — first term in top speed cap.")]
        [SerializeField] private float previewPrimaryThrusterMoveSpeed;
        [Tooltip("Top speed cap: primary thruster move speed + extra thruster moveSpeedPerLevel terms.")]
        [SerializeField] private float previewTopSpeedMoveSpeed;

        public ShipComponentAbilityStats TotalStats => totalStats;
        public IReadOnlyList<string> MatchedComponentIds => matchedComponentIds;
        public IReadOnlyList<float> MatchedScaleFactors => matchedScaleFactors;
        public IReadOnlyList<ShipComponentAbilityStats> PerComponentStats => perComponentStats;
        /// <summary>Sum of thruster <see cref="ShipComponentAbilityStats.accelerationCap"/> (level 1). Matches Starship propulsion thrust stacking.</summary>
        public float PreviewSumPropulsionAcceleration => previewSumPropulsionAcceleration;
        /// <summary>Sum of thruster <see cref="ShipComponentAbilityStats.accelerationCapPerLevel"/>.</summary>
        public float PreviewSumPropulsionAccelerationPerLevel => previewSumPropulsionAccelerationPerLevel;
        /// <summary>Effective extra top speed from non-primary propulsion parts (half summed moveSpeedPerLevel).</summary>
        public float PreviewExtraThrusterMoveSpeed => previewExtraThrusterMoveSpeed;
        /// <summary>Primary thruster base move speed before extras.</summary>
        public float PreviewPrimaryThrusterMoveSpeed => previewPrimaryThrusterMoveSpeed;
        /// <summary>Top speed cap from thruster aggregation (matches speedometer max).</summary>
        public float PreviewTopSpeedMoveSpeed => previewTopSpeedMoveSpeed;
        /// <summary>Ship family definition used for stats (so runtime can apply the same stats from prefab).</summary>
        public ShipFamilyDefinition ShipFamily => shipFamily;

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
#endif

        /// <summary>
        /// Scan all child transforms, parse names of the form Family_ComponentId, and sum their stats.
        /// Each component's contribution is scaled by transform: non-weapons use average scale (x+y+z)/3 for most stats;
        /// engine/thruster move speed, acceleration cap, and turn speed use authored values only (see <see cref="ShipComponentAbilityStats.ScaleStatsByTransform"/>).
        /// Weapons: fire power scales by average(x,y); fire rate scales by 1/z (smaller z = faster); bullet speed is not scaled by transform.
        /// Propulsion preview fields mirror <c>Starship.ApplyChassisComponentStats</c> for thruster parts at ship level 1.
        /// </summary>
        public void RecalculateFromChildren()
        {
            totalStats = default;
            matchedComponentIds.Clear();
            matchedScaleFactors.Clear();
            perComponentStats.Clear();
            previewSumPropulsionAcceleration = 0f;
            previewSumPropulsionAccelerationPerLevel = 0f;
            previewExtraThrusterMoveSpeed = 0f;
            previewPrimaryThrusterMoveSpeed = 0f;
            previewTopSpeedMoveSpeed = 0f;

            if (shipFamily == null)
                return;

            string familyId = !string.IsNullOrWhiteSpace(familyIdOverride)
                ? familyIdOverride.Trim()
                : shipFamily.familyId != null ? shipFamily.familyId.Trim() : string.Empty;

            if (string.IsNullOrEmpty(familyId))
                return;

#if UNITY_EDITOR
            shipFamily.InvalidateComponentStatsLookup();
#endif

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
                    ShipComponentAbilityStats scaled = ShipComponentAbilityStats.ScaleStatsByTransform(stats, t, componentId);
                    totalStats.AddInPlace(scaled);
                    matchedComponentIds.Add(componentId);
                    matchedScaleFactors.Add(ShipComponentAbilityStats.GetNormalizedScaleFromTransform(t));
                    perComponentStats.Add(scaled);
                }
            }

            ShipPropulsionAggregation.Result propulsion = ShipPropulsionAggregation.ComputeThrusterPropulsion(
                matchedComponentIds,
                perComponentStats,
                shipLevel: 1);
            totalStats = ShipPropulsionAggregation.ApplyPropulsionToSummedStats(
                totalStats,
                matchedComponentIds,
                perComponentStats,
                shipLevel: 1);

            previewTopSpeedMoveSpeed = propulsion.topMoveSpeed;
            previewSumPropulsionAcceleration = propulsion.sumAcceleration;
            previewPrimaryThrusterMoveSpeed = propulsion.primaryIndex >= 0
                ? perComponentStats[propulsion.primaryIndex].moveSpeed
                : 0f;
            previewExtraThrusterMoveSpeed = propulsion.extraMoveSpeedFromPerLevel;

            for (int k = 0; k < matchedComponentIds.Count; k++)
            {
                if (!ShipComponentAbilityStats.IsPropulsionComponent(matchedComponentIds[k]))
                    continue;
                previewSumPropulsionAccelerationPerLevel += perComponentStats[k].accelerationCapPerLevel;
            }
        }
    }
}

