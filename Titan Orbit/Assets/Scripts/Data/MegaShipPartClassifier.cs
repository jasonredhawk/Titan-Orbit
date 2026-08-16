using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// MEGA weapon mounts are tagged prefabs (<c>Gun</c>, <c>Cannon</c>, <c>Missile</c>,
    /// <c>Sniper</c>). Discovery walks the hull until it hits a tagged GameObject and stops —
    /// children of that prefab are one turret. Identity is the prefab asset name, not the
    /// instance name Unity may have suffixed with <c> (1)</c>.
    /// </summary>
    public static class MegaShipPartClassifier
    {
        public const string TagGun = "Gun";
        public const string TagCannon = "Cannon";
        public const string TagMissile = "Missile";
        public const string TagSniper = "Sniper";

        static readonly Regex UnityDuplicateSuffixRegex =
            new Regex(@"\s*\(\d+\)\s*$", RegexOptions.Compiled);

        /// <summary>
        /// Tagged weapon prefabs under <paramref name="hull"/>. Stops at each tagged GO.
        /// When the hull has no weapon tags (regular family ships), falls back to the first
        /// weapon-named child in each branch.
        /// </summary>
        public static void CollectWeaponAssemblies(Transform hull, List<Transform> into)
        {
            into.Clear();
            if (hull == null)
                return;

            CollectTaggedWeaponAssembliesRecursive(hull, into);
            if (into.Count > 0)
                return;

            CollectLegacyNameWeaponAssembliesRecursive(hull, into);
        }

        static void CollectTaggedWeaponAssembliesRecursive(Transform current, List<Transform> into)
        {
            int n = current.childCount;
            for (int i = 0; i < n; i++)
            {
                Transform child = current.GetChild(i);
                if (child == null)
                    continue;

                if (IsTaggedWeapon(child))
                {
                    into.Add(child);
                    continue;
                }

                CollectTaggedWeaponAssembliesRecursive(child, into);
            }
        }

        static void CollectLegacyNameWeaponAssembliesRecursive(Transform current, List<Transform> into)
        {
            int n = current.childCount;
            for (int i = 0; i < n; i++)
            {
                Transform child = current.GetChild(i);
                if (child == null)
                    continue;

                if (IsHelperChildName(child.name))
                    continue;

                if (IsWeaponGroupFolder(child.name))
                {
                    CollectLegacyNameWeaponAssembliesRecursive(child, into);
                    continue;
                }

                if (IsLegacyNamedWeapon(child))
                {
                    into.Add(child);
                    continue;
                }

                CollectLegacyNameWeaponAssembliesRecursive(child, into);
            }
        }

        /// <summary>True when this GameObject has a MEGA weapon tag.</summary>
        public static bool IsTaggedWeapon(Transform t)
        {
            return t != null && TryGetWeaponTag(t.gameObject, out _);
        }

        /// <summary>True when this transform is a MEGA tagged weapon mount.</summary>
        public static bool IsWeaponMountTransform(Transform t) => IsTaggedWeapon(t);

        /// <summary>Reads Gun / Cannon / Missile / Sniper from <paramref name="go"/>.</summary>
        public static bool TryGetWeaponTag(GameObject go, out string tag)
        {
            tag = null;
            if (go == null)
                return false;

            string value = go.tag;
            if (string.Equals(value, TagGun, StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, TagCannon, StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, TagMissile, StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, TagSniper, StringComparison.OrdinalIgnoreCase))
            {
                tag = value;
                return true;
            }

            return false;
        }

        /// <summary>Part profile for a weapon tag.</summary>
        public static string ResolvePartTypeFromWeaponTag(string tag)
        {
            if (string.Equals(tag, TagCannon, StringComparison.OrdinalIgnoreCase))
                return ShipFamilyPartTypes.WeaponCannon;
            if (string.Equals(tag, TagMissile, StringComparison.OrdinalIgnoreCase))
                return ShipFamilyPartTypes.WeaponMissile;
            if (string.Equals(tag, TagSniper, StringComparison.OrdinalIgnoreCase))
                return ShipFamilyPartTypes.WeaponSniper;
            return ShipFamilyPartTypes.WeaponBullet;
        }

        /// <summary>
        /// Prefab asset name for a nested instance (Editor), else the cleaned object name.
        /// Unity duplicate suffixes like <c> (1)</c> are stripped.
        /// </summary>
        public static string GetPrefabAssetName(Transform t)
        {
            return t != null ? GetPrefabAssetName(t.gameObject) : string.Empty;
        }

        /// <summary>Prefab asset name for <paramref name="go"/>.</summary>
        public static string GetPrefabAssetName(GameObject go)
        {
            if (go == null)
                return string.Empty;

#if UNITY_EDITOR
            var nearest = UnityEditor.PrefabUtility.GetNearestPrefabInstanceRoot(go);
            if (nearest != null)
            {
                var nearestSource = UnityEditor.PrefabUtility.GetCorrespondingObjectFromOriginalSource(nearest)
                                    ?? UnityEditor.PrefabUtility.GetCorrespondingObjectFromSource(nearest);
                if (nearestSource != null && !string.IsNullOrEmpty(nearestSource.name))
                    return StripUnityDuplicateSuffix(nearestSource.name);
            }

            var direct = UnityEditor.PrefabUtility.GetCorrespondingObjectFromOriginalSource(go)
                         ?? UnityEditor.PrefabUtility.GetCorrespondingObjectFromSource(go);
            if (direct != null && !string.IsNullOrEmpty(direct.name))
                return StripUnityDuplicateSuffix(direct.name);
#endif
            return StripUnityDuplicateSuffix(go.name);
        }

        /// <summary>Strips trailing Unity <c> (N)</c> duplicate suffixes.</summary>
        public static string StripUnityDuplicateSuffix(string name)
        {
            if (string.IsNullOrEmpty(name))
                return string.Empty;
            return UnityDuplicateSuffixRegex.Replace(name.Trim(), string.Empty).Trim();
        }

        /// <summary>
        /// Folder that holds several guns. Used only by the untagged (regular-ship) fallback.
        /// </summary>
        public static bool IsWeaponGroupFolder(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;
            if (string.Equals(name, "Turrets", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Weapons", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Guns", StringComparison.OrdinalIgnoreCase))
                return true;
            return name.EndsWith("_Turrets", StringComparison.OrdinalIgnoreCase)
                   || name.EndsWith("_Weapons", StringComparison.OrdinalIgnoreCase)
                   || name.EndsWith("_Guns", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Part-profile id for a MEGA child. Tagged weapons use the tag; others use the
        /// prefab asset name (not the instance name).
        /// </summary>
        public static string ResolvePartType(Transform t)
        {
            if (t == null)
                return ShipFamilyPartTypes.Hull;
            if (TryGetWeaponTag(t.gameObject, out string tag))
                return ResolvePartTypeFromWeaponTag(tag);
            return ResolvePartType(GetPrefabAssetName(t));
        }

        /// <summary>
        /// Part-profile id for a MEGA child name. Used when summing static MEGA stats.
        /// </summary>
        public static string ResolvePartType(string childName)
        {
            if (string.IsNullOrWhiteSpace(childName))
                return ShipFamilyPartTypes.Hull;

            string id = childName.ToLowerInvariant();

            if (id.IndexOf("turretbase", StringComparison.Ordinal) >= 0)
                return ShipFamilyPartTypes.Ignore;

            if (IsHelperChildName(childName))
                return ShipFamilyPartTypes.Ignore;

            if (id.IndexOf("sniper", StringComparison.Ordinal) >= 0
                || id.IndexOf("rail", StringComparison.Ordinal) >= 0)
                return ShipFamilyPartTypes.WeaponSniper;

            if (id.IndexOf("missile", StringComparison.Ordinal) >= 0
                || id.IndexOf("launcher", StringComparison.Ordinal) >= 0
                || id.IndexOf("rocket", StringComparison.Ordinal) >= 0)
                return ShipFamilyPartTypes.WeaponMissile;

            if (id.IndexOf("cannon", StringComparison.Ordinal) >= 0)
                return ShipFamilyPartTypes.WeaponCannon;

            if (id.IndexOf("turret", StringComparison.Ordinal) >= 0
                || id.IndexOf("barrel", StringComparison.Ordinal) >= 0
                || id.IndexOf("weapon", StringComparison.Ordinal) >= 0
                || id.IndexOf("gun", StringComparison.Ordinal) >= 0)
                return ShipFamilyPartTypes.WeaponBullet;

            return ShipFamilyPartTypes.InferFromComponentName(childName);
        }

        /// <summary>True when this part should be skipped during stat sum and mount bake.</summary>
        public static bool ShouldIgnore(string childName)
        {
            if (IsHelperChildName(childName))
                return true;

            string part = ResolvePartType(childName);
            return string.Equals(part, ShipFamilyPartTypes.Ignore, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(part, ShipFamilyPartTypes.Unmapped, StringComparison.OrdinalIgnoreCase)
                      && ContainsIgnoreCase(childName, "TurretBase");
        }

        /// <summary>
        /// Physics / LOD / mesh helper children under a StarSparrow part. They are not
        /// gameplay components and must not receive hull stats.
        /// </summary>
        public static bool IsHelperChildName(string childName)
        {
            if (string.IsNullOrWhiteSpace(childName))
                return true;

            if (ContainsIgnoreCase(childName, "Collider"))
                return true;
            if (ContainsIgnoreCase(childName, "LOD"))
                return true;
            if (string.Equals(childName, "Mesh", StringComparison.OrdinalIgnoreCase)
                || string.Equals(childName, "default", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        static bool IsLegacyNamedWeapon(Transform t)
        {
            if (t == null)
                return false;

            string name = t.name;
            if (string.IsNullOrEmpty(name))
                return false;

            if (ContainsIgnoreCase(name, "TurretBase"))
                return false;

            if (ContainsIgnoreCase(name, "TurretBarrel")
                || ContainsIgnoreCase(name, "MissileLauncher")
                || ContainsIgnoreCase(name, "Launcher")
                || ContainsIgnoreCase(name, "Sniper")
                || ContainsIgnoreCase(name, "Rail"))
                return true;

            if (ContainsIgnoreCase(name, "Turret") && !ContainsIgnoreCase(name, "Base"))
                return true;

            return ShipComponentAbilityStatsMath.IsWeaponComponent(name)
                   || name.IndexOf("Weapon", StringComparison.OrdinalIgnoreCase) >= 0
                   || name.IndexOf("Gun", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static bool ContainsIgnoreCase(string haystack, string needle)
        {
            return haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
