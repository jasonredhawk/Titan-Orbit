using System.Collections.Generic;
using TitanOrbit.Data;
using UnityEngine;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Session cache of extracted family-part templates (one GameObject per
    /// <c>familyId|suffix</c>). First miss instantiates a source chassis, clones the
    /// matching child, then destroys the hull so later store purchases and B-key cycles
    /// only <c>Instantiate</c> the small template.
    /// <para>
    /// [TITAN-ORBIT] Templates keep authored colliders so hull-bake remaps can measure
    /// the foreign silhouette. Live proxies strip those colliders after clone.
    /// Weapon child <c>localScale</c> comes from the lowest-tier prefab so
    /// Weapon Bullet vs Weapon Cannon keep their designed size after a B-key swap.
    /// </para>
    /// </summary>
    public static class ShipFamilyPartVisualCache
    {
        static readonly Dictionary<string, GameObject> Templates =
            new Dictionary<string, GameObject>(64);

        static Transform s_HiddenRoot;

        /// <summary>Dictionary key <c>CosmicShark|Engine_2</c>.</summary>
        public static string MakeKey(string familyId, string normalizedSuffix)
        {
            string family = string.IsNullOrWhiteSpace(familyId) ? "?" : familyId.Trim();
            string suffix = string.IsNullOrWhiteSpace(normalizedSuffix) ? "?" : normalizedSuffix.Trim();
            return family + "|" + suffix;
        }

        /// <summary>
        /// Returns a hidden template for <paramref name="componentId"/> on
        /// <paramref name="sourceFamily"/>, or null when no upgrade-tree prefab contains it.
        /// Weapon templates use the lowest-tier authored size so Bullet vs Cannon
        /// keep their designed localScale. Other parts still prefer
        /// <paramref name="shipLevel"/>.
        /// </summary>
        public static GameObject GetOrCreateTemplate(
            ShipFamilyDefinition sourceFamily,
            string componentId,
            int shipLevel)
        {
            if (sourceFamily == null || string.IsNullOrWhiteSpace(componentId))
                return null;

            string suffix = ShipFamilyDefinition.GetComponentIdSuffix(sourceFamily.familyId, componentId);
            if (string.IsNullOrEmpty(suffix))
                suffix = ShipFamilyDefinition.NormalizeComponentId(componentId);
            if (string.IsNullOrEmpty(suffix))
                return null;

            string key = MakeKey(sourceFamily.familyId, suffix);
            if (Templates.TryGetValue(key, out GameObject cached) && cached != null)
                return cached;

            GameObject template = ExtractTemplate(sourceFamily, suffix, shipLevel);
            if (template == null)
                return null;

            Templates[key] = template;
            return template;
        }

        /// <summary>
        /// Instantiates <paramref name="template"/> under the host slot's parent,
        /// copies the host hardpoint pose, and applies the source part's authored
        /// localScale (Weapon Bullet vs Cannon keep their own size). Attribute grow
        /// runs afterward from that base — do not copy the live grown host scale.
        /// </summary>
        /// <param name="stripColliders">
        /// True on hybrid proxies (ECS covering box is the collider). False on bake clones.
        /// </param>
        public static GameObject InstantiateAtHostSlot(
            GameObject template,
            Transform hostSlot,
            bool stripColliders)
        {
            if (template == null || hostSlot == null)
                return null;

            Transform parent = hostSlot.parent;
            int sibling = hostSlot.GetSiblingIndex();
            string hostName = hostSlot.name;
            Vector3 pos = hostSlot.localPosition;
            Quaternion rot = hostSlot.localRotation;
            Vector3 sourceScale = template.transform.localScale;

            GameObject clone = Object.Instantiate(template, parent, false);
            clone.name = hostName;
            clone.SetActive(true);
            clone.transform.SetSiblingIndex(sibling);
            clone.transform.localPosition = pos;
            clone.transform.localRotation = rot;
            clone.transform.localScale = sourceScale;
            if (stripColliders)
                StripCollidersAndBodies(clone);
            return clone;
        }

        static GameObject ExtractTemplate(ShipFamilyDefinition family, string suffix, int shipLevel)
        {
            var prefabs = new List<GameObject>(8);

            // --- Weapon parts: lowest-tier prefab first ---
            // [TITAN-ORBIT] Child localScale on a L5 hull is often already grown. Attribute
            // grow + ship-root tier scale apply later; extracting a high-tier gun made
            // B-key swaps look enormous. Other parts still prefer the live ship tier.
            bool weaponPart = ShipFamilyPartTypes.IsWeapon(
                ShipFamilyPartTypes.Normalize(
                    ShipComponentAbilityStats.ResolvePartTypeForSuggestedStats(suffix),
                    suffix));
            int preferredLevel = weaponPart ? 1 : Mathf.Max(1, shipLevel);
            if (family.TryGetVisualPrefabForLevel(preferredLevel, out GameObject preferredPrefab)
                && preferredPrefab != null)
                prefabs.Add(preferredPrefab);
            if (family.upgradeTree != null)
            {
                for (int i = 0; i < family.upgradeTree.Count; i++)
                {
                    GameObject prefab = family.upgradeTree[i]?.prefab;
                    if (prefab != null && !prefabs.Contains(prefab))
                        prefabs.Add(prefab);
                }
            }

            Transform hidden = EnsureHiddenRoot();
            for (int i = 0; i < prefabs.Count; i++)
            {
                GameObject hull = Object.Instantiate(prefabs[i], hidden, false);
                hull.name = "_Scan_" + family.familyId;
                hull.SetActive(false);
                Transform found = ShipFamilyPartMatch.FindBestSourceTransform(
                    hull.transform, family.familyId, suffix);
                if (found == null)
                {
                    Object.DestroyImmediate(hull);
                    continue;
                }

                GameObject template = Object.Instantiate(found.gameObject, hidden, false);
                template.name = MakeKey(family.familyId, suffix);
                template.SetActive(false);
                Object.DestroyImmediate(hull);
                return template;
            }

            return null;
        }

        static Transform EnsureHiddenRoot()
        {
            if (s_HiddenRoot != null)
                return s_HiddenRoot;

            var go = new GameObject("ShipFamilyPartVisualCache");
            go.hideFlags = HideFlags.HideAndDontSave;
            if (Application.isPlaying)
                Object.DontDestroyOnLoad(go);
            go.SetActive(false);
            s_HiddenRoot = go.transform;
            return s_HiddenRoot;
        }

        static void StripCollidersAndBodies(GameObject root)
        {
            if (root == null)
                return;
            var cols = root.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < cols.Length; i++)
            {
                if (cols[i] != null)
                    Object.Destroy(cols[i]);
            }

            var bodies = root.GetComponentsInChildren<Rigidbody>(true);
            for (int i = 0; i < bodies.Length; i++)
            {
                if (bodies[i] != null)
                    Object.Destroy(bodies[i]);
            }
        }
    }
}
