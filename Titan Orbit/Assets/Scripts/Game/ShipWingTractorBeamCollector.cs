using TitanOrbit.ECS.Authoring;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Editor/runtime helper that tags wing transforms on a hull prefab with
    /// <see cref="ECS.Authoring.ShipWingTractorBeamAuthoring"/> so baking produces tractor-beam buffer elements.
    /// Paired with <see cref="ShipWingTractorBeamSyncSystem"/> for visual sync. Presentation-only — does not affect sim.
    /// </summary>
    public static class ShipWingTractorBeamCollector
    {
        /// <summary>
        /// Walks the hull hierarchy and adds authoring components on transforms whose names contain "Wing"
        /// (excluding weapon slots). Call when building or validating chassis prefabs.
        /// </summary>
        public static void EnsureWingTractorBeamsOnHierarchy(Transform hullRoot)
        {
            if (hullRoot == null)
                return;

            foreach (var t in hullRoot.GetComponentsInChildren<Transform>(true))
            {
                if (t == hullRoot)
                    continue;
                if (!LooksLikeWingTransform(t))
                    continue;

                if (t.GetComponent<ShipWingTractorBeamAuthoring>() == null)
                    t.gameObject.AddComponent<ShipWingTractorBeamAuthoring>();
            }
        }

        static bool LooksLikeWingTransform(Transform t)
        {
            string name = t.name;
            if (string.IsNullOrEmpty(name))
                return false;

            // Weapon children can contain "Wing" in legacy names — exclude them.
            if (name.Contains("Weapon", System.StringComparison.OrdinalIgnoreCase))
                return false;

            return name.Contains("Wing", System.StringComparison.OrdinalIgnoreCase);
        }
    }
}
