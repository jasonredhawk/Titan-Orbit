using System;
using System.Collections.Generic;
using UnityEngine;
using TitanOrbit.Data;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Editor/runtime preview of summed ship-family stats on a chassis prefab.
    /// </summary>
    [ExecuteAlways]
    public class ShipFamilyStatsPreview : MonoBehaviour
    {
        [SerializeField] ShipFamilyDefinition shipFamily;
        [SerializeField] string familyIdOverride;
        [SerializeField] ShipComponentAbilityStats totalStats;
        [SerializeField] List<string> matchedComponentIds = new List<string>();
        [SerializeField] List<ShipComponentAbilityStats> perComponentStats = new List<ShipComponentAbilityStats>();

        public ShipComponentAbilityStats TotalStats => totalStats;
        public ShipFamilyDefinition ShipFamily => shipFamily;

        void OnEnable() => RecalculateFromChildren();

        void OnTransformChildrenChanged() => RecalculateFromChildren();

#if UNITY_EDITOR
        void OnValidate() => RecalculateFromChildren();
#endif

        public void RecalculateFromChildren()
        {
            totalStats = default;
            matchedComponentIds.Clear();
            perComponentStats.Clear();

            if (shipFamily == null)
                return;

            var sum = ShipFamilyStatsCalculator.SumFromPrefabHierarchy(gameObject, shipFamily, shipLevel: 1);
            totalStats = sum.TotalStats;
            matchedComponentIds = sum.MatchedComponentIds ?? new List<string>();
            perComponentStats = sum.PerComponentStats ?? new List<ShipComponentAbilityStats>();
        }
    }
}
