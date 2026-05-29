using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;
using TitanOrbit.Core;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Per-component ability modifiers for a ship family part (e.g. AstroEagle Cockpit, Wing1, Engine2).
    /// Values are deltas applied when this component is present on the ship.
    /// </summary>
    [Serializable]
    public struct ShipComponentAbilityStats
    {
        [Header("Offense")]
        public float firePower;            // Fire Power (damage / shot strength)
        public float firePowerPerLevel;    // Fire Power gained per ship level
        public float bulletSpeed;          // Bullet Speed
        public float bulletSpeedPerLevel;  // Bullet Speed gained per ship level
        public float fireRate;             // Bullets per second
        public float fireRatePerLevel;     // Fire rate gained per ship level
        [Tooltip("Ramming / collision offense: base ramming power used in force and damage calculations.")]
        public float rammingPower;
        [Tooltip("Ramming power gained per ship level.")]
        public float rammingPowerPerLevel;

        [Header("Health")]
        public float healthCap;            // Max Health
        public float healthCapPerLevel;    // Max Health gained per ship level
        public float healthRegen;          // Health Regen
        public float healthRegenPerLevel;  // Health Regen gained per ship level

        [Header("Energy")]
        public float energyCap;            // Energy Capacity
        public float energyCapPerLevel;    // Energy Capacity gained per ship level
        public float energyRegen;          // Energy Regen
        public float energyRegenPerLevel;  // Energy Regen gained per ship level

        [Header("Movement")]
        [Tooltip("Engine/thruster: authoritative game units for thrust (sum) and max speed (best engine). Not multiplied by part scale—matches speedometer and physics cap.")]
        public float moveSpeed;
        [Tooltip("Not used for ship-level mobility (runtime: stat − (stat × 0.11) × (level − 1) on move/turn). Kept for data/editor aggregation.")]
        public float moveSpeedPerLevel;
        [Tooltip("Acceleration contribution. This is cumulative across all relevant components and independent from top speed cap.")]
        public float accelerationCap;
        [Tooltip("Acceleration gained per ship level.")]
        public float accelerationCapPerLevel;
        public float turnSpeed;            // Turn Speed (rotation speed)
        [Tooltip("Not used for ship-level mobility (runtime: stat − (stat × 0.11) × (level − 1) on move/turn). Kept for data/editor aggregation.")]
        public float turnSpeedPerLevel;

        [Header("Capacity")]
        public float maxGems;              // Gem Capacity
        public float maxGemsPerLevel;      // Gem Capacity gained per ship level
        public float maxPeople;            // People Capacity
        public float maxPeoplePerLevel;    // People Capacity gained per ship level

        public static ShipComponentAbilityStats operator +(ShipComponentAbilityStats a, ShipComponentAbilityStats b)
        {
            return new ShipComponentAbilityStats
            {
                firePower = a.firePower + b.firePower,
                firePowerPerLevel = a.firePowerPerLevel + b.firePowerPerLevel,
                bulletSpeed = a.bulletSpeed + b.bulletSpeed,
                bulletSpeedPerLevel = a.bulletSpeedPerLevel + b.bulletSpeedPerLevel,
                fireRate = a.fireRate + b.fireRate,
                fireRatePerLevel = a.fireRatePerLevel + b.fireRatePerLevel,
                healthCap = a.healthCap + b.healthCap,
                healthCapPerLevel = a.healthCapPerLevel + b.healthCapPerLevel,
                healthRegen = a.healthRegen + b.healthRegen,
                healthRegenPerLevel = a.healthRegenPerLevel + b.healthRegenPerLevel,
                energyCap = a.energyCap + b.energyCap,
                energyCapPerLevel = a.energyCapPerLevel + b.energyCapPerLevel,
                energyRegen = a.energyRegen + b.energyRegen,
                energyRegenPerLevel = a.energyRegenPerLevel + b.energyRegenPerLevel,
                moveSpeed = a.moveSpeed + b.moveSpeed,
                moveSpeedPerLevel = a.moveSpeedPerLevel + b.moveSpeedPerLevel,
                accelerationCap = a.accelerationCap + b.accelerationCap,
                accelerationCapPerLevel = a.accelerationCapPerLevel + b.accelerationCapPerLevel,
                turnSpeed = a.turnSpeed + b.turnSpeed,
                turnSpeedPerLevel = a.turnSpeedPerLevel + b.turnSpeedPerLevel,
                rammingPower = a.rammingPower + b.rammingPower,
                rammingPowerPerLevel = a.rammingPowerPerLevel + b.rammingPowerPerLevel,
                maxGems = a.maxGems + b.maxGems,
                maxGemsPerLevel = a.maxGemsPerLevel + b.maxGemsPerLevel,
                maxPeople = a.maxPeople + b.maxPeople,
                maxPeoplePerLevel = a.maxPeoplePerLevel + b.maxPeoplePerLevel
            };
        }

        public void AddInPlace(ShipComponentAbilityStats other)
        {
            firePower += other.firePower;
            firePowerPerLevel += other.firePowerPerLevel;
            bulletSpeed += other.bulletSpeed;
            bulletSpeedPerLevel += other.bulletSpeedPerLevel;
            fireRate += other.fireRate;
            fireRatePerLevel += other.fireRatePerLevel;
            healthCap += other.healthCap;
            healthCapPerLevel += other.healthCapPerLevel;
            healthRegen += other.healthRegen;
            healthRegenPerLevel += other.healthRegenPerLevel;
            energyCap += other.energyCap;
            energyCapPerLevel += other.energyCapPerLevel;
            energyRegen += other.energyRegen;
            energyRegenPerLevel += other.energyRegenPerLevel;
            moveSpeed += other.moveSpeed;
            moveSpeedPerLevel += other.moveSpeedPerLevel;
            accelerationCap += other.accelerationCap;
            accelerationCapPerLevel += other.accelerationCapPerLevel;
            turnSpeed += other.turnSpeed;
            turnSpeedPerLevel += other.turnSpeedPerLevel;
            rammingPower += other.rammingPower;
            rammingPowerPerLevel += other.rammingPowerPerLevel;
            maxGems += other.maxGems;
            maxGemsPerLevel += other.maxGemsPerLevel;
            maxPeople += other.maxPeople;
            maxPeoplePerLevel += other.maxPeoplePerLevel;
        }

        /// <summary>Multiply all ability values by a factor (e.g. average of localScale x,y,z). Used so stretched components contribute proportionally.</summary>
        public static ShipComponentAbilityStats operator *(ShipComponentAbilityStats s, float factor)
        {
            return new ShipComponentAbilityStats
            {
                firePower = s.firePower * factor,
                firePowerPerLevel = s.firePowerPerLevel * factor,
                bulletSpeed = s.bulletSpeed * factor,
                bulletSpeedPerLevel = s.bulletSpeedPerLevel * factor,
                fireRate = s.fireRate * factor,
                fireRatePerLevel = s.fireRatePerLevel * factor,
                healthCap = s.healthCap * factor,
                healthCapPerLevel = s.healthCapPerLevel * factor,
                healthRegen = s.healthRegen * factor,
                healthRegenPerLevel = s.healthRegenPerLevel * factor,
                energyCap = s.energyCap * factor,
                energyCapPerLevel = s.energyCapPerLevel * factor,
                energyRegen = s.energyRegen * factor,
                energyRegenPerLevel = s.energyRegenPerLevel * factor,
                moveSpeed = s.moveSpeed * factor,
                moveSpeedPerLevel = s.moveSpeedPerLevel * factor,
                accelerationCap = s.accelerationCap * factor,
                accelerationCapPerLevel = s.accelerationCapPerLevel * factor,
                turnSpeed = s.turnSpeed * factor,
                turnSpeedPerLevel = s.turnSpeedPerLevel * factor,
                rammingPower = s.rammingPower * factor,
                rammingPowerPerLevel = s.rammingPowerPerLevel * factor,
                maxGems = s.maxGems * factor,
                maxGemsPerLevel = s.maxGemsPerLevel * factor,
                maxPeople = s.maxPeople * factor,
                maxPeoplePerLevel = s.maxPeoplePerLevel * factor
            };
        }

        /// <summary>Scale factor from transform: arithmetic mean of localScale x, y, z (same idea as <see cref="ChassisComponentStats.GetScaleFactor"/>). (1,1,1)=1.</summary>
        public static float GetNormalizedScaleFromTransform(Transform t)
        {
            if (t == null) return 1f;
            Vector3 s = t.localScale;
            return (s.x + s.y + s.z) / 3f;
        }

        /// <summary>
        /// True if <paramref name="componentId"/> is a weapon for scaling rules: isolated "weapon" in the id (e.g. Weapon1, weapon(1), Main_Weapon_L),
        /// or legacy prefix "Weapon". Fire power uses average(x,y); fire rate uses 1/z; bullet speed is not scaled by part size.
        /// </summary>
        public static bool IsWeaponComponent(string componentId)
        {
            if (string.IsNullOrEmpty(componentId)) return false;
            string id = componentId.TrimStart();
            if (id.StartsWith("Weapon", StringComparison.OrdinalIgnoreCase)) return true;
            return ContainsIsolatedKeyword(id, "weapon");
        }

        /// <summary>
        /// True if engine for propulsion rules: isolated "engine" or "thrust", but not when id is a thruster (thruster contains "thrust" as substring).
        /// Engines use the same movement stats and aggregation as thrusters.
        /// </summary>
        public static bool IsEngineComponent(string componentId)
        {
            if (string.IsNullOrEmpty(componentId)) return false;
            string id = componentId.TrimStart();
            if (IsThrusterComponent(id)) return false;
            if (id.StartsWith("Engine", StringComparison.OrdinalIgnoreCase)) return true;
            return ContainsIsolatedKeyword(id, "engine") || ContainsIsolatedKeyword(id, "thrust");
        }

        /// <summary>
        /// True if thruster for mobility rules: isolated "thruster", or legacy prefix "Thruster". Checked before engine/thrust so names like Thruster_1 are not engines.
        /// </summary>
        public static bool IsThrusterComponent(string componentId)
        {
            if (string.IsNullOrEmpty(componentId)) return false;
            string id = componentId.TrimStart();
            if (id.StartsWith("Thruster", StringComparison.OrdinalIgnoreCase)) return true;
            return ContainsIsolatedKeyword(id, "thruster");
        }

        /// <summary>Engines and thrusters share propulsion aggregation (move speed + acceleration). Thrusters also contribute turn speed.</summary>
        public static bool IsPropulsionComponent(string componentId)
        {
            if (IsThrusterComponent(componentId) || IsEngineComponent(componentId))
                return true;

            string partType = ResolvePartTypeForSuggestedStats(componentId);
            return string.Equals(partType, "Engine", StringComparison.OrdinalIgnoreCase)
                || string.Equals(partType, "Thruster", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Keyword appears as its own token: not glued to letters on either side (digits, underscores, parens OK).
        /// Avoids false positives like "engineer" for "engine" or "finger" for "fin".
        /// </summary>
        private static bool ContainsIsolatedKeyword(string s, string keyword)
        {
            if (string.IsNullOrEmpty(s) || string.IsNullOrEmpty(keyword)) return false;
            int idx = 0;
            while ((idx = s.IndexOf(keyword, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                int end = idx + keyword.Length;
                bool okBefore = idx == 0 || !char.IsLetter(s[idx - 1]);
                bool okAfter = end >= s.Length || !char.IsLetter(s[end]);
                if (okBefore && okAfter)
                    return true;
                idx++;
            }
            return false;
        }

        /// <summary>
        /// Maps a component id suffix (after FamilyId_) to the part type string used by editor auto-populate stats heuristics.
        /// Uses isolated keywords (weapon, engine, wing, …) then falls back to the first underscore segment for exact switch matches.
        /// </summary>
        public static string ResolvePartTypeForSuggestedStats(string componentIdRest)
        {
            if (string.IsNullOrWhiteSpace(componentIdRest)) return string.Empty;
            string s = componentIdRest.Trim();
            if (ContainsIsolatedKeyword(s, "cockpit")) return "Cockpit";
            if (ContainsIsolatedKeyword(s, "thruster")) return "Thruster";
            if (ContainsIsolatedKeyword(s, "thrustcover")) return "Thruster";
            if (ContainsIsolatedKeyword(s, "weapon")) return "Weapon";
            if (ContainsIsolatedKeyword(s, "gun")) return "Weapon";
            if (ContainsIsolatedKeyword(s, "machinegun")) return "Weapon";
            if (ContainsIsolatedKeyword(s, "missile")) return "Weapon";
            if (ContainsIsolatedKeyword(s, "ammunition")) return "Weapon";
            if (ContainsIsolatedKeyword(s, "barrel")) return "Weapon";
            if (ContainsIsolatedKeyword(s, "engine")) return "Engine";
            if (ContainsIsolatedKeyword(s, "wing")) return "Wing";
            if (ContainsIsolatedKeyword(s, "wingholder")) return "Wing";
            if (ContainsIsolatedKeyword(s, "arm")) return "Arm";
            if (ContainsIsolatedKeyword(s, "fin")) return "Fin";
            if (ContainsIsolatedKeyword(s, "tail")) return "Tail";
            if (ContainsIsolatedKeyword(s, "hull")) return "Hull";
            if (ContainsIsolatedKeyword(s, "mainbody")) return "Hull";
            if (ContainsIsolatedKeyword(s, "body")) return "Hull";
            if (ContainsIsolatedKeyword(s, "armor")) return "Hull";
            if (ContainsIsolatedKeyword(s, "part")) return "Part";
            if (ContainsIsolatedKeyword(s, "core")) return "Core";
            if (ContainsIsolatedKeyword(s, "solar")) return "Solar";
            if (ContainsIsolatedKeyword(s, "sensor")) return "Sensor";
            if (ContainsIsolatedKeyword(s, "track")) return "Engine";
            if (ContainsIsolatedKeyword(s, "thrust")) return "Engine";

            string[] parts = s.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0 && !string.IsNullOrEmpty(parts[0]))
                return parts[0];
            return string.Empty;
        }

        /// <summary>
        /// Scale stats by transform.
        /// Weapons: fire power scales by average(x,y); fire rate scales by 1/z (smaller z = faster).
        ///          Bullet speed uses authored values only (not scaled by weapon transform).
        ///          Other weapon properties (health, energy, etc.) are NOT scaled by transform.
        /// Non-weapons: stats scale by average(x,y,z) except turn speed and engine/thruster move speed (authored as-is).
        /// <c>Starship</c> converts turn definition units to degrees per second when applying rotation.
        /// </summary>
        public static ShipComponentAbilityStats ScaleStatsByTransform(ShipComponentAbilityStats stats, Transform t, string componentId)
        {
            if (t == null) return stats;
            float x = t.localScale.x;
            float y = t.localScale.y;
            float z = Mathf.Max(t.localScale.z, 0.01f);

            if (IsWeaponComponent(componentId))
            {
                float firePowerScale = (x + y) * 0.5f; // average of x and y for damage / fire power
                float fireRateScale = 1f / z;       // smaller z = faster rate of fire
                return new ShipComponentAbilityStats
                {
                    firePower = stats.firePower * firePowerScale,
                    firePowerPerLevel = stats.firePowerPerLevel * firePowerScale,
                    bulletSpeed = stats.bulletSpeed,
                    bulletSpeedPerLevel = stats.bulletSpeedPerLevel,
                    fireRate = stats.fireRate * fireRateScale,
                    fireRatePerLevel = stats.fireRatePerLevel * fireRateScale,
                    // z-scale only affects fire rate; average(x,y) scales fire power. Bullet speed is not scaled by weapon part size.
                    healthCap = stats.healthCap,
                    healthCapPerLevel = stats.healthCapPerLevel,
                    healthRegen = stats.healthRegen,
                    healthRegenPerLevel = stats.healthRegenPerLevel,
                    energyCap = stats.energyCap,
                    energyCapPerLevel = stats.energyCapPerLevel,
                    energyRegen = stats.energyRegen,
                    energyRegenPerLevel = stats.energyRegenPerLevel,
                    moveSpeed = stats.moveSpeed,
                    moveSpeedPerLevel = stats.moveSpeedPerLevel,
                    accelerationCap = stats.accelerationCap,
                    accelerationCapPerLevel = stats.accelerationCapPerLevel,
                    turnSpeed = stats.turnSpeed,
                    turnSpeedPerLevel = stats.turnSpeedPerLevel,
                    rammingPower = stats.rammingPower,
                    rammingPowerPerLevel = stats.rammingPowerPerLevel,
                    maxGems = stats.maxGems,
                    maxGemsPerLevel = stats.maxGemsPerLevel,
                    maxPeople = stats.maxPeople,
                    maxPeoplePerLevel = stats.maxPeoplePerLevel
                };
            }

            float scale = (x + y + z) / 3f;
            ShipComponentAbilityStats scaled = stats * scale;
            scaled.turnSpeed = stats.turnSpeed;
            scaled.turnSpeedPerLevel = stats.turnSpeedPerLevel;
            scaled.rammingPower = stats.rammingPower;
            scaled.rammingPowerPerLevel = stats.rammingPowerPerLevel;
            // Do not scale engine/thruster move speed by part volume—designers tune these to match gameplay speeds.
            if (IsPropulsionComponent(componentId))
            {
                scaled.moveSpeed = stats.moveSpeed;
                scaled.moveSpeedPerLevel = stats.moveSpeedPerLevel;
                scaled.accelerationCap = stats.accelerationCap;
                scaled.accelerationCapPerLevel = stats.accelerationCapPerLevel;
            }
            return scaled;
        }

        /// <summary>Zeroes all stat fields outside <paramref name="category"/> so each component contributes to one category only.</summary>
        public static ShipComponentAbilityStats KeepOnlyCategory(ShipComponentAbilityStats stats, ShipComponentStatCategory category)
        {
            var filtered = new ShipComponentAbilityStats();
            switch (category)
            {
                case ShipComponentStatCategory.Offense:
                    filtered.firePower = stats.firePower;
                    filtered.firePowerPerLevel = stats.firePowerPerLevel;
                    filtered.bulletSpeed = stats.bulletSpeed;
                    filtered.bulletSpeedPerLevel = stats.bulletSpeedPerLevel;
                    filtered.fireRate = stats.fireRate;
                    filtered.fireRatePerLevel = stats.fireRatePerLevel;
                    filtered.rammingPower = stats.rammingPower;
                    filtered.rammingPowerPerLevel = stats.rammingPowerPerLevel;
                    break;
                case ShipComponentStatCategory.Health:
                    filtered.healthCap = stats.healthCap;
                    filtered.healthCapPerLevel = stats.healthCapPerLevel;
                    filtered.healthRegen = stats.healthRegen;
                    filtered.healthRegenPerLevel = stats.healthRegenPerLevel;
                    break;
                case ShipComponentStatCategory.Energy:
                    filtered.energyCap = stats.energyCap;
                    filtered.energyCapPerLevel = stats.energyCapPerLevel;
                    filtered.energyRegen = stats.energyRegen;
                    filtered.energyRegenPerLevel = stats.energyRegenPerLevel;
                    break;
                case ShipComponentStatCategory.Movement:
                    filtered.moveSpeed = stats.moveSpeed;
                    filtered.moveSpeedPerLevel = stats.moveSpeedPerLevel;
                    filtered.accelerationCap = stats.accelerationCap;
                    filtered.accelerationCapPerLevel = stats.accelerationCapPerLevel;
                    filtered.turnSpeed = stats.turnSpeed;
                    filtered.turnSpeedPerLevel = stats.turnSpeedPerLevel;
                    break;
                case ShipComponentStatCategory.Capacity:
                    filtered.maxGems = stats.maxGems;
                    filtered.maxGemsPerLevel = stats.maxGemsPerLevel;
                    filtered.maxPeople = stats.maxPeople;
                    filtered.maxPeoplePerLevel = stats.maxPeoplePerLevel;
                    break;
            }
            return filtered;
        }

        /// <summary>Keeps only the stat fields allowed for this component's categories and part id.</summary>
        public static ShipComponentAbilityStats KeepOnlyAuthoringFields(
            ShipComponentAbilityStats stats,
            IReadOnlyList<ShipComponentStatCategory> categories,
            string componentId)
        {
            string[] allowed = ShipFamilyComponentPartKey.GetAuthoringStatFieldNames(categories, componentId);
            var allowedSet = new HashSet<string>(allowed, StringComparer.Ordinal);
            var filtered = new ShipComponentAbilityStats();

            if (allowedSet.Contains("firePower")) filtered.firePower = stats.firePower;
            if (allowedSet.Contains("firePowerPerLevel")) filtered.firePowerPerLevel = stats.firePowerPerLevel;
            if (allowedSet.Contains("bulletSpeed")) filtered.bulletSpeed = stats.bulletSpeed;
            if (allowedSet.Contains("bulletSpeedPerLevel")) filtered.bulletSpeedPerLevel = stats.bulletSpeedPerLevel;
            if (allowedSet.Contains("fireRate")) filtered.fireRate = stats.fireRate;
            if (allowedSet.Contains("fireRatePerLevel")) filtered.fireRatePerLevel = stats.fireRatePerLevel;
            if (allowedSet.Contains("rammingPower")) filtered.rammingPower = stats.rammingPower;
            if (allowedSet.Contains("rammingPowerPerLevel")) filtered.rammingPowerPerLevel = stats.rammingPowerPerLevel;
            if (allowedSet.Contains("healthCap")) filtered.healthCap = stats.healthCap;
            if (allowedSet.Contains("healthCapPerLevel")) filtered.healthCapPerLevel = stats.healthCapPerLevel;
            if (allowedSet.Contains("healthRegen")) filtered.healthRegen = stats.healthRegen;
            if (allowedSet.Contains("healthRegenPerLevel")) filtered.healthRegenPerLevel = stats.healthRegenPerLevel;
            if (allowedSet.Contains("energyCap")) filtered.energyCap = stats.energyCap;
            if (allowedSet.Contains("energyCapPerLevel")) filtered.energyCapPerLevel = stats.energyCapPerLevel;
            if (allowedSet.Contains("energyRegen")) filtered.energyRegen = stats.energyRegen;
            if (allowedSet.Contains("energyRegenPerLevel")) filtered.energyRegenPerLevel = stats.energyRegenPerLevel;
            if (allowedSet.Contains("moveSpeed")) filtered.moveSpeed = stats.moveSpeed;
            if (allowedSet.Contains("moveSpeedPerLevel")) filtered.moveSpeedPerLevel = stats.moveSpeedPerLevel;
            if (allowedSet.Contains("accelerationCap")) filtered.accelerationCap = stats.accelerationCap;
            if (allowedSet.Contains("accelerationCapPerLevel")) filtered.accelerationCapPerLevel = stats.accelerationCapPerLevel;
            if (allowedSet.Contains("turnSpeed")) filtered.turnSpeed = stats.turnSpeed;
            if (allowedSet.Contains("turnSpeedPerLevel")) filtered.turnSpeedPerLevel = stats.turnSpeedPerLevel;
            if (allowedSet.Contains("maxGems")) filtered.maxGems = stats.maxGems;
            if (allowedSet.Contains("maxGemsPerLevel")) filtered.maxGemsPerLevel = stats.maxGemsPerLevel;
            if (allowedSet.Contains("maxPeople")) filtered.maxPeople = stats.maxPeople;
            if (allowedSet.Contains("maxPeoplePerLevel")) filtered.maxPeoplePerLevel = stats.maxPeoplePerLevel;

            return filtered;
        }

        /// <summary>Keeps only the stat fields allowed for a single category (convenience wrapper).</summary>
        public static ShipComponentAbilityStats KeepOnlyAuthoringFields(
            ShipComponentAbilityStats stats,
            ShipComponentStatCategory category,
            string componentId)
        {
            return KeepOnlyAuthoringFields(stats, new[] { category }, componentId);
        }
    }

    /// <summary>High-level stat bucket assigned to a ship part (components may use one or more).</summary>
    public enum ShipComponentStatCategory
    {
        Offense = 0,
        Health = 1,
        Energy = 2,
        Movement = 3,
        Capacity = 4
    }

    /// <summary>Parses component ids into mapping keys and provides default stat-category inference.</summary>
    public static class ShipFamilyComponentPartKey
    {
        private static readonly Regex TrailingDigitsRegex = new Regex(@"\d+$", RegexOptions.Compiled);

        /// <summary>Related part names that share the same mapping key (e.g. ThrustCover → Thruster, WingHolder → Wing).</summary>
        private static readonly Dictionary<string, string> AliasToCanonical =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "ThrustCover", "Thruster" },
                { "WingHolder", "Wing" },
                { "Gun", "Weapon" },
                { "Machinegun", "Weapon" },
                { "Missile", "Weapon" },
                { "Missile_Launcher", "Weapon" },
                { "Barrel", "Weapon" },
                { "Ammunition", "Weapon" },
                { "EngineComp1", "Engine" },
                { "EngineComp2", "Engine" },
                { "Engine_1", "Engine" },
                { "Engine_2", "Engine" },
                { "Engine1", "Engine" },
                { "Engine2", "Engine" },
                { "Thrusters", "Thruster" },
                { "Thrusters_Big", "Thruster" },
                { "Tiny_Thrusters", "Thruster" },
                { "Thruster_Place", "Thruster" },
                { "Small_Wing", "Wing" },
                { "Tiny_Wing", "Wing" },
                { "WingMain", "Wing" },
                { "WingMini", "Wing" },
                { "WingTip", "Wing" },
                { "WingWide", "Wing" },
                { "Cockpit_Base", "Cockpit" },
                { "Cockpit_Base_1", "Cockpit" },
                { "Cockpit_Base_2", "Cockpit" },
                { "CockpitCover", "Cockpit" },
                { "MainBody1", "MainBody" },
                { "MainBody2", "MainBody" },
                { "MainBody3", "MainBody" },
                { "MainBody4", "MainBody" },
                { "Body_01", "Body" },
                { "Body_02", "Body" },
                { "Body_03", "Body" },
                { "Body1", "Body" },
                { "Body2", "Body" },
                { "Armor_01", "Armor" },
                { "Armor_02", "Armor" },
                { "Part_1", "Part" },
                { "Part_2", "Part" },
                { "Acc", "Part" },
                { "Wing_01", "Wing" },
                { "Wing_02", "Wing" },
                { "Wing_03", "Wing" },
                { "Wing_1", "Wing" },
                { "Wing_2", "Wing" },
                { "Wing_3", "Wing" },
                { "Wing_4", "Wing" },
                { "Wing_5", "Wing" },
                { "Wing1", "Wing" },
                { "Wing2", "Wing" },
                { "Wing3", "Wing" },
                { "Wing4", "Wing" },
            };

        /// <summary>Strips version suffixes: Wing1 → Wing, Wing_3 → Wing, MainBody4 → MainBody.</summary>
        public static string GetBasePartKey(string componentId)
        {
            string s = ShipFamilyDefinition.NormalizeComponentId(componentId);
            if (string.IsNullOrEmpty(s))
                return string.Empty;

            if (AliasToCanonical.TryGetValue(s, out string alias))
                return alias;

            string[] segments = s.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2 && TrailingDigitsRegex.IsMatch(segments[segments.Length - 1]))
            {
                var trimmed = new List<string>(segments.Length - 1);
                for (int i = 0; i < segments.Length - 1; i++)
                    trimmed.Add(segments[i]);
                string joined = string.Join("_", trimmed);
                if (!string.IsNullOrEmpty(joined))
                    return joined;
            }

            string withoutDigits = TrailingDigitsRegex.Replace(s, string.Empty);
            return string.IsNullOrEmpty(withoutDigits) ? s : withoutDigits;
        }

        /// <summary>Returns the canonical related-part key when one exists (ThrustCover → Thruster).</summary>
        public static string ResolveAliasKey(string componentId)
        {
            string s = ShipFamilyDefinition.NormalizeComponentId(componentId);
            if (string.IsNullOrEmpty(s))
                return string.Empty;
            return AliasToCanonical.TryGetValue(s, out string alias) ? alias : s;
        }

        /// <summary>Default stat categories from part keywords when scanning or migrating component entries.</summary>
        public static List<ShipComponentStatCategory> InferDefaultStatCategories(string componentId)
        {
            string partType = ShipComponentAbilityStats.ResolvePartTypeForSuggestedStats(componentId);
            switch (partType)
            {
                case "Cockpit":
                    return new List<ShipComponentStatCategory>
                    {
                        ShipComponentStatCategory.Offense,
                        ShipComponentStatCategory.Health,
                        ShipComponentStatCategory.Capacity
                    };
                case "Engine":
                case "Thruster":
                case "Fin":
                case "Tail":
                    return new List<ShipComponentStatCategory> { ShipComponentStatCategory.Movement };
                case "Wing":
                case "Arm":
                    return new List<ShipComponentStatCategory>
                    {
                        ShipComponentStatCategory.Health,
                        ShipComponentStatCategory.Capacity
                    };
                case "Weapon":
                    return new List<ShipComponentStatCategory>
                    {
                        ShipComponentStatCategory.Offense,
                        ShipComponentStatCategory.Energy
                    };
                default:
                    return new List<ShipComponentStatCategory> { ShipComponentStatCategory.Health };
            }
        }

        /// <summary>First default category (legacy / CSV export).</summary>
        public static ShipComponentStatCategory InferDefaultStatCategory(string componentId)
        {
            var categories = InferDefaultStatCategories(componentId);
            return categories.Count > 0 ? categories[0] : ShipComponentStatCategory.Health;
        }

        private static readonly ShipComponentStatCategory[] CategoryDisplayOrder =
        {
            ShipComponentStatCategory.Offense,
            ShipComponentStatCategory.Health,
            ShipComponentStatCategory.Energy,
            ShipComponentStatCategory.Movement,
            ShipComponentStatCategory.Capacity
        };

        private static readonly string[] RammingOffenseFields = { "rammingPower", "rammingPowerPerLevel" };
        private static readonly string[] WeaponOffenseFields =
        {
            "firePower", "firePowerPerLevel", "bulletSpeed", "bulletSpeedPerLevel",
            "fireRate", "fireRatePerLevel"
        };
        private static readonly string[] HealthFields =
            { "healthCap", "healthCapPerLevel", "healthRegen", "healthRegenPerLevel" };
        private static readonly string[] EnergyFields =
            { "energyCap", "energyCapPerLevel", "energyRegen", "energyRegenPerLevel" };
        private static readonly string[] PropulsionMovementFields =
            { "moveSpeed", "moveSpeedPerLevel", "accelerationCap", "accelerationCapPerLevel" };
        private static readonly string[] ThrusterMovementFields =
        {
            "moveSpeed", "moveSpeedPerLevel", "accelerationCap", "accelerationCapPerLevel",
            "turnSpeed", "turnSpeedPerLevel"
        };
        private static readonly string[] TurnMovementFields = { "turnSpeed", "turnSpeedPerLevel" };
        private static readonly string[] CapacityFields =
            { "maxGems", "maxGemsPerLevel", "maxPeople", "maxPeoplePerLevel" };

        /// <summary>Stat fields shown and stored for a component based on category and part id.</summary>
        public static string[] GetAuthoringStatFieldNames(ShipComponentStatCategory category, string componentId)
        {
            string partType = ShipComponentAbilityStats.ResolvePartTypeForSuggestedStats(componentId);
            switch (category)
            {
                case ShipComponentStatCategory.Offense:
                    return partType == "Cockpit" ? RammingOffenseFields : WeaponOffenseFields;
                case ShipComponentStatCategory.Health:
                    return HealthFields;
                case ShipComponentStatCategory.Energy:
                    return EnergyFields;
                case ShipComponentStatCategory.Movement:
                    if (partType == "Thruster")
                        return ThrusterMovementFields;
                    if (partType == "Engine")
                        return PropulsionMovementFields;
                    if (partType == "Fin" || partType == "Tail")
                        return TurnMovementFields;
                    return PropulsionMovementFields;
                case ShipComponentStatCategory.Capacity:
                    return CapacityFields;
                default:
                    return HealthFields;
            }
        }

        private static bool ContainsStatCategory(
            IReadOnlyList<ShipComponentStatCategory> categories,
            ShipComponentStatCategory category)
        {
            for (int i = 0; i < categories.Count; i++)
            {
                if (categories[i] == category)
                    return true;
            }

            return false;
        }

        /// <summary>Union of stat fields for all assigned categories (stable display order).</summary>
        public static string[] GetAuthoringStatFieldNames(
            IReadOnlyList<ShipComponentStatCategory> categories,
            string componentId)
        {
            if (categories == null || categories.Count == 0)
                return GetAuthoringStatFieldNames(ShipComponentStatCategory.Health, componentId);

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var ordered = new List<string>();
            for (int i = 0; i < CategoryDisplayOrder.Length; i++)
            {
                ShipComponentStatCategory category = CategoryDisplayOrder[i];
                if (!ContainsStatCategory(categories, category))
                    continue;

                string[] fields = GetAuthoringStatFieldNames(category, componentId);
                for (int f = 0; f < fields.Length; f++)
                {
                    if (seen.Add(fields[f]))
                        ordered.Add(fields[f]);
                }
            }

            return ordered.ToArray();
        }

        /// <summary>True when any assigned category is offense and the part is a weapon (not cockpit).</summary>
        public static bool ShouldShowBulletPrefabIndex(
            IReadOnlyList<ShipComponentStatCategory> categories,
            string componentId)
        {
            if (categories == null || categories.Count == 0)
                return false;

            for (int i = 0; i < categories.Count; i++)
            {
                if (ShouldShowBulletPrefabIndex(categories[i], componentId))
                    return true;
            }

            return false;
        }

        /// <summary>True when the offense category component should expose bullet prefab index (weapons only).</summary>
        public static bool ShouldShowBulletPrefabIndex(ShipComponentStatCategory category, string componentId)
        {
            if (category != ShipComponentStatCategory.Offense)
                return false;
            return !string.Equals(
                ShipComponentAbilityStats.ResolvePartTypeForSuggestedStats(componentId),
                "Cockpit",
                StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Scan/auto-populate health caps and regen for all parts with a Health category (cockpit, wing, hull, part, …).</summary>
    public static class ShipComponentHealthSuggestions
    {
        /// <summary>Health cap at version 1 (¼ of legacy 21).</summary>
        public const float HealthCapV1 = 5.25f;

        /// <summary>Health cap added per version tier (v2 = 6.75, v3 = 8.25, … — same curve for every health part).</summary>
        public const float HealthCapPerVersion = 1.5f;

        /// <summary>Health regen as a fraction of cap (legacy ratio 0.75 / 21).</summary>
        public const float HealthRegenFractionOfCap = 0.75f / 21f;

        /// <summary>Health cap from version: v1=5.25, v2=6.75, v3=8.25, v4=9.75, …</summary>
        public static float GetSuggestedHealthCap(int version)
        {
            int v = Mathf.Max(1, version);
            return HealthCapV1 + (v - 1) * HealthCapPerVersion;
        }

        public static float GetSuggestedHealthRegen(int version) =>
            GetSuggestedHealthCap(version) * HealthRegenFractionOfCap;

        public static float GetSuggestedHealthCapPerLevel(int version) =>
            GetSuggestedHealthCap(version) * ShipPropulsionAggregation.PerLevelFractionOfBase;

        public static float GetSuggestedHealthRegenPerLevel(int version) =>
            GetSuggestedHealthRegen(version) * ShipPropulsionAggregation.PerLevelFractionOfBase;
    }

    /// <summary>Scan/auto-populate cockpit ramming offense (scaled down with lower ship health).</summary>
    public static class ShipComponentRammingSuggestions
    {
        /// <summary>Ramming power at version 1 (cockpit).</summary>
        public const float RammingPowerV1 = 0.75f;

        /// <summary>Ramming power added per version tier (v2 = 1.0, v3 = 1.25, …).</summary>
        public const float RammingPowerPerVersion = 0.25f;

        public static float GetSuggestedRammingPower(int version)
        {
            int v = Mathf.Max(1, version);
            return RammingPowerV1 + (v - 1) * RammingPowerPerVersion;
        }

        public static float GetSuggestedRammingPowerPerLevel(int version) =>
            GetSuggestedRammingPower(version) * ShipPropulsionAggregation.PerLevelFractionOfBase;
    }

    /// <summary>Scan/auto-populate turn speed for fins, tails, and thrusters (summed at runtime).</summary>
    public static class ShipComponentTurnSpeedSuggestions
    {
        /// <summary>
        /// After thrusters gained turn speed, typical summed totals rose ~37 vs the prior ~22 target.
        /// Scale all turn-speed parts by this ratio so ship totals match the old feel.
        /// </summary>
        public const float ComponentTurnSpeedScale = 22f / 37f;

        public const float FinTurnSpeedPerVersion = 7f;
        public const float TailTurnSpeedPerVersion = 11f;
        public const float ThrusterTurnSpeedV1 = 5f;
        public const float ThrusterTurnSpeedPerVersion = 1f;

        public static float GetSuggestedFinTurnSpeed(int version)
        {
            int v = Mathf.Max(1, version);
            return FinTurnSpeedPerVersion * v * ComponentTurnSpeedScale;
        }

        public static float GetSuggestedTailTurnSpeed(int version)
        {
            int v = Mathf.Max(1, version);
            return TailTurnSpeedPerVersion * v * ComponentTurnSpeedScale;
        }

        public static float GetSuggestedThrusterTurnSpeed(int version)
        {
            int v = Mathf.Max(1, version);
            return (ThrusterTurnSpeedV1 + (v - 1) * ThrusterTurnSpeedPerVersion) * ComponentTurnSpeedScale;
        }

        public static float GetSuggestedTurnSpeedPerLevel(float baseTurnSpeed) =>
            baseTurnSpeed * ShipPropulsionAggregation.PerLevelFractionOfBase;
    }

    /// <summary>
    /// One named component entry within a ship family, e.g. "Cockpit", "Wing1", "Weapon_1".
    /// </summary>
    [Serializable]
    public class ShipFamilyComponentEntry
    {
        [Tooltip("Component identifier after the family name and underscore. Example: for AstroEagle_Cockpit the id is \"Cockpit\".")]
        public string componentId;

        [Tooltip("Optional friendly label for editor-only use.")]
        public string displayName;

        [SerializeField, HideInInspector]
        private ShipComponentStatCategory statCategory = ShipComponentStatCategory.Health;

        [Tooltip("Stat categories for this component. Weapons: Offense + Energy. Cockpits: Offense + Health + Capacity. Wings: Health + Capacity.")]
        public List<ShipComponentStatCategory> statCategories = new List<ShipComponentStatCategory>();

        [Tooltip("Ability stat modifiers contributed by this component.")]
        public ShipComponentAbilityStats stats;

        /// <summary>Ensures <see cref="statCategories"/> is populated (migrates legacy single category when needed).</summary>
        public void EnsureStatCategories()
        {
            if (statCategories == null)
                statCategories = new List<ShipComponentStatCategory>();

            if (statCategories.Count > 0)
            {
                ApplyLegacyCategoryUpgrades();
                DedupeStatCategories();
                return;
            }

            statCategories.Add(statCategory);
            ApplyLegacyCategoryUpgrades();

            if (statCategories.Count == 0 && !string.IsNullOrWhiteSpace(componentId))
                statCategories.AddRange(ShipFamilyComponentPartKey.InferDefaultStatCategories(componentId));
            else if (statCategories.Count == 0)
                statCategories.Add(ShipComponentStatCategory.Health);

            DedupeStatCategories();
        }

        private void ApplyLegacyCategoryUpgrades()
        {
            string partType = ShipComponentAbilityStats.ResolvePartTypeForSuggestedStats(componentId);
            if (string.Equals(partType, "Weapon", StringComparison.OrdinalIgnoreCase) &&
                statCategories.Contains(ShipComponentStatCategory.Offense) &&
                !statCategories.Contains(ShipComponentStatCategory.Energy))
            {
                statCategories.Add(ShipComponentStatCategory.Energy);
            }

            if (string.Equals(partType, "Engine", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(partType, "Thruster", StringComparison.OrdinalIgnoreCase))
            {
                if (statCategories.Contains(ShipComponentStatCategory.Energy))
                    statCategories.Remove(ShipComponentStatCategory.Energy);
                if (!statCategories.Contains(ShipComponentStatCategory.Movement))
                    statCategories.Add(ShipComponentStatCategory.Movement);
            }

            if (string.Equals(partType, "Cockpit", StringComparison.OrdinalIgnoreCase))
            {
                EnsureStatCategory(ShipComponentStatCategory.Health);
                EnsureStatCategory(ShipComponentStatCategory.Capacity);
            }

            if (string.Equals(partType, "Wing", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(partType, "Arm", StringComparison.OrdinalIgnoreCase))
            {
                EnsureStatCategory(ShipComponentStatCategory.Health);
                EnsureStatCategory(ShipComponentStatCategory.Capacity);
            }
        }

        private void EnsureStatCategory(ShipComponentStatCategory category)
        {
            if (!statCategories.Contains(category))
                statCategories.Add(category);
        }

        private void DedupeStatCategories()
        {
            if (statCategories == null || statCategories.Count <= 1)
                return;

            var seen = new HashSet<ShipComponentStatCategory>();
            var deduped = new List<ShipComponentStatCategory>(statCategories.Count);
            for (int i = 0; i < statCategories.Count; i++)
            {
                if (seen.Add(statCategories[i]))
                    deduped.Add(statCategories[i]);
            }

            statCategories = deduped;
        }

        [Tooltip("For weapons: index into CombatSystem's Bullet Prefab Bank. -1 = use family default (ShipFamilyDefinition.bulletPrefabIndex).")]
        public int bulletPrefabIndex = -1;
    }

    /// <summary>
    /// Heuristic breakdown of <see cref="ShipFamilyChassisTierEntry.powerScore"/> (offense + defense + energy + mobility + capacity).
    /// Populated when building the upgrade tree from folder in the editor.
    /// </summary>
    [Serializable]
    public struct ShipFamilyPowerScoreBreakdown
    {
        [Tooltip("Weighted offense contribution (fire power, bullet speed, fire rate, per-level terms).")]
        public float offense;
        [Tooltip("Weighted defense contribution (health cap/regen, per-level terms).")]
        public float defense;
        [Tooltip("Weighted energy contribution (energy cap/regen, per-level terms).")]
        public float energy;
        [Tooltip("Weighted mobility contribution (move speed, turn speed, per-level terms).")]
        public float mobility;
        [Tooltip("Weighted capacity contribution (gems, people, per-level terms).")]
        public float capacity;

        public float Total => offense + defense + energy + mobility + capacity;

        /// <summary>
        /// Heuristic category weights from summed ship stats (same formula as the upgrade-tree editor power breakdown).
        /// Input stats must already include per-component localScale (see ShipFamilyUpgradeTreeStatScanner in the Editor assembly).
        /// Used to bias generated upgrade cards toward what the family's prefabs are strong in.
        /// </summary>
        public static ShipFamilyPowerScoreBreakdown FromSummedShipStats(ShipComponentAbilityStats s)
        {
            return new ShipFamilyPowerScoreBreakdown
            {
                offense =
                    s.firePower * 2.0f +
                    s.firePowerPerLevel * 1.0f +
                    s.bulletSpeed * 0.5f +
                    s.bulletSpeedPerLevel * 0.25f +
                    s.fireRate * 1.0f +
                    s.fireRatePerLevel * 0.5f +
                    s.rammingPower * 0.9f +
                    s.rammingPowerPerLevel * 1.1f,
                defense =
                    s.healthCap * 0.03f +
                    s.healthCapPerLevel * 0.5f +
                    s.healthRegen * 1.0f +
                    s.healthRegenPerLevel * 1.5f,
                energy =
                    s.energyCap * 0.01f +
                    s.energyCapPerLevel * 0.25f +
                    s.energyRegen * 0.8f +
                    s.energyRegenPerLevel * 1.0f,
                mobility =
                    s.moveSpeed * 0.5f +
                    s.moveSpeedPerLevel * 0.8f +
                    s.accelerationCap * 0.9f +
                    s.accelerationCapPerLevel * 1.1f +
                    s.turnSpeed * 0.6f +
                    s.turnSpeedPerLevel * 0.9f,
                capacity =
                    s.maxGems * 0.01f +
                    s.maxGemsPerLevel * 0.2f +
                    s.maxPeople * 0.5f +
                    s.maxPeoplePerLevel * 0.8f
            };
        }
    }

    /// <summary>
    /// One chassis/variant in the family upgrade tree.
    /// </summary>
    [Serializable]
    public class ShipFamilyChassisTierEntry
    {
        [Tooltip("Chassis identifier, e.g. AstroEagle_01.")]
        public string chassisId;

        [Tooltip("Player-facing name in the orbit upgrade tree only. Not the chassis ID; leave empty to fall back to Upgrade Tree node / ShipData names.")]
        public string upgradeTreeShipName;

        [Tooltip("Prefab representing this chassis variant (from the family folder).")]
        public GameObject prefab;

        [Tooltip("Orbit store / upgrade tree thumbnail. Assign manually or generate in editor (Ship Family inspector: Generate Menu Preview Images).")]
        public Sprite menuPreviewSprite;
        [Tooltip("Per-team/material-variant menu preview sprites generated from this family's team material sets.")]
        public List<ShipFamilyMenuPreviewSprite> teamMenuPreviewSprites = new List<ShipFamilyMenuPreviewSprite>();

        [Tooltip("Minimum home planet level required to unlock this chassis in the upgrade tree.")]
        public int minHomePlanetLevel = 1;

        [Tooltip("Approximate overall power score used for auto-ordering (higher = stronger). Sum of power score breakdown categories.")]
        public float powerScore;

        [Tooltip("Editor: heuristic parts of powerScore (offense + defense + energy + mobility + capacity).")]
        public ShipFamilyPowerScoreBreakdown powerScoreBreakdown;

        [Tooltip("When enabled, Resort Upgrade Tree keeps this entry at its list index and only re-sorts unlocked ships.")]
        public bool lockedInUpgradeTree;
    }

    [Serializable]
    public class ShipFamilyMenuPreviewSprite
    {
        [Tooltip("Variant label used in file names and lookup (e.g. TeamA, Red, Blue).")]
        public string variantName;
        [Tooltip("Optional team this preview corresponds to.")]
        public TeamManager.Team team = TeamManager.Team.None;
        public Sprite sprite;
    }

    [Serializable]
    public class ShipFamilyTeamMaterialSet
    {
        [Tooltip("Optional label for this material set (e.g. Red, Blue, Orange). Used for menu preview variant names.")]
        public string variantName;
        [Tooltip("Team this material list applies to.")]
        public TeamManager.Team team = TeamManager.Team.TeamA;
        [Tooltip("Materials used for this team. They are assigned to ship component renderers in slot order (cycled if needed).")]
        public List<Material> materials = new List<Material>();
    }

    /// <summary>
    /// ScriptableObject describing all component stats for a single ship family (e.g. AstroEagle).
    /// Child GameObjects named "Family_ComponentId" can be mapped to entries here.
    /// </summary>
    [CreateAssetMenu(fileName = "NewShipFamily", menuName = "Titan Orbit/Ship Family Definition")]
    public class ShipFamilyDefinition : ScriptableObject
    {
        [Tooltip("Ship family identifier prefix used in child names. Example: 'AstroEagle' for objects named 'AstroEagle_Cockpit'.")]
        public string familyId;

        [Header("Bullets")]
        [Tooltip("Index into CombatSystem's Bullet Prefab Bank (CombatSystem.bulletPrefabBank). 0 = first prefab. Weapon components can override per-cannon via ShipFamilyComponentEntry.bulletPrefabIndex. Same list/order on all builds for networking.")]
        public int bulletPrefabIndex = 0;

        [Header("Components")]
        [Tooltip("All components (cockpit, wings, engines, weapons, etc.) and their ability stat modifiers for this family.")]
        public List<ShipFamilyComponentEntry> components = new List<ShipFamilyComponentEntry>();

        [Header("Upgrade Tree (auto-generated, editable)")]
        [Tooltip("Chassis variants for this family, ordered by power and annotated with minimum planet level.")]
        public List<ShipFamilyChassisTierEntry> upgradeTree = new List<ShipFamilyChassisTierEntry>();

        [Header("Upgrade cards")]
        [Tooltip("Card pool for this ship family (orbit spins / card shop). When unset or empty, a procedural deck is built at runtime from CardDeckBalance.")]
        public CardDeckDefinition upgradeCardDeck;

        [Header("Team Materials")]
        [Tooltip("Per-team material lists applied to ship component renderers at runtime. Use this instead of per-renderer tinting.")]
        public List<ShipFamilyTeamMaterialSet> teamMaterials = new List<ShipFamilyTeamMaterialSet>();

        [Header("Menu preview generation (editor)")]
        [Tooltip("Clear color when rendering top-down PNGs into MenuPreviews/.")]
        public Color menuPreviewBackgroundColor = new Color(0.06f, 0.09f, 0.14f, 1f);
        [Tooltip("Framing margin around combined renderer bounds (larger = more padding).")]
        [Range(1f, 2.2f)]
        public float menuPreviewBoundsPadding = 1.22f;

        private readonly Dictionary<string, ShipComponentAbilityStats> _lookup =
            new Dictionary<string, ShipComponentAbilityStats>(StringComparer.OrdinalIgnoreCase);

        private bool _lookupBuilt;

        [NonSerialized] private List<CardData> _runtimeProceduralCards;

        private static readonly Regex CloneSuffixRegex = new Regex(@"\s+\(\d+\)$", RegexOptions.Compiled);

        /// <summary>
        /// Normalizes a transform suffix or component id (strips Unity clone suffixes and _Mirrored).
        /// </summary>
        public static string NormalizeComponentId(string rawId)
        {
            if (string.IsNullOrWhiteSpace(rawId))
                return string.Empty;

            string s = rawId.Trim();
            s = CloneSuffixRegex.Replace(s, string.Empty);
            if (s.EndsWith("_Mirrored", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(0, s.Length - "_Mirrored".Length);
            return s.Trim();
        }

        /// <summary>
        /// Cards for this family: <see cref="upgradeCardDeck"/> when assigned, otherwise a one-time procedural list per family asset.
        /// </summary>
        public IReadOnlyList<CardData> GetUpgradeCards()
        {
            if (upgradeCardDeck != null && upgradeCardDeck.cards != null && upgradeCardDeck.cards.Count > 0)
                return upgradeCardDeck.cards;
            if (_runtimeProceduralCards == null)
                _runtimeProceduralCards = CardDeckRuntimeDefaults.CreateProceduralDeck(familyId);
            return _runtimeProceduralCards;
        }

        private void OnValidate()
        {
            EnforceComponentStatCategories();
            InvalidateComponentStatsLookup();
            _runtimeProceduralCards = null;
        }

        /// <summary>Strips each component entry down to stats for its <see cref="ShipFamilyComponentEntry.statCategories"/> only.</summary>
        public void EnforceComponentStatCategories()
        {
            if (components == null)
                return;
            for (int i = 0; i < components.Count; i++)
            {
                var entry = components[i];
                if (entry == null)
                    continue;

                entry.EnsureStatCategories();

                if (!string.IsNullOrWhiteSpace(entry.componentId) &&
                    !HasNonZeroStats(ShipComponentAbilityStats.KeepOnlyAuthoringFields(entry.stats, entry.statCategories, entry.componentId)) &&
                    HasNonZeroStats(entry.stats))
                {
                    entry.statCategories = ShipFamilyComponentPartKey.InferDefaultStatCategories(entry.componentId);
                }

                entry.stats = ShipComponentAbilityStats.KeepOnlyAuthoringFields(entry.stats, entry.statCategories, entry.componentId);
            }
        }

        private static bool HasNonZeroStats(ShipComponentAbilityStats stats)
        {
            return stats.firePower != 0f || stats.firePowerPerLevel != 0f ||
                   stats.bulletSpeed != 0f || stats.bulletSpeedPerLevel != 0f ||
                   stats.fireRate != 0f || stats.fireRatePerLevel != 0f ||
                   stats.rammingPower != 0f || stats.rammingPowerPerLevel != 0f ||
                   stats.healthCap != 0f || stats.healthCapPerLevel != 0f ||
                   stats.healthRegen != 0f || stats.healthRegenPerLevel != 0f ||
                   stats.energyCap != 0f || stats.energyCapPerLevel != 0f ||
                   stats.energyRegen != 0f || stats.energyRegenPerLevel != 0f ||
                   stats.moveSpeed != 0f || stats.moveSpeedPerLevel != 0f ||
                   stats.accelerationCap != 0f || stats.accelerationCapPerLevel != 0f ||
                   stats.turnSpeed != 0f || stats.turnSpeedPerLevel != 0f ||
                   stats.maxGems != 0f || stats.maxGemsPerLevel != 0f ||
                   stats.maxPeople != 0f || stats.maxPeoplePerLevel != 0f;
        }

        /// <summary>
        /// Clears the cached component-id → stats map so the next lookup reads current <see cref="components"/> entries.
        /// Call after edits that might not run <c>OnValidate</c> (e.g. some nested list operations in the inspector).
        /// </summary>
        public void InvalidateComponentStatsLookup()
        {
            _lookupBuilt = false;
        }

        private void EnsureLookup()
        {
            if (_lookupBuilt) return;
            _lookup.Clear();
            if (components != null)
            {
                foreach (var entry in components)
                {
                    if (entry == null) continue;
                    if (string.IsNullOrWhiteSpace(entry.componentId)) continue;
                    string raw = entry.componentId.Trim();
                    _lookup[raw] = entry.stats;
                    string canonical = NormalizeComponentId(raw);
                    if (!string.IsNullOrEmpty(canonical))
                        _lookup[canonical] = entry.stats;
                }
            }

            _lookupBuilt = true;
        }

        /// <summary>
        /// Try to get ability stats for a given component id (e.g. \"Cockpit\", \"Wing1\").
        /// </summary>
        public bool TryGetStatsForComponent(string componentId, out ShipComponentAbilityStats stats)
        {
            EnsureLookup();
            if (string.IsNullOrWhiteSpace(componentId))
            {
                stats = default;
                return false;
            }

            string raw = componentId.Trim();
            if (_lookup.TryGetValue(raw, out stats))
                return true;
            string canonical = NormalizeComponentId(raw);
            return !string.IsNullOrEmpty(canonical) && _lookup.TryGetValue(canonical, out stats);
        }

        /// <summary>
        /// Try to get the full component entry for a given component id (e.g. \"Weapon_1\").
        /// </summary>
        public bool TryGetComponentEntry(string componentId, out ShipFamilyComponentEntry entry)
        {
            entry = null;
            if (components == null || string.IsNullOrWhiteSpace(componentId))
                return false;
            string id = componentId.Trim();
            string canonical = NormalizeComponentId(id);
            for (int i = 0; i < components.Count; i++)
            {
                if (components[i] == null) continue;
                string test = components[i].componentId?.Trim();
                if (string.Equals(test, id, StringComparison.OrdinalIgnoreCase))
                {
                    entry = components[i];
                    return true;
                }
                if (!string.IsNullOrEmpty(canonical) &&
                    string.Equals(NormalizeComponentId(test), canonical, StringComparison.OrdinalIgnoreCase))
                {
                    entry = components[i];
                    return true;
                }
            }
            return false;
        }

        /// <summary>Returns the configured material list for the given team, or null when not configured.</summary>
        public List<Material> GetMaterialsForTeam(TeamManager.Team team)
        {
            if (teamMaterials == null || teamMaterials.Count == 0)
                return null;
            for (int i = 0; i < teamMaterials.Count; i++)
            {
                var set = teamMaterials[i];
                if (set == null || set.materials == null || set.materials.Count == 0)
                    continue;
                if (set.team == team)
                    return set.materials;
            }
            return null;
        }
    }
}

