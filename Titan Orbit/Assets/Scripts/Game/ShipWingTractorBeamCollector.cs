using TitanOrbit.ECS.Authoring;
using UnityEngine;

namespace TitanOrbit.Game
{
    public static class ShipWingTractorBeamCollector
    {
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

            if (name.Contains("Weapon", System.StringComparison.OrdinalIgnoreCase))
                return false;

            return name.Contains("Wing", System.StringComparison.OrdinalIgnoreCase);
        }
    }
}
