using System.Collections.Generic;
using TitanOrbit.Data;
using UnityEngine;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Spawns visual meshes for store-bought ship components under the ship prefab container.
    /// </summary>
    public static class EquippedComponentVisualBuilder
    {
        public const string VisualNamePrefix = "EquippedPart_";

        public static void RebuildAll(
            Starship ship,
            Transform visualRoot,
            GameObject sourcePrefab,
            ShipFamilyDefinition family,
            IReadOnlyList<EquippedEquipmentEntry> equipment)
        {
            if (visualRoot == null)
                return;

            RemoveExisting(visualRoot);

            if (equipment == null || equipment.Count == 0 || family == null || sourcePrefab == null)
                return;

            string familyId = family.familyId != null ? family.familyId.Trim() : string.Empty;
            if (string.IsNullOrEmpty(familyId))
                return;

            GameObject scratch = Object.Instantiate(sourcePrefab);
            try
            {
                for (int slot = 0; slot < equipment.Count; slot++)
                {
                    EquippedEquipmentEntry entry = equipment[slot];
                    if (!entry.IsShipComponent)
                        continue;

                    string componentId = entry.ComponentId;
                    if (string.IsNullOrWhiteSpace(componentId))
                        continue;

                    Transform sourceTransform = FindComponentTransform(scratch.transform, family, familyId, componentId);
                    if (sourceTransform == null)
                        continue;

                    GameObject instance = Object.Instantiate(sourceTransform.gameObject, visualRoot);
                    instance.name = VisualNamePrefix + slot + "_" + componentId;
                    Transform t = instance.transform;
                    t.localPosition = EquippedComponentPlacementUtility.GetLocalPosition(in entry);
                    t.localRotation = EquippedComponentPlacementUtility.GetLocalRotation(in entry);
                    t.localScale = sourceTransform.localScale;

                    Starship.StripNonVisualComponents(t, null);
                }
            }
            finally
            {
                Object.Destroy(scratch);
            }
        }

        public static void RemoveExisting(Transform visualRoot)
        {
            if (visualRoot == null)
                return;

            for (int i = visualRoot.childCount - 1; i >= 0; i--)
            {
                Transform child = visualRoot.GetChild(i);
                if (child != null && child.name.StartsWith(VisualNamePrefix))
                {
                    if (Application.isPlaying)
                        Object.Destroy(child.gameObject);
                    else
                        Object.DestroyImmediate(child.gameObject);
                }
            }
        }

        public static Transform FindComponentTransform(
            Transform searchRoot,
            ShipFamilyDefinition family,
            string familyId,
            string componentId)
        {
            if (searchRoot == null || string.IsNullOrWhiteSpace(componentId))
                return null;

            var matches = new List<Transform>();
            CollectMatchingTransforms(searchRoot, family, familyId, componentId, matches);
            if (matches.Count == 0)
                return null;

            for (int i = 0; i < matches.Count; i++)
            {
                Transform t = matches[i];
                if (t != null && t.name.IndexOf("_Mirrored", System.StringComparison.OrdinalIgnoreCase) < 0)
                    return t;
            }

            return matches[0];
        }

        private static void CollectMatchingTransforms(
            Transform root,
            ShipFamilyDefinition family,
            string familyId,
            string componentId,
            List<Transform> results)
        {
            if (root == null || results == null)
                return;

            if (TransformMatchesComponentId(family, root.name, familyId, componentId))
                results.Add(root);

            for (int i = 0; i < root.childCount; i++)
                CollectMatchingTransforms(root.GetChild(i), family, familyId, componentId, results);
        }

        private static bool TransformMatchesComponentId(
            ShipFamilyDefinition family,
            string transformName,
            string familyId,
            string componentId)
        {
            if (string.IsNullOrWhiteSpace(transformName) || string.IsNullOrWhiteSpace(componentId))
                return false;

            string trimmedId = componentId.Trim();
            string directPrefix = familyId + "_" + trimmedId;
            if (transformName.StartsWith(directPrefix, System.StringComparison.OrdinalIgnoreCase))
                return true;

            if (family != null && family.TryGetComponentEntry(trimmedId, out ShipFamilyComponentEntry entry) &&
                entry != null && !string.IsNullOrWhiteSpace(entry.componentId))
            {
                string canonical = entry.componentId.Trim();
                if (!string.Equals(canonical, trimmedId, System.StringComparison.OrdinalIgnoreCase))
                {
                    string altPrefix = familyId + "_" + canonical;
                    if (transformName.StartsWith(altPrefix, System.StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            return false;
        }
    }
}
