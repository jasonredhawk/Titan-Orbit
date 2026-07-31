using System;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Canonical Part Profile group ids for <see cref="ShipFamilyPartCalcProfileSet"/>.
    /// Name Mappings point at these strings; Scan evaluates the matching profile.
    /// <para>
    /// [TITAN-ORBIT] Engine and thruster mounts share <see cref="Engine"/> stats; only mounts with
    /// <c>enablePropulsionVfx</c> get jet particles. Fin merges into <see cref="Tail"/>. Weapons split
    /// into rapid small-shot <see cref="WeaponBullet"/> vs slow heavy <see cref="WeaponCannon"/>.
    /// Everything else maps to <see cref="Hull"/>.
    /// </para>
    /// </summary>
    public static class ShipFamilyPartTypes
    {
        public const string Cockpit = "Cockpit";
        public const string WeaponBullet = "Weapon Bullet";
        public const string WeaponCannon = "Weapon Cannon";
        public const string Wing = "Wing";
        /// <summary>Shared propulsion group (engine meshes + thruster mounts).</summary>
        public const string Engine = "Engine/Thrust";
        /// <summary>Turn group — includes legacy Fin parts.</summary>
        public const string Tail = "Tail";
        /// <summary>Catch-all for Body, Armor, Core, Support, Arm, etc.</summary>
        public const string Hull = "Hull";

        public const string Unmapped = "Unmapped";
        public const string Ignore = "Ignore";

        /// <summary>Ordered core profiles shown after Reset / Ensure.</summary>
        public static readonly string[] CoreProfiles =
        {
            Cockpit,
            WeaponBullet,
            WeaponCannon,
            Wing,
            Engine,
            Tail,
            Hull,
        };

        /// <summary>True for Engine/Thrust (and legacy Engine / Thruster labels).</summary>
        public static bool IsPropulsion(string partType)
        {
            if (string.IsNullOrWhiteSpace(partType))
                return false;
            return string.Equals(partType, Engine, StringComparison.OrdinalIgnoreCase)
                || string.Equals(partType, "Engine", StringComparison.OrdinalIgnoreCase)
                || string.Equals(partType, "Thruster", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>True for either weapon subtype (and legacy Weapon).</summary>
        public static bool IsWeapon(string partType)
        {
            if (string.IsNullOrWhiteSpace(partType))
                return false;
            return string.Equals(partType, WeaponBullet, StringComparison.OrdinalIgnoreCase)
                || string.Equals(partType, WeaponCannon, StringComparison.OrdinalIgnoreCase)
                || string.Equals(partType, "Weapon", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>True for Tail (and legacy Fin).</summary>
        public static bool IsTurn(string partType)
        {
            if (string.IsNullOrWhiteSpace(partType))
                return false;
            return string.Equals(partType, Tail, StringComparison.OrdinalIgnoreCase)
                || string.Equals(partType, "Fin", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Migrates legacy labels and resolves generic Weapon → Bullet vs Cannon from the part name.
        /// </summary>
        /// <param name="partType">Current mapping / heuristic type.</param>
        /// <param name="componentIdOrName">Discovered name used when splitting weapons.</param>
        public static string Normalize(string partType, string componentIdOrName = null)
        {
            if (string.IsNullOrWhiteSpace(partType))
                return Unmapped;

            string t = partType.Trim();
            if (string.Equals(t, Unmapped, StringComparison.OrdinalIgnoreCase))
                return Unmapped;
            if (string.Equals(t, Ignore, StringComparison.OrdinalIgnoreCase))
                return Ignore;

            // --- Already canonical ---
            for (int i = 0; i < CoreProfiles.Length; i++)
            {
                if (string.Equals(t, CoreProfiles[i], StringComparison.OrdinalIgnoreCase))
                    return CoreProfiles[i];
            }

            // --- Legacy merges ---
            if (string.Equals(t, "Thruster", StringComparison.OrdinalIgnoreCase)
                || string.Equals(t, "Engine", StringComparison.OrdinalIgnoreCase)
                || string.Equals(t, "Exhaust", StringComparison.OrdinalIgnoreCase))
                return Engine;

            if (string.Equals(t, "Fin", StringComparison.OrdinalIgnoreCase)
                || string.Equals(t, "Tail", StringComparison.OrdinalIgnoreCase))
                return Tail;

            if (string.Equals(t, "Arm", StringComparison.OrdinalIgnoreCase)
                || string.Equals(t, "Body", StringComparison.OrdinalIgnoreCase)
                || string.Equals(t, "Armor", StringComparison.OrdinalIgnoreCase)
                || string.Equals(t, "Core", StringComparison.OrdinalIgnoreCase)
                || string.Equals(t, "Support", StringComparison.OrdinalIgnoreCase)
                || string.Equals(t, "Hull", StringComparison.OrdinalIgnoreCase)
                || string.Equals(t, "Part", StringComparison.OrdinalIgnoreCase)
                || string.Equals(t, "MainBody", StringComparison.OrdinalIgnoreCase)
                || string.Equals(t, "Tracks", StringComparison.OrdinalIgnoreCase)
                || string.Equals(t, "Spike", StringComparison.OrdinalIgnoreCase))
                return Hull;

            if (string.Equals(t, "Weapon", StringComparison.OrdinalIgnoreCase)
                || string.Equals(t, "WeaponBullet", StringComparison.OrdinalIgnoreCase)
                || string.Equals(t, "WeaponCannon", StringComparison.OrdinalIgnoreCase))
                return ResolveWeaponSubtype(componentIdOrName);

            if (string.Equals(t, Cockpit, StringComparison.OrdinalIgnoreCase))
                return Cockpit;
            if (string.Equals(t, Wing, StringComparison.OrdinalIgnoreCase))
                return Wing;

            // Unknown non-empty type → Hull so Scan still has a profile.
            return Hull;
        }

        /// <summary>
        /// Cannon / missile → heavy slow; gun / machinegun / generic weapon → rapid small.
        /// </summary>
        public static string ResolveWeaponSubtype(string componentIdOrName)
        {
            if (string.IsNullOrWhiteSpace(componentIdOrName))
                return WeaponBullet;

            string id = componentIdOrName.ToLowerInvariant();
            if (id.IndexOf("cannon", StringComparison.Ordinal) >= 0
                || id.IndexOf("missile", StringComparison.Ordinal) >= 0
                || id.IndexOf("rocket", StringComparison.Ordinal) >= 0)
                return WeaponCannon;

            return WeaponBullet;
        }

        /// <summary>Heuristic part type from a component / prefab suffix name.</summary>
        public static string InferFromComponentName(string componentId)
        {
            if (string.IsNullOrWhiteSpace(componentId))
                return Unmapped;

            string alias = ShipFamilyComponentPartKey.ResolveAliasKey(componentId);
            string id = (string.IsNullOrEmpty(alias) ? componentId : alias).ToLowerInvariant();

            if (id.IndexOf("cockpit", StringComparison.Ordinal) >= 0)
                return Cockpit;
            if (id.IndexOf("wing", StringComparison.Ordinal) >= 0)
                return Wing;
            if (id.IndexOf("cannon", StringComparison.Ordinal) >= 0
                || id.IndexOf("missile", StringComparison.Ordinal) >= 0
                || id.IndexOf("rocket", StringComparison.Ordinal) >= 0)
                return WeaponCannon;
            if (id.IndexOf("weapon", StringComparison.Ordinal) >= 0
                || id.IndexOf("gun", StringComparison.Ordinal) >= 0
                || id.IndexOf("barrel", StringComparison.Ordinal) >= 0
                || id.IndexOf("ammunition", StringComparison.Ordinal) >= 0)
                return WeaponBullet;
            if (id.IndexOf("engine", StringComparison.Ordinal) >= 0
                || id.IndexOf("thruster", StringComparison.Ordinal) >= 0
                || id.IndexOf("exhaust", StringComparison.Ordinal) >= 0
                || id.IndexOf("thrust", StringComparison.Ordinal) >= 0)
                return Engine;
            if (ContainsIsolatedKeyword(id, "fin") || ContainsIsolatedKeyword(id, "tail"))
                return Tail;

            return Hull;
        }

        static bool ContainsIsolatedKeyword(string haystackLower, string keywordLower)
        {
            int idx = haystackLower.IndexOf(keywordLower, StringComparison.Ordinal);
            while (idx >= 0)
            {
                bool leftOk = idx == 0 || !char.IsLetterOrDigit(haystackLower[idx - 1]);
                int end = idx + keywordLower.Length;
                bool rightOk = end >= haystackLower.Length || !char.IsLetterOrDigit(haystackLower[end]);
                if (leftOk && rightOk)
                    return true;
                idx = haystackLower.IndexOf(keywordLower, idx + 1, StringComparison.Ordinal);
            }

            return false;
        }
    }
}
