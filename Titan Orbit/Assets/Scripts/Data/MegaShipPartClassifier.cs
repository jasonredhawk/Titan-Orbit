using System;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Maps StarSparrow MEGA prefab child names onto the same eight part profiles regular
    /// USC families use (<see cref="ShipFamilyPartTypes"/>). MEGA hulls are not family-prefixed
    /// (<c>Armor1</c>, <c>TurretBarrel</c>, <c>MissileLauncher</c>) so the normal
    /// <c>AstroEagle_Weapon</c> scanner would miss them.
    /// <para>
    /// [TITAN-ORBIT] One turret is one gun: <c>TurretBarrel</c> / <c>MissileLauncher</c> count as
    /// weapon mounts; parent <c>TurretBase</c> is ignored so we do not double-count.
    /// </para>
    /// </summary>
    public static class MegaShipPartClassifier
    {
        /// <summary>
        /// True when this transform is a live MEGA weapon muzzle (pad + auto-fire mount).
        /// Skips <c>TurretBase</c> when a barrel/launcher child exists.
        /// </summary>
        public static bool IsWeaponMountTransform(Transform t)
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
                   || ShipWeaponMountCollectorLooksLikeWeapon(name);
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

        static bool ShipWeaponMountCollectorLooksLikeWeapon(string name)
        {
            return name.IndexOf("Weapon", StringComparison.OrdinalIgnoreCase) >= 0
                   || name.IndexOf("Gun", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static bool ContainsIgnoreCase(string haystack, string needle)
        {
            return haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
