using System;
using System.Collections.Generic;
using UnityEngine;
using TitanOrbit.Data;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Editor/runtime MonoBehaviour that live-sums <see cref="ShipComponentAbilityStats"/> from prefab children
    /// using <see cref="ShipFamilyStatsCalculator"/>. Attach to a chassis prefab root to preview matched parts
    /// and totals while authoring. [UNITY] Recalculates on enable, child changes, and OnValidate.
    /// Exposes propulsion breakdown and mass estimates for <see cref="Editor.ShipFamilyStatsPreviewEditor"/>.
    /// </summary>
    [ExecuteAlways]
    public class ShipFamilyStatsPreview : MonoBehaviour
    {
        [SerializeField] ShipFamilyDefinition shipFamily;
        [SerializeField] string familyIdOverride;
        [SerializeField] ShipComponentAbilityStats totalStats;
        [SerializeField] List<string> matchedComponentIds = new List<string>();
        [SerializeField] List<ShipComponentAbilityStats> perComponentStats = new List<ShipComponentAbilityStats>();
        [SerializeField] List<float> matchedScaleFactors = new List<float>();

        [SerializeField] float previewSumPropulsionAcceleration;
        [SerializeField] float previewSumPropulsionAccelerationPerLevel;
        [SerializeField] float previewPrimaryThrusterMoveSpeed;
        [SerializeField] float previewExtraThrusterMoveSpeed;
        [SerializeField] float previewTopSpeedMoveSpeed;
        [SerializeField] float previewComponentMass;
        [SerializeField] float previewHudHullMass;

        /// <summary>Aggregated ability totals after shared propulsion / weapon rules + family bonuses.</summary>
        public ShipComponentAbilityStats TotalStats => totalStats;

        /// <summary>Family asset used for component id → stats lookup.</summary>
        public ShipFamilyDefinition ShipFamily => shipFamily;

        /// <summary>Prefab child ids that matched family component entries on the last scan.</summary>
        public List<string> MatchedComponentIds => matchedComponentIds;

        /// <summary>Average localScale factors parallel to <see cref="MatchedComponentIds"/>.</summary>
        public List<float> MatchedScaleFactors => matchedScaleFactors;

        /// <summary>Per-part scaled stats parallel to <see cref="MatchedComponentIds"/>.</summary>
        public List<ShipComponentAbilityStats> PerComponentStats => perComponentStats;

        /// <summary>Sum of accelerationCap on engine/thruster parts (level 1).</summary>
        public float PreviewSumPropulsionAcceleration => previewSumPropulsionAcceleration;

        /// <summary>Sum of accelerationCapPerLevel on engine/thruster parts.</summary>
        public float PreviewSumPropulsionAccelerationPerLevel => previewSumPropulsionAccelerationPerLevel;

        /// <summary>Best engine/thruster base moveSpeed (counted once toward top speed).</summary>
        public float PreviewPrimaryThrusterMoveSpeed => previewPrimaryThrusterMoveSpeed;

        /// <summary>Half the sum of moveSpeedPerLevel from non-primary propulsion parts.</summary>
        public float PreviewExtraThrusterMoveSpeed => previewExtraThrusterMoveSpeed;

        /// <summary>Primary + extra propulsion move speed (matches in-game top-speed feel at level 1).</summary>
        public float PreviewTopSpeedMoveSpeed => previewTopSpeedMoveSpeed;

        /// <summary>Sum of part scale factors — speedometer MASS before hullMassScale.</summary>
        public float PreviewComponentMass => previewComponentMass;

        /// <summary>Component mass × <see cref="ShipFamilyDefinition.DefaultHullMassScale"/>.</summary>
        public float PreviewHudHullMass => previewHudHullMass;

        void OnEnable() => RecalculateFromChildren();

        void OnTransformChildrenChanged() => RecalculateFromChildren();

#if UNITY_EDITOR
        void OnValidate() => RecalculateFromChildren();
#endif

        /// <summary>Re-scans child transforms and refreshes serialized total/matched lists for inspector display.</summary>
        public void RecalculateFromChildren()
        {
            // --- Reset ---
            totalStats = default;
            matchedComponentIds.Clear();
            perComponentStats.Clear();
            matchedScaleFactors.Clear();
            previewSumPropulsionAcceleration = 0f;
            previewSumPropulsionAccelerationPerLevel = 0f;
            previewPrimaryThrusterMoveSpeed = 0f;
            previewExtraThrusterMoveSpeed = 0f;
            previewTopSpeedMoveSpeed = 0f;
            previewComponentMass = 0f;
            previewHudHullMass = 0f;

            if (shipFamily == null)
                return;

            // --- Full sum (aggregation + fallbacks + family bonuses) ---
            var sum = ShipFamilyStatsCalculator.SumFromPrefabHierarchy(gameObject, shipFamily, shipLevel: 1);
            totalStats = sum.TotalStats;
            matchedComponentIds = sum.MatchedComponentIds ?? new List<string>();
            perComponentStats = sum.PerComponentStats ?? new List<ShipComponentAbilityStats>();

            // --- Scale factors for matched ids (editor display only) ---
            string familyId = !string.IsNullOrWhiteSpace(familyIdOverride)
                ? familyIdOverride.Trim()
                : (shipFamily.familyId ?? string.Empty).Trim();
            RefreshMatchedScaleFactors(familyId);

            // --- Propulsion breakdown (engines + thrusters only) ---
            RefreshPropulsionPreview(shipLevel: 1);

            // --- Mass ---
            previewComponentMass = ChassisComponentStats.ComputeComponentMassFromTransform(transform, familyId);
            previewHudHullMass = Mathf.Max(0.5f, previewComponentMass * ShipFamilyDefinition.DefaultHullMassScale);
        }

        /// <summary>Fills <see cref="matchedScaleFactors"/> from current hierarchy for each matched id.</summary>
        void RefreshMatchedScaleFactors(string familyId)
        {
            matchedScaleFactors.Clear();
            if (matchedComponentIds == null || matchedComponentIds.Count == 0)
                return;

            var transforms = GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < matchedComponentIds.Count; i++)
            {
                string id = matchedComponentIds[i];
                float scale = 1f;
                if (!string.IsNullOrEmpty(familyId) && !string.IsNullOrEmpty(id))
                {
                    string want = familyId + "_" + id;
                    for (int t = 0; t < transforms.Length; t++)
                    {
                        Transform tr = transforms[t];
                        if (tr == null)
                            continue;
                        string name = ShipFamilyDefinition.NormalizeComponentId(tr.name);
                        if (string.Equals(name, want, StringComparison.OrdinalIgnoreCase)
                            || name.EndsWith("_" + id, StringComparison.OrdinalIgnoreCase))
                        {
                            scale = ChassisComponentStats.GetScaleFactor(tr);
                            break;
                        }
                    }
                }

                matchedScaleFactors.Add(scale);
            }
        }

        /// <summary>
        /// Computes primary / extra move-speed and accel sums from matched propulsion parts
        /// using the same rules as <see cref="ShipPropulsionAggregation"/>.
        /// </summary>
        void RefreshPropulsionPreview(int shipLevel)
        {
            if (matchedComponentIds == null || perComponentStats == null)
                return;

            var propulsion = ShipPropulsionAggregation.ComputeThrusterPropulsion(
                matchedComponentIds,
                perComponentStats,
                shipLevel);

            previewPrimaryThrusterMoveSpeed = 0f;
            previewExtraThrusterMoveSpeed = propulsion.extraMoveSpeedFromPerLevel;
            previewTopSpeedMoveSpeed = propulsion.topMoveSpeed;
            previewSumPropulsionAcceleration = propulsion.sumAcceleration;
            previewSumPropulsionAccelerationPerLevel = 0f;

            if (propulsion.primaryIndex >= 0 && propulsion.primaryIndex < perComponentStats.Count)
                previewPrimaryThrusterMoveSpeed = perComponentStats[propulsion.primaryIndex].moveSpeed;

            for (int i = 0; i < matchedComponentIds.Count && i < perComponentStats.Count; i++)
            {
                if (!ShipComponentAbilityStats.IsPropulsionComponent(matchedComponentIds[i]))
                    continue;
                previewSumPropulsionAccelerationPerLevel += perComponentStats[i].accelerationCapPerLevel;
            }
        }
    }
}
