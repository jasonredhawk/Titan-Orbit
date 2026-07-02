using System.Collections.Generic;
using TitanOrbit.ECS;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>Caches hull box colliders collected from module prefabs before physics stripping.</summary>
    public class ShipHullColliderCache : MonoBehaviour
    {
        [SerializeField] List<ShipHullColliderElement> colliders = new List<ShipHullColliderElement>();

        public IReadOnlyList<ShipHullColliderElement> Colliders => colliders;

        public void SetColliders(List<ShipHullColliderElement> source)
        {
            colliders.Clear();
            if (source == null)
                return;
            colliders.AddRange(source);
        }
    }

    public static class ShipHullColliderCollector
    {
        public static List<ShipHullColliderElement> Collect(Transform hullRoot) =>
            ShipHullColliderBakeUtility.CollectFromHierarchy(hullRoot);

        public static void EnsureCacheOnHull(Transform hullRoot)
        {
            if (hullRoot == null)
                return;

            var cache = hullRoot.GetComponent<ShipHullColliderCache>();
            if (cache == null)
                cache = hullRoot.gameObject.AddComponent<ShipHullColliderCache>();

            cache.SetColliders(Collect(hullRoot));
        }
    }
}
