using System;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Canonical Part Profile group ids for <see cref="ShipFamilyPartCalcProfileSet"/>.
    /// Name Mappings point at these strings; Scan evaluates the matching profile.
    /// <para>
    /// [TITAN-ORBIT] <see cref="Engine"/> and <see cref="Thruster"/> are separate Part Profiles:
    /// engines author move/accel + Energy Cap/Regen (power plant); thrusters author move/accel + turn
    /// and set thrust energy drain. Both still share the same move/accel aggregation rules at runtime
    /// (<see cref="ShipPropulsionAggregation"/>). Only mounts with <c>enablePropulsionVfx</c> get jet
    /// particles. Fin merges into <see cref="Tail"/>. Weapons split into rapid small-shot
    /// <see cref="WeaponBullet"/> vs slow heavy <see cref="WeaponCannon"/> — offense plus
    /// Energy Cap (extra storage); engines alone produce Regen.
    /// Everything else maps to <see cref="Hull"/>.
    /// </para>
    /// </summary>
    public static class ShipFamilyPartTypes
    {
        public const string Cockpit = "Cockpit";
        public const string WeaponBullet = "Weapon Bullet";
        public const string WeaponCannon = "Weapon Cannon";
        public const string Wing = "Wing";
        /// <summary>Power-plant profile — move/accel + Energy Cap/Regen.</summary>
        public const string Engine = "Engine";
        /// <summary>Maneuver-jet profile — move/accel + turn; thrust energy drain source.</summary>
        public const string Thruster = "Thruster";
        /// <summary>[LEGACY] Pre-split shared propulsion label — Normalize rewrites to Engine or Thruster.</summary>
        public const string LegacyEngineThrust = "Engine/Thrust";
        /// <summary>Turn group — includes legacy Fin parts.</summary>
        public const string Tail = "Tail";
        /// <summary>Catch-all for Body, Armor, Core, Support, Arm, etc.</summary>
        public const string Hull = "Hull";

        public const string Unmapped = "Unmapped";
        public const string Ignore = "Ignore";

        /// <summary>Ordered core profiles shown after Reset / Ensure (eight groups).</summary>
        public static readonly string[] CoreProfiles =
        {
            Cockpit,
            WeaponBullet,
            WeaponCannon,
            Wing,
            Engine,
            Thruster,
            Tail,
            Hull,
        };

        /// <summary>True for Engine, Thruster, and legacy Engine/Thrust labels.</summary>
        public static bool IsPropulsion(string partType)
        {
            if (string.IsNullOrWhiteSpace(partType))
                return false;
            return string.Equals(partType, Engine, StringComparison.OrdinalIgnoreCase)
                || string.Equals(partType, Thruster, StringComparison.OrdinalIgnoreCase)
                || string.Equals(partType, LegacyEngineThrust, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>True for the Engine Part Profile (power plant).</summary>
        public static bool IsEngineProfile(string partType)
        {
            if (string.IsNullOrWhiteSpace(partType))
                return false;
            return string.Equals(partType, Engine, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>True for the Thruster Part Profile (maneuver jets).</summary>
        public static bool IsThrusterProfile(string partType)
        {
            if (string.IsNullOrWhiteSpace(partType))
                return false;
            return string.Equals(partType, Thruster, StringComparison.OrdinalIgnoreCase);
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

        /// <summary>True for the heavy Weapon Cannon Part Profile (and Missile aliases after Normalize).</summary>
        public static bool IsWeaponCannonProfile(string partType)
        {
            if (string.IsNullOrWhiteSpace(partType))
                return false;
            return string.Equals(partType, WeaponCannon, StringComparison.OrdinalIgnoreCase);
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
        /// [TITAN-ORBIT] Maneuver-jet name heuristic (same idea as propulsion VFX fallback).
        /// Thruster-like mounts author turn + contribute thrust energy drain; they do not own Energy Cap/Regen
        /// unless the hull has no engines (SpaceExcalibur-style fallback).
        /// </summary>
        /// <param name="componentId">Prefab child / family component id.</param>
        public static bool IsThrusterLikeName(string componentId)
        {
            if (string.IsNullOrWhiteSpace(componentId))
                return false;
            string n = componentId.Trim();
            return n.IndexOf("Thruster", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("Exhaust", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// [TITAN-ORBIT] Power-plant mounts: propulsion that is not thruster-like.
        /// Engines author Energy Cap/Regen (cumulative across mounts) plus move/accel.
        /// </summary>
        /// <param name="componentId">Prefab child / family component id.</param>
        public static bool IsEngineLikeName(string componentId)
        {
            if (string.IsNullOrWhiteSpace(componentId))
                return false;
            if (IsThrusterLikeName(componentId))
                return false;

            // Explicit engine token, or inferred propulsion that is not a thruster/exhaust mount.
            string n = componentId.Trim();
            if (n.IndexOf("Engine", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            string inferred = InferFromComponentName(componentId);
            return IsEngineProfile(inferred) || string.Equals(inferred, LegacyEngineThrust, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Migrates legacy labels and resolves Engine vs Thruster from the part name when needed.
        /// </summary>
        /// <param name="partType">Current mapping / heuristic type.</param>
        /// <param name="componentIdOrName">Discovered name used when splitting Engine/Thrust and weapons.</param>
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

            // --- Legacy shared propulsion → Engine or Thruster from the mount name ---
            if (string.Equals(t, LegacyEngineThrust, StringComparison.OrdinalIgnoreCase))
                return ResolvePropulsionSubtype(componentIdOrName);

            // --- Explicit thruster / exhaust tokens ---
            if (string.Equals(t, "Thruster", StringComparison.OrdinalIgnoreCase)
                || string.Equals(t, "Exhaust", StringComparison.OrdinalIgnoreCase))
                return Thruster;

            // --- Explicit engine token ---
            if (string.Equals(t, "Engine", StringComparison.OrdinalIgnoreCase))
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
        /// Splits legacy <c>Engine/Thrust</c> (or ambiguous propulsion) into Engine vs Thruster by name.
        /// </summary>
        public static string ResolvePropulsionSubtype(string componentIdOrName)
        {
            if (IsThrusterLikeName(componentIdOrName))
                return Thruster;
            return Engine;
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

            // Thruster / exhaust before generic "thrust" / engine.
            if (id.IndexOf("thruster", StringComparison.Ordinal) >= 0
                || id.IndexOf("exhaust", StringComparison.Ordinal) >= 0)
                return Thruster;
            if (id.IndexOf("engine", StringComparison.Ordinal) >= 0
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
