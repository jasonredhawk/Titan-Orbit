using System;
using System.Text.RegularExpressions;
using TitanOrbit.Data;
using UnityEditor;
using UnityEngine;

namespace TitanOrbit.Editor
{
    /// <summary>
    /// Resolves ship-part identity from <b>nested prefab asset names</b>, not GameObject instance
    /// names. Unity appends <c> (1)</c>, <c> (2)</c> when hierarchy names collide — those must never
    /// become component ids. Discover and Scan Folder share this helper.
    /// </summary>
    public static class ShipFamilyPrefabComponentNameUtility
    {
        /// <summary>[UNITY] Duplicate hierarchy suffix Unity adds: "Wing_2 (1)".</summary>
        static readonly Regex UnityDuplicateSuffixRegex =
            new Regex(@"\s*\(\d+\)\s*$", RegexOptions.Compiled);

        /// <summary>
        /// Tries to read the part suffix after <c>FamilyId_</c> (e.g. <c>Cockpit_Base_2</c>)
        /// for a transform under a ship prefab. Discover / Scan prepend the family id to form
        /// the catalog id. Prefers the nested prefab asset name.
        /// </summary>
        /// <param name="t">Child transform inside a loaded ship prefab.</param>
        /// <param name="familyId">Family folder id (AstroEagle, …).</param>
        /// <param name="componentRest">Suffix after FamilyId_, with _L/_R stripped.</param>
        /// <returns>True when a clean family-prefixed part id was resolved.</returns>
        public static bool TryResolveComponentRest(Transform t, string familyId, out string componentRest)
        {
            componentRest = string.Empty;
            if (t == null || string.IsNullOrWhiteSpace(familyId))
                return false;

            // --- Prefer nested prefab asset name (stable; no Unity (N) duplicates) ---
            string rawName = ResolvePrefabAssetOrTransformName(t);
            if (string.IsNullOrEmpty(rawName))
                return false;

            rawName = StripUnityDuplicateSuffix(rawName);
            string name = ShipFamilyDefinition.NormalizeComponentId(rawName);
            if (string.IsNullOrEmpty(name))
                return false;

            // Reject leftover duplicate markers after normalize (safety net).
            if (name.IndexOf('(') >= 0)
                return false;

            if (!name.StartsWith(familyId + "_", StringComparison.OrdinalIgnoreCase))
                return false;

            string rest = name.Substring(familyId.Length + 1);
            if (string.IsNullOrWhiteSpace(rest))
                return false;

            if (rest.EndsWith("_L", StringComparison.OrdinalIgnoreCase)
                || rest.EndsWith("_R", StringComparison.OrdinalIgnoreCase))
                rest = rest.Substring(0, rest.Length - 2);

            if (string.IsNullOrWhiteSpace(rest))
                return false;

            componentRest = ShipFamilyDefinition.NormalizeComponentId(rest);
            return !string.IsNullOrWhiteSpace(componentRest);
        }

        /// <summary>
        /// Nested prefab source name when available; otherwise the transform name only if it has
        /// no Unity <c>(N)</c> duplicate suffix (plain hierarchy parts that are not prefab instances).
        /// </summary>
        public static string ResolvePrefabAssetOrTransformName(Transform t)
        {
            if (t == null)
                return string.Empty;

            // [UNITY] Corresponding source on a nested prefab instance → asset name (Wing_2.prefab).
            GameObject source = PrefabUtility.GetCorrespondingObjectFromSource(t.gameObject);
            if (source != null && !string.IsNullOrEmpty(source.name))
                return source.name;

            // Nearest prefab instance root (when the transform is a child inside a nested prefab).
            GameObject nearestRoot = PrefabUtility.GetNearestPrefabInstanceRoot(t.gameObject);
            if (nearestRoot != null)
            {
                GameObject nearestSource = PrefabUtility.GetCorrespondingObjectFromSource(nearestRoot);
                if (nearestSource != null && !string.IsNullOrEmpty(nearestSource.name))
                    return nearestSource.name;
            }

            // Non-prefab child: allow only clean names (no Unity duplicate suffix).
            string instanceName = t.name ?? string.Empty;
            if (UnityDuplicateSuffixRegex.IsMatch(instanceName))
                return string.Empty;
            return instanceName;
        }

        /// <summary>Strips trailing <c> (N)</c> that Unity adds for duplicate hierarchy names.</summary>
        public static string StripUnityDuplicateSuffix(string name)
        {
            if (string.IsNullOrEmpty(name))
                return string.Empty;
            return UnityDuplicateSuffixRegex.Replace(name.Trim(), string.Empty).Trim();
        }

        /// <summary>Family suffix of a transform/asset name, or empty.</summary>
        public static string ExtractFamilySuffix(string transformOrAssetName, string familyId)
        {
            string stripped = StripUnityDuplicateSuffix(transformOrAssetName);
            string name = ShipFamilyDefinition.NormalizeComponentId(stripped);
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(familyId))
                return string.Empty;
            if (!name.StartsWith(familyId + "_", StringComparison.OrdinalIgnoreCase))
                return string.Empty;
            string rest = name.Substring(familyId.Length + 1);
            if (rest.EndsWith("_L", StringComparison.OrdinalIgnoreCase)
                || rest.EndsWith("_R", StringComparison.OrdinalIgnoreCase))
                rest = rest.Substring(0, rest.Length - 2);
            return rest;
        }
    }
}
