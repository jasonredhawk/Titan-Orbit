using System;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Canonical Part Profile group ids for <see cref="ShipFamilyPartCalcProfileSet"/>.
    /// Name Mappings point at these strings; Scan evaluates the matching profile.
    /// <para>
    /// [TITAN-ORBIT] <see cref="Engine"/> and <see cref="Thruster"/> are separate Part Profiles:
    /// engines own Move Speed + Energy Cap/Regen (power plant); thrusters own Acceleration + turn
    /// and set thrust energy drain. Runtime aggregation follows that split
    /// (<see cref="ShipPropulsionAggregation"/>). Only mounts with <c>enablePropulsionVfx</c> get jet
    /// particles. Fin merges into <see cref="Tail"/>. Every family weapon — gun, cannon,
    /// missile, rocket, launcher — shares <see cref="WeaponBullet"/> (offense plus Energy
    /// Cap). <see cref="WeaponCannon"/>, <see cref="WeaponMissile"/>, and <see cref="WeaponSniper"/>
    /// stay MEGA combat classes and are not family Part Profiles. Engines alone produce Regen.
    /// Titan cargo / hold / storage parts use <see cref="Cargo"/>.
    /// Everything else maps to <see cref="Hull"/>.
    /// </para>
    /// </summary>
    public static class ShipFamilyPartTypes
    {
        public const string Cockpit = "Cockpit";
        /// <summary>The only family weapon Part Profile. Gun, cannon, missile, and launcher names all use it.</summary>
        public const string WeaponBullet = "Weapon Bullet";
        /// <summary>MEGA hitscan / cannon class. Family Normalize rewrites this label to <see cref="WeaponBullet"/>.</summary>
        public const string WeaponCannon = "Weapon Cannon";
        /// <summary>MEGA / heavy rockets — slow, high damage.</summary>
        public const string WeaponMissile = "Weapon Missile";
        /// <summary>MEGA sniper — high damage, high speed, very slow cadence.</summary>
        public const string WeaponSniper = "Weapon Sniper";
        public const string Wing = "Wing";
        /// <summary>Cargo hold / storage — Titan type-table plus family capacity parts.</summary>
        public const string Cargo = "Cargo";
        /// <summary>Power-plant profile — Move Speed + Energy Cap/Regen.</summary>
        public const string Engine = "Engine";
        /// <summary>Maneuver-jet profile — Acceleration + turn; thrust energy drain source.</summary>
        public const string Thruster = "Thruster";
        /// <summary>[LEGACY] Pre-split shared propulsion label — Normalize rewrites to Engine or Thruster.</summary>
        public const string LegacyEngineThrust = "Engine/Thrust";
        /// <summary>Turn group — includes legacy Fin parts.</summary>
        public const string Tail = "Tail";
        /// <summary>Catch-all for Body, Armor, Core, Support, Arm, etc.</summary>
        public const string Hull = "Hull";

        public const string Unmapped = "Unmapped";
        public const string Ignore = "Ignore";

        /// <summary>Ordered core profiles shown after Reset / Ensure (seven groups, one weapon).</summary>
        public static readonly string[] CoreProfiles =
        {
            Cockpit,
            WeaponBullet,
            Wing,
            Engine,
            Thruster,
            Tail,
            Hull,
        };

        /// <summary>
        /// Inspector popup for Name Mapping / suggestion Part Type:
        /// Unmapped, seven core groups, Cargo, Ignore.
        /// </summary>
        public static readonly string[] InspectorChoices =
        {
            Unmapped,
            Cockpit,
            WeaponBullet,
            Wing,
            Cargo,
            Engine,
            Thruster,
            Tail,
            Hull,
            Ignore,
        };

        /// <summary>Popup labels for mapping rows (Unmapped + core + Ignore) or profile rows (core only).</summary>
        public static string[] GetInspectorChoices(bool includeUnmappedAndIgnore) =>
            includeUnmappedAndIgnore ? InspectorChoices : CoreProfiles;

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

        /// <summary>True for the family weapon profile and MEGA cannon / missile / sniper labels.</summary>
        public static bool IsWeapon(string partType)
        {
            if (string.IsNullOrWhiteSpace(partType))
                return false;
            return string.Equals(partType, WeaponBullet, StringComparison.OrdinalIgnoreCase)
                || string.Equals(partType, WeaponCannon, StringComparison.OrdinalIgnoreCase)
                || string.Equals(partType, WeaponMissile, StringComparison.OrdinalIgnoreCase)
                || string.Equals(partType, WeaponSniper, StringComparison.OrdinalIgnoreCase)
                || string.Equals(partType, "Weapon", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>True for the MEGA Weapon Cannon combat class (hitscan when <c>isLaser</c> is set).</summary>
        public static bool IsWeaponCannonProfile(string partType)
        {
            if (string.IsNullOrWhiteSpace(partType))
                return false;
            return string.Equals(partType, WeaponCannon, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>True for the Cargo / hold / storage Part Profile.</summary>
        public static bool IsCargoProfile(string partType)
        {
            if (string.IsNullOrWhiteSpace(partType))
                return false;
            return string.Equals(partType, Cargo, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Cargo-hold name heuristic: Cargo, Storage, or an isolated Hold token
        /// (not Holder / Threshold).
        /// </summary>
        public static bool IsCargoLikeName(string componentId)
        {
            if (string.IsNullOrWhiteSpace(componentId))
                return false;

            string id = componentId.ToLowerInvariant();
            if (id.IndexOf("cargo", StringComparison.Ordinal) >= 0
                || id.IndexOf("storage", StringComparison.Ordinal) >= 0)
                return true;
            return ContainsIsolatedKeyword(id, "hold");
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
        /// Engines author Energy Cap/Regen (cumulative across mounts) plus Move Speed.
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
        /// <param name="componentIdOrName">Discovered name used when splitting Engine/Thrust.</param>
        public static string Normalize(string partType, string componentIdOrName = null)
        {
            if (string.IsNullOrWhiteSpace(partType))
                return Unmapped;

            string t = partType.Trim();
            if (string.Equals(t, Unmapped, StringComparison.OrdinalIgnoreCase))
                return Unmapped;
            if (string.Equals(t, Ignore, StringComparison.OrdinalIgnoreCase))
                return Ignore;

            // Family weapons are one profile. MEGA combat still reads Weapon Cannon /
            // Missile / Sniper without calling Normalize.
            if (string.Equals(t, WeaponCannon, StringComparison.OrdinalIgnoreCase)
                || string.Equals(t, "WeaponCannon", StringComparison.OrdinalIgnoreCase)
                || string.Equals(t, "Weapon", StringComparison.OrdinalIgnoreCase)
                || string.Equals(t, "WeaponBullet", StringComparison.OrdinalIgnoreCase))
                return WeaponBullet;

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

            if (string.Equals(t, Cockpit, StringComparison.OrdinalIgnoreCase))
                return Cockpit;
            if (string.Equals(t, Wing, StringComparison.OrdinalIgnoreCase))
                return Wing;
            if (string.Equals(t, Cargo, StringComparison.OrdinalIgnoreCase)
                || string.Equals(t, "Hold", StringComparison.OrdinalIgnoreCase)
                || string.Equals(t, "Storage", StringComparison.OrdinalIgnoreCase))
                return Cargo;

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
        /// Every family weapon name shares <see cref="WeaponBullet"/>.
        /// Gun, cannon, missile, rocket, and launcher are the same Part Profile.
        /// </summary>
        public static string ResolveWeaponSubtype(string componentIdOrName)
        {
            _ = componentIdOrName;
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
            if (IsCargoLikeName(componentId))
                return Cargo;
            if (id.IndexOf("cannon", StringComparison.Ordinal) >= 0
                || id.IndexOf("missile", StringComparison.Ordinal) >= 0
                || id.IndexOf("rocket", StringComparison.Ordinal) >= 0
                || id.IndexOf("launcher", StringComparison.Ordinal) >= 0
                || id.IndexOf("weapon", StringComparison.Ordinal) >= 0
                || id.IndexOf("gun", StringComparison.Ordinal) >= 0
                || id.IndexOf("barrel", StringComparison.Ordinal) >= 0
                || id.IndexOf("turret", StringComparison.Ordinal) >= 0
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
