using System.Collections.Generic;
using TitanOrbit.Data;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Collects one transform per prefab component on a hybrid ship proxy for the death breakup.
    /// Regular hulls use USC component lists plus leftover meshes; MEGA uses
    /// <see cref="MegaShipComponentInventory.TryClassifyChild"/>. Helper Mesh/LOD/Collider
    /// children stay glued to their parent.
    /// </summary>
    public static class ShipDeathDebrisParts
    {
        const string BankPivotName = "BankPivot";
        const string PrefabContainerName = "Prefab";

        static readonly List<Transform> s_scratch = new List<Transform>(64);
        static readonly HashSet<Transform> s_set = new HashSet<Transform>();

        /// <summary>
        /// Fills <paramref name="into"/> with component roots under the live proxy (still active).
        /// </summary>
        public static void Collect(Transform proxyRoot, bool isMega, string familyPrefix, List<Transform> into)
        {
            into.Clear();
            if (proxyRoot == null)
                return;

            Transform chassis = FindChassisRoot(proxyRoot);
            if (chassis == null)
                return;

            s_set.Clear();
            if (isMega)
                CollectMega(chassis, s_set);
            else
                CollectRegular(chassis, familyPrefix, s_set);

            CollectFallbackRenderers(chassis, s_set);
            DropFolderOnlyAncestors(s_set);

            s_scratch.Clear();
            foreach (Transform t in s_set)
            {
                if (t != null)
                    s_scratch.Add(t);
            }

            s_scratch.Sort(CompareHierarchy);
            into.AddRange(s_scratch);
        }

        /// <summary>BankPivot/Prefab container, else the proxy root.</summary>
        public static Transform FindChassisRoot(Transform proxyRoot)
        {
            if (proxyRoot == null)
                return null;

            Transform bank = proxyRoot.Find(BankPivotName);
            if (bank != null)
            {
                Transform prefab = bank.Find(PrefabContainerName);
                if (prefab != null)
                    return prefab;
                if (bank.childCount > 0)
                    return bank.GetChild(0);
                return bank;
            }

            return proxyRoot;
        }

        /// <summary>True when this object is presentation-only (not a hull module).</summary>
        public static bool IsPresentationObject(Transform t)
        {
            if (t == null)
                return true;
            if (t.GetComponent<ShipWorldNameplate>() != null)
                return true;
            if (t.GetComponent<ShipPropulsionVisualApplier>() != null)
                return true;
            if (t.GetComponent<ShipDamageSmokeVisualApplier>() != null)
                return true;
            if (t.GetComponent<ShipStatusLoopVfxApplier>() != null)
                return true;
            if (t.GetComponent<ShipMoonDockVisualApplier>() != null)
                return true;
            if (t.GetComponent<ShipBankVisualApplier>() != null)
                return true;
            if (t.GetComponent<ShipComponentAttributeScaleApplier>() != null)
                return true;
            return false;
        }

        static void CollectRegular(Transform chassis, string familyPrefix, HashSet<Transform> into)
        {
            var stats = ChassisComponentStats.FromTransform(chassis, familyPrefix);
            AddList(stats.cockpitTransforms, into);
            AddList(stats.wingTransforms, into);
            AddList(stats.weaponTransforms, into);
            AddList(stats.engineTransforms, into);
            AddList(stats.thrusterTransforms, into);
            AddList(stats.tailTransforms, into);
            AddList(stats.partTransforms, into);
        }

        static void CollectMega(Transform chassis, HashSet<Transform> into)
        {
            var all = chassis.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                Transform t = all[i];
                if (t == null || t == chassis)
                    continue;
                if (IsPresentationObject(t))
                    continue;
                if (!MegaShipComponentInventory.TryClassifyChild(t, chassis, out _, out _))
                    continue;
                into.Add(t);
            }
        }

        static void CollectFallbackRenderers(Transform chassis, HashSet<Transform> into)
        {
            var renderers = chassis.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                if (renderer == null || renderer is ParticleSystemRenderer)
                    continue;
                if (renderer is TrailRenderer || renderer is LineRenderer)
                    continue;

                Transform t = NearestComponentRoot(renderer.transform, chassis);
                if (t == null || t == chassis)
                    continue;
                if (IsUnderCollected(t, into))
                    continue;
                into.Add(t);
            }
        }

        static Transform NearestComponentRoot(Transform mesh, Transform chassis)
        {
            Transform t = mesh;
            while (t != null && t != chassis && MegaShipPartClassifier.IsHelperChildName(t.name))
                t = t.parent;
            if (t == null || t == chassis)
                return null;
            if (IsPresentationObject(t) || MegaShipPartClassifier.ShouldIgnore(t.name))
                return null;
            return t;
        }

        static bool IsUnderCollected(Transform t, HashSet<Transform> collected)
        {
            Transform walk = t;
            while (walk != null)
            {
                if (collected.Contains(walk))
                    return true;
                walk = walk.parent;
            }

            return false;
        }

        static void DropFolderOnlyAncestors(HashSet<Transform> into)
        {
            s_scratch.Clear();
            foreach (Transform t in into)
            {
                if (t == null)
                    continue;
                if (HasOwnMesh(t))
                    continue;
                if (HasCollectedDescendant(t, into))
                    s_scratch.Add(t);
            }

            for (int i = 0; i < s_scratch.Count; i++)
                into.Remove(s_scratch[i]);
        }

        static bool HasCollectedDescendant(Transform t, HashSet<Transform> collected)
        {
            foreach (Transform other in collected)
            {
                if (other == null || other == t)
                    continue;
                if (other.IsChildOf(t))
                    return true;
            }

            return false;
        }

        static bool HasOwnMesh(Transform t)
        {
            var renderer = t.GetComponent<Renderer>();
            return renderer != null
                   && renderer is not ParticleSystemRenderer
                   && renderer is not TrailRenderer
                   && renderer is not LineRenderer;
        }

        static void AddList(List<Transform> source, HashSet<Transform> into)
        {
            if (source == null)
                return;
            for (int i = 0; i < source.Count; i++)
            {
                Transform t = source[i];
                if (t == null || IsPresentationObject(t))
                    continue;
                if (MegaShipPartClassifier.IsHelperChildName(t.name))
                    continue;
                into.Add(t);
            }
        }

        static int CompareHierarchy(Transform a, Transform b)
        {
            if (a == b)
                return 0;
            if (a == null)
                return 1;
            if (b == null)
                return -1;
            return string.CompareOrdinal(a.name, b.name);
        }
    }
}
