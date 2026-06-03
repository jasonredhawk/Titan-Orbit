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
        [Tooltip("Ramming / collision offense: base ramming power (authored small; high mass already amplifies damage).")]
        public float rammingPower;
        [Tooltip("Ramming power gained per ship level (authored small). See ShipComponentRammingSuggestions.")]
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
        [Tooltip("Wing tractor beam reach (m) in normal space. Orbit zones apply a multiplier at runtime.")]
        public float tractorBeamDistance;
        [Tooltip("Tractor reach gained per ship level.")]
        public float tractorBeamDistancePerLevel;
        [Tooltip("Wing tractor beam pull speed (m/s) toward the ship.")]
        public float tractorBeamPower;
        [Tooltip("Tractor pull speed gained per ship level.")]
        public float tractorBeamPowerPerLevel;
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
                tractorBeamDistance = a.tractorBeamDistance + b.tractorBeamDistance,
                tractorBeamDistancePerLevel = a.tractorBeamDistancePerLevel + b.tractorBeamDistancePerLevel,
                tractorBeamPower = a.tractorBeamPower + b.tractorBeamPower,
                tractorBeamPowerPerLevel = a.tractorBeamPowerPerLevel + b.tractorBeamPowerPerLevel,
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
            tractorBeamDistance += other.tractorBeamDistance;
            tractorBeamDistancePerLevel += other.tractorBeamDistancePerLevel;
            tractorBeamPower += other.tractorBeamPower;
            tractorBeamPowerPerLevel += other.tractorBeamPowerPerLevel;
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
                tractorBeamDistance = s.tractorBeamDistance * factor,
                tractorBeamDistancePerLevel = s.tractorBeamDistancePerLevel * factor,
                tractorBeamPower = s.tractorBeamPower * factor,
                tractorBeamPowerPerLevel = s.tractorBeamPowerPerLevel * factor,
                maxPeople = s.maxPeople * factor,
                maxPeoplePerLevel = s.maxPeoplePerLevel * factor
            };
        }

        /// <summary>True when every ability stat field is exactly zero.</summary>
        public bool IsAllZero()
        {
            return firePower == 0f && firePowerPerLevel == 0f &&
                   bulletSpeed == 0f && bulletSpeedPerLevel == 0f &&
                   fireRate == 0f && fireRatePerLevel == 0f &&
                   rammingPower == 0f && rammingPowerPerLevel == 0f &&
                   healthCap == 0f && healthCapPerLevel == 0f &&
                   healthRegen == 0f && healthRegenPerLevel == 0f &&
                   energyCap == 0f && energyCapPerLevel == 0f &&
                   energyRegen == 0f && energyRegenPerLevel == 0f &&
                   moveSpeed == 0f && moveSpeedPerLevel == 0f &&
                   accelerationCap == 0f && accelerationCapPerLevel == 0f &&
                   turnSpeed == 0f && turnSpeedPerLevel == 0f &&
                   maxGems == 0f && maxGemsPerLevel == 0f &&
                   tractorBeamDistance == 0f && tractorBeamDistancePerLevel == 0f &&
                   tractorBeamPower == 0f && tractorBeamPowerPerLevel == 0f &&
                   maxPeople == 0f && maxPeoplePerLevel == 0f;
        }

        /// <summary>
        /// Replaces any exactly-zero field with the corresponding value from <paramref name="defaults"/>.
        /// Used after summing component stats so missing parts do not leave critical stats at zero.
        /// </summary>
        public ShipComponentAbilityStats WithZeroStatFallbacks(ShipComponentAbilityStats defaults)
        {
            var result = this;
            if (result.firePower == 0f) result.firePower = defaults.firePower;
            if (result.firePowerPerLevel == 0f) result.firePowerPerLevel = defaults.firePowerPerLevel;
            if (result.bulletSpeed == 0f) result.bulletSpeed = defaults.bulletSpeed;
            if (result.bulletSpeedPerLevel == 0f) result.bulletSpeedPerLevel = defaults.bulletSpeedPerLevel;
            if (result.fireRate == 0f) result.fireRate = defaults.fireRate;
            if (result.fireRatePerLevel == 0f) result.fireRatePerLevel = defaults.fireRatePerLevel;
            if (result.rammingPower == 0f) result.rammingPower = defaults.rammingPower;
            if (result.rammingPowerPerLevel == 0f) result.rammingPowerPerLevel = defaults.rammingPowerPerLevel;
            if (result.healthCap == 0f) result.healthCap = defaults.healthCap;
            if (result.healthCapPerLevel == 0f) result.healthCapPerLevel = defaults.healthCapPerLevel;
            if (result.healthRegen == 0f) result.healthRegen = defaults.healthRegen;
            if (result.healthRegenPerLevel == 0f) result.healthRegenPerLevel = defaults.healthRegenPerLevel;
            if (result.energyCap == 0f) result.energyCap = defaults.energyCap;
            if (result.energyCapPerLevel == 0f) result.energyCapPerLevel = defaults.energyCapPerLevel;
            if (result.energyRegen == 0f) result.energyRegen = defaults.energyRegen;
            if (result.energyRegenPerLevel == 0f) result.energyRegenPerLevel = defaults.energyRegenPerLevel;
            if (result.moveSpeed == 0f) result.moveSpeed = defaults.moveSpeed;
            if (result.moveSpeedPerLevel == 0f) result.moveSpeedPerLevel = defaults.moveSpeedPerLevel;
            if (result.accelerationCap == 0f) result.accelerationCap = defaults.accelerationCap;
            if (result.accelerationCapPerLevel == 0f) result.accelerationCapPerLevel = defaults.accelerationCapPerLevel;
            if (result.turnSpeed == 0f) result.turnSpeed = defaults.turnSpeed;
            if (result.turnSpeedPerLevel == 0f) result.turnSpeedPerLevel = defaults.turnSpeedPerLevel;
            if (result.maxGems == 0f) result.maxGems = defaults.maxGems;
            if (result.maxGemsPerLevel == 0f) result.maxGemsPerLevel = defaults.maxGemsPerLevel;
            if (result.tractorBeamDistance == 0f) result.tractorBeamDistance = defaults.tractorBeamDistance;
            if (result.tractorBeamDistancePerLevel == 0f) result.tractorBeamDistancePerLevel = defaults.tractorBeamDistancePerLevel;
            if (result.tractorBeamPower == 0f) result.tractorBeamPower = defaults.tractorBeamPower;
            if (result.tractorBeamPowerPerLevel == 0f) result.tractorBeamPowerPerLevel = defaults.tractorBeamPowerPerLevel;
            if (result.maxPeople == 0f) result.maxPeople = defaults.maxPeople;
            if (result.maxPeoplePerLevel == 0f) result.maxPeoplePerLevel = defaults.maxPeoplePerLevel;
            return result;
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
                    tractorBeamDistance = stats.tractorBeamDistance,
                    tractorBeamDistancePerLevel = stats.tractorBeamDistancePerLevel,
                    tractorBeamPower = stats.tractorBeamPower,
                    tractorBeamPowerPerLevel = stats.tractorBeamPowerPerLevel,
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
                    filtered.tractorBeamDistance = stats.tractorBeamDistance;
                    filtered.tractorBeamDistancePerLevel = stats.tractorBeamDistancePerLevel;
                    filtered.tractorBeamPower = stats.tractorBeamPower;
                    filtered.tractorBeamPowerPerLevel = stats.tractorBeamPowerPerLevel;
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
            if (allowedSet.Contains("tractorBeamDistance")) filtered.tractorBeamDistance = stats.tractorBeamDistance;
            if (allowedSet.Contains("tractorBeamDistancePerLevel")) filtered.tractorBeamDistancePerLevel = stats.tractorBeamDistancePerLevel;
            if (allowedSet.Contains("tractorBeamPower")) filtered.tractorBeamPower = stats.tractorBeamPower;
            if (allowedSet.Contains("tractorBeamPowerPerLevel")) filtered.tractorBeamPowerPerLevel = stats.tractorBeamPowerPerLevel;
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
        private static readonly string[] WingCapacityFields =
        {
            "maxGems", "maxGemsPerLevel",
            "tractorBeamDistance", "tractorBeamDistancePerLevel",
            "tractorBeamPower", "tractorBeamPowerPerLevel",
            "maxPeople", "maxPeoplePerLevel"
        };

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
                    if (partType == "Wing" || partType == "Arm")
                        return WingCapacityFields;
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
        /// <summary>Health cap at version 1 (+20% vs prior 5.25).</summary>
        public const float HealthCapV1 = 6.3f;

        /// <summary>Health cap added per version tier (v2 = 8.1, v3 = 9.9, v4 = 11.7, … — same curve for every health part).</summary>
        public const float HealthCapPerVersion = 1.8f;

        /// <summary>Health regen as a fraction of cap (legacy ratio 0.75 / 21).</summary>
        public const float HealthRegenFractionOfCap = 0.75f / 21f;

        /// <summary>Health cap from version: v1=6.3, v2=8.1, v3=9.9, v4=11.7, …</summary>
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

    /// <summary>Scan/auto-populate cockpit ramming offense (kept low — damage also scales sublinearly with ship mass).</summary>
    public static class ShipComponentRammingSuggestions
    {
        /// <summary>Global tuning on effective ram damage (impact, grind, HUD rating). Does not affect bounce/restitution.</summary>
        public const float GlobalDamageMultiplier = 3f;

        /// <summary>Ramming power at version 1 (cockpit). Hull mass amplifies via sublinear massFactor.</summary>
        public const float RammingPowerV1 = 1f;

        /// <summary>Ramming power added per version tier (v2, v3, …).</summary>
        public const float RammingPowerPerVersion = 0.12f;

        /// <summary>Per-level ramming power as a fraction of base when scanning family assets.</summary>
        public const float RammingPerLevelFractionOfBase = 0.25f;

        /// <summary>Reference mass when no per-ship baseline is supplied (legacy fallback only).</summary>
        public const float ReferenceRamMass = 5f;

        /// <summary>Mass exponent for ram damage (&lt; 1 = gems/cargo add less than linear weight).</summary>
        public const float MassDamageExponent = 0.45f;

        /// <summary>Softer mass curve for self chip damage (heavy ships should not one-shot themselves).</summary>
        public const float SelfMassDamageExponent = 0.28f;

        /// <summary>Self hull chip vs asteroid on the same hit (was ~1.67 from legacy force scales).</summary>
        public const float SelfToAsteroidDamageRatio = 1.35f;

        /// <summary>Max self damage from a single ram impact as a fraction of <see cref="Entities.Starship.MaxHealth"/>.</summary>
        public const float MaxSelfImpactDamageFractionOfMaxHealth = 0.22f;

        /// <summary>Damage multiplier added per summed base rammingPower point (cockpit + parts at level 1).</summary>
        public const float OffenseMultiplierPerBasePower = 0.14f;

        /// <summary>
        /// Extra damage multiplier per (summed rammingPowerPerLevel × levels after 1). Primary driver for level-up ramming feel.
        /// </summary>
        public const float OffenseMultiplierPerLevelPower = 0.52f;

        public static float GetSuggestedRammingPower(int version)
        {
            int v = Mathf.Max(1, version);
            return RammingPowerV1 + (v - 1) * RammingPowerPerVersion;
        }

        public static float GetSuggestedRammingPowerPerLevel(int version) =>
            GetSuggestedRammingPower(version) * RammingPerLevelFractionOfBase;

        /// <summary>Summed family ramming stat = bullet-comparable rating before massFactor (excludes ship prefab baseRammingPower).</summary>
        public static float ComputeDamageRatingFromFamilyPower(float summedFamilyRammingPower)
        {
            return Mathf.Max(0.05f, summedFamilyRammingPower) * GlobalDamageMultiplier;
        }

        /// <summary>Sublinear mass vs this ship's own hull baseline (≈1 with no gem cargo, any level).</summary>
        public static float ComputeMassDamageFactor(float mass, float hullBaselineMass, float exponent = MassDamageExponent)
        {
            float ratio = Mathf.Max(0.1f, mass / Mathf.Max(0.5f, hullBaselineMass));
            return Mathf.Pow(ratio, Mathf.Max(0.05f, exponent));
        }

        /// <summary>Legacy: fixed reference mass (prefer per-ship baseline overload).</summary>
        public static float ComputeMassDamageFactor(float mass) =>
            ComputeMassDamageFactor(mass, ReferenceRamMass);

        /// <summary>Maps summed family ramming stats + ship level to asteroid/grind offense multiplier.</summary>
        public static float ComputeOffenseMultiplier(float summedBasePower, float summedPowerPerLevel, int shipLevel)
        {
            float perLvl = Mathf.Max(0, shipLevel - 1);
            float basePart = 1f + Mathf.Max(0f, summedBasePower) * OffenseMultiplierPerBasePower;
            float levelPart = 1f + Mathf.Max(0f, summedPowerPerLevel) * perLvl * OffenseMultiplierPerLevelPower;
            return basePart * levelPart;
        }

        /// <summary>Head-on speed (m/s) where impact damage equals rating × massFactor (massFactor = 1 at reference mass).</summary>
        public const float ReferenceImpactSpeed = 10f;

        /// <summary>Engine push (N) into the rock where one grind pulse deals full rating × massFactor × interval damage.</summary>
        public const float ReferenceGrindPushNewtons = 80f;

        /// <summary>
        /// Impact: rating × massFactor × speed factor. massFactor ≈ 1 at hull baseline (level-invariant).
        /// Pass <paramref name="maxRestitutionForDamage"/> (not bounce restitution) so suppressed bounce energy becomes damage.
        /// </summary>
        public static float ComputeImpactDamage(
            float ramDamageRating,
            float mass,
            float hullBaselineMass,
            float inboundNormalSpeed,
            float maxRestitutionForDamage)
        {
            float deltaNormalSpeed = (1f + Mathf.Clamp01(maxRestitutionForDamage)) * Mathf.Max(0f, inboundNormalSpeed);
            float speedFactor = deltaNormalSpeed / Mathf.Max(0.1f, ReferenceImpactSpeed);
            float massFactor = ComputeMassDamageFactor(mass, hullBaselineMass);
            return Mathf.Max(0f, ramDamageRating * massFactor * speedFactor);
        }

        /// <summary>Grind pulse: rating × massFactor × push factor × interval.</summary>
        public static float ComputeGrindDamagePerPulse(
            float ramDamageRating,
            float mass,
            float hullBaselineMass,
            float pushNewtons,
            float pulseInterval)
        {
            float pushFactor = pushNewtons / Mathf.Max(1f, ReferenceGrindPushNewtons);
            float massFactor = ComputeMassDamageFactor(mass, hullBaselineMass);
            return Mathf.Max(0f, ramDamageRating * massFactor * pushFactor * pulseInterval);
        }

        public static float ComputeSelfMassDamageFactor(float mass, float hullBaselineMass) =>
            ComputeMassDamageFactor(mass, hullBaselineMass, SelfMassDamageExponent);
    }

    /// <summary>Scan/auto-populate weapon offense and energy (burst ~2s at full fire rate, regen below sustained drain).</summary>
    public static class ShipComponentWeaponSuggestions
    {
        public const float FirePowerV1 = 3f;
        public const float FireRate = 3f;
        public const float FireRatePerLevel = 0f;
        public const float BulletSpeedV1 = 12f;

        /// <summary>Seconds of continuous fire at authored fire rate before the pool is empty (no regen).</summary>
        public const float BurstSecondsAtFullDrain = 2f;

        /// <summary>
        /// Shots/sec when energy-limited (energyRegen / firePower at runtime). Independent of max fire rate.
        /// </summary>
        public const float SustainedFireRateShotsPerSecond = 1f;

        public static float GetSuggestedFirePower(int version)
        {
            int v = Mathf.Max(1, version);
            return FirePowerV1 * v;
        }

        public static float GetSuggestedBulletSpeed(int version)
        {
            int v = Mathf.Max(1, version);
            return BulletSpeedV1 * v;
        }

        public static float GetSuggestedFirePowerPerLevel(int version) =>
            GetSuggestedFirePower(version) * ShipPropulsionAggregation.PerLevelFractionOfBase;

        public static float GetSuggestedBulletSpeedPerLevel(int version) =>
            GetSuggestedBulletSpeed(version) * ShipPropulsionAggregation.PerLevelFractionOfBase;

        public static float ComputeSustainedEnergyDrain(float firePower, float fireRate) =>
            Mathf.Max(0f, firePower) * Mathf.Max(0.01f, fireRate);

        public static float ComputeSustainedEnergyDrain(ShipComponentAbilityStats weaponStats, int levelAfterFirst = 0)
        {
            float firePower = weaponStats.firePower + weaponStats.firePowerPerLevel * Mathf.Max(0, levelAfterFirst);
            float fireRate = weaponStats.fireRate + weaponStats.fireRatePerLevel * Mathf.Max(0, levelAfterFirst);
            return ComputeSustainedEnergyDrain(firePower, fireRate);
        }

        public static void ApplyBalancedEnergy(
            ref ShipComponentAbilityStats stats,
            float sustainedFireRateShotsPerSecond = SustainedFireRateShotsPerSecond,
            float capacitySecondsAtFullDrain = BurstSecondsAtFullDrain,
            int levelAfterFirst = 0)
        {
            float firePower = stats.firePower + stats.firePowerPerLevel * Mathf.Max(0, levelAfterFirst);
            float drain = ComputeSustainedEnergyDrain(stats, levelAfterFirst);
            if (firePower <= 0f || drain <= 0f)
                return;

            stats.energyRegen = firePower * Mathf.Max(0f, sustainedFireRateShotsPerSecond);
            stats.energyRegenPerLevel = stats.energyRegen * ShipPropulsionAggregation.PerLevelFractionOfBase;
            stats.energyCap = drain * capacitySecondsAtFullDrain;
            stats.energyCapPerLevel = stats.energyCap * ShipPropulsionAggregation.PerLevelFractionOfBase;
        }
    }

    /// <summary>Scan/auto-populate wing tractor beam reach and pull speed (Capacity category).</summary>
    public static class ShipComponentTractorBeamSuggestions
    {
        /// <summary>Tractor reach (m) at wing version 1 in normal space.</summary>
        public const float TractorDistanceV1 = 3f;

        /// <summary>Tractor reach added per wing version tier (v2 = 6m, v3 = 9m, …).</summary>
        public const float TractorDistancePerVersion = 3f;

        /// <summary>Authored tractor pull speed (m/s) at wing version 1; scaled down at runtime.</summary>
        public const float TractorPowerV1 = 4f;

        /// <summary>Authored tractor pull speed added per wing version tier.</summary>
        public const float TractorPowerPerVersion = 4f;

        public static float GetSuggestedTractorDistance(int version)
        {
            int v = Mathf.Max(1, version);
            return TractorDistanceV1 + (v - 1) * TractorDistancePerVersion;
        }

        public static float GetSuggestedTractorPower(int version)
        {
            int v = Mathf.Max(1, version);
            return TractorPowerV1 + (v - 1) * TractorPowerPerVersion;
        }

        public static float GetSuggestedTractorDistancePerLevel(int version) =>
            GetSuggestedTractorDistance(version) * ShipPropulsionAggregation.PerLevelFractionOfBase;

        public static float GetSuggestedTractorPowerPerLevel(int version) =>
            GetSuggestedTractorPower(version) * ShipPropulsionAggregation.PerLevelFractionOfBase;
    }

    /// <summary>Scan/auto-populate people capacity for cockpits, wings, and other Capacity category parts.</summary>
    public static class ShipComponentPeopleCapacitySuggestions
    {
        /// <summary>People capacity at version 1 per Capacity-category component (halved from prior 4).</summary>
        public const float PeopleCapacityV1 = 2f;

        public static float GetSuggestedPeopleCapacity(int version)
        {
            int v = Mathf.Max(1, version);
            return PeopleCapacityV1 * v;
        }

        public static float GetSuggestedPeopleCapacityPerLevel(int version) =>
            Mathf.Max(0f, Mathf.RoundToInt(GetSuggestedPeopleCapacity(version) * ShipPropulsionAggregation.PerLevelFractionOfBase));
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
        /// <summary>Per-version turn speed for thrusters (~90% of fin; below fin and tail at the same version).</summary>
        public const float ThrusterTurnSpeedV1 = 6.3f;
        public const float ThrusterTurnSpeedPerVersion = 6.3f;

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
    /// Breakdown of <see cref="ShipFamilyChassisTierEntry.powerScore"/> (offense + defense + energy + mobility + capacity),
    /// plus the ten ship-upgrade-menu stats shown in the orbit ship tree power bar.
    /// Populated when building the upgrade tree from folder in the editor.
    /// </summary>
    [Serializable]
    public struct ShipFamilyPowerScoreBreakdown
    {
        public const int DisplayStatCount = 10;

        [Tooltip("Offense category total (fire power, bullet speed, fire rate, ramming).")]
        public float offense;
        [Tooltip("Defense category total (health cap, health regen).")]
        public float defense;
        [Tooltip("Energy category total (energy cap, energy regen).")]
        public float energy;
        [Tooltip("Mobility category total (move speed, turn speed, acceleration).")]
        public float mobility;
        [Tooltip("Capacity category total (gems, people).")]
        public float capacity;

        [Tooltip("Fire Power (upgrade menu stat, level-1 effective value).")]
        public float firePower;
        [Tooltip("Bullet Speed (upgrade menu stat, level-1 effective value).")]
        public float bulletSpeed;
        [Tooltip("Health Cap (upgrade menu stat, level-1 effective value).")]
        public float healthCap;
        [Tooltip("Health Regen (upgrade menu stat, level-1 effective value).")]
        public float healthRegen;
        [Tooltip("Energy Cap (upgrade menu stat, level-1 effective value).")]
        public float energyCap;
        [Tooltip("Energy Regen (upgrade menu stat, level-1 effective value).")]
        public float energyRegen;
        [Tooltip("Move Speed (upgrade menu stat, level-1 effective value).")]
        public float moveSpeed;
        [Tooltip("Turn Speed (upgrade menu stat, level-1 effective value).")]
        public float turnSpeed;
        [Tooltip("Gem Cap (upgrade menu stat, level-1 effective value).")]
        public float gemCap;
        [Tooltip("People Cap (upgrade menu stat, level-1 effective value).")]
        public float peopleCap;

        public float Total => offense + defense + energy + mobility + capacity;

        /// <summary>Sum of the ten upgrade-menu stat contributions used by orbit ship-tree power bars.</summary>
        public float DisplayTotal =>
            firePower + bulletSpeed + healthCap + healthRegen + energyCap + energyRegen +
            moveSpeed + turnSpeed + gemCap + peopleCap;

        public bool HasDisplayStats => DisplayTotal > 0.01f;

        /// <summary>Upgrade-menu stat power values for UI bars (falls back to splitting legacy O/D/E/M/C pairs).</summary>
        public float GetDisplayStatValue(int statIndex)
        {
            if (HasDisplayStats)
            {
                switch (statIndex)
                {
                    case 0: return firePower;
                    case 1: return bulletSpeed;
                    case 2: return healthCap;
                    case 3: return healthRegen;
                    case 4: return energyCap;
                    case 5: return energyRegen;
                    case 6: return moveSpeed;
                    case 7: return turnSpeed;
                    case 8: return gemCap;
                    case 9: return peopleCap;
                }

                return 0f;
            }

            float halfCategory = 0.5f;
            switch (statIndex)
            {
                case 0:
                case 1: return offense * halfCategory;
                case 2:
                case 3: return defense * halfCategory;
                case 4:
                case 5: return energy * halfCategory;
                case 6:
                case 7: return mobility * halfCategory;
                case 8:
                case 9: return capacity * halfCategory;
                default: return 0f;
            }
        }

        /// <summary>Total power represented by <see cref="GetDisplayStatValue"/> (handles legacy breakdown data).</summary>
        public float GetDisplayTotalForUi()
        {
            float total = 0f;
            for (int i = 0; i < DisplayStatCount; i++)
                total += GetDisplayStatValue(i);
            return total;
        }

        /// <summary>
        /// Category and per-stat breakdown from summed ship stats (level-1 effective values, no heuristic scaling).
        /// Input stats must already include per-component localScale (see ShipFamilyUpgradeTreeStatScanner in the Editor assembly).
        /// </summary>
        public static ShipFamilyPowerScoreBreakdown FromSummedShipStats(ShipComponentAbilityStats s)
        {
            return new ShipFamilyPowerScoreBreakdown
            {
                firePower = s.firePower,
                bulletSpeed = s.bulletSpeed,
                healthCap = s.healthCap,
                healthRegen = s.healthRegen,
                energyCap = s.energyCap,
                energyRegen = s.energyRegen,
                moveSpeed = s.moveSpeed,
                turnSpeed = s.turnSpeed,
                gemCap = s.maxGems,
                peopleCap = s.maxPeople,
                offense = s.firePower + s.bulletSpeed + s.fireRate + s.rammingPower,
                defense = s.healthCap + s.healthRegen,
                energy = s.energyCap + s.energyRegen,
                mobility = s.moveSpeed + s.turnSpeed + s.accelerationCap,
                capacity = s.maxGems + s.maxPeople
            };
        }

        /// <summary>
        /// Branch slot order for one upgrade-tree level row (left → right).
        /// Anchors highest fire power on the left and highest gem cap on the right; remaining ships are
        /// placed on a fire→gems skew axis so the row visibly trends fire-heavy left and gem-heavy right.
        /// </summary>
        public static int[] ComputeBranchLayoutOrder(IReadOnlyList<ShipFamilyPowerScoreBreakdown> breakdowns)
        {
            int n = breakdowns?.Count ?? 0;
            if (n <= 1)
                return n == 1 ? new[] { 0 } : Array.Empty<int>();

            var slotTargets = new float[n];
            for (int i = 0; i < n; i++)
                slotTargets[i] = ComputeFireGemsSlotTarget(breakdowns, i);

            int leftAnchor = FindBestAnchorIndex(
                breakdowns,
                b => b.firePower,
                preferLowerGemsOnTie: true,
                preferLowerFireOnTie: false);

            int rightAnchor = FindBestAnchorIndex(
                breakdowns,
                b => b.gemCap,
                preferLowerGemsOnTie: false,
                preferLowerFireOnTie: true,
                excludedIndex: leftAnchor);

            var interior = new List<int>(n);
            for (int i = 0; i < n; i++)
            {
                if (i == leftAnchor || i == rightAnchor)
                    continue;
                interior.Add(i);
            }

            interior.Sort((a, b) => CompareFireGemsBranchOrder(breakdowns, slotTargets, a, b));

            var order = new int[n];
            order[0] = leftAnchor;
            if (n >= 2)
                order[n - 1] = rightAnchor;

            int interiorSlot = 0;
            for (int slot = 1; slot < n - 1; slot++)
                order[slot] = interior[interiorSlot++];

            return order;
        }

        /// <summary>0 = fire branch (left), 1 = gem branch (right).</summary>
        private static float ComputeFireGemsSlotTarget(IReadOnlyList<ShipFamilyPowerScoreBreakdown> breakdowns, int index)
        {
            int n = breakdowns.Count;
            float fireNorm = NormalizeBranchStat(breakdowns, index, b => b.firePower);
            float gemNorm = NormalizeBranchStat(breakdowns, index, b => b.gemCap);
            return (1f - fireNorm + gemNorm) * 0.5f;
        }

        private static float NormalizeBranchStat(
            IReadOnlyList<ShipFamilyPowerScoreBreakdown> breakdowns,
            int index,
            Func<ShipFamilyPowerScoreBreakdown, float> metric)
        {
            int n = breakdowns.Count;
            if (n <= 1)
                return 0.5f;

            float min = float.MaxValue;
            float max = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                float v = metric(breakdowns[i]);
                min = Mathf.Min(min, v);
                max = Mathf.Max(max, v);
            }

            float value = metric(breakdowns[index]);
            if (max - min <= 0.0001f)
                return ComputeBranchRankNorm(breakdowns, index, metric);

            return (value - min) / (max - min);
        }

        private static float ComputeBranchRankNorm(
            IReadOnlyList<ShipFamilyPowerScoreBreakdown> breakdowns,
            int index,
            Func<ShipFamilyPowerScoreBreakdown, float> metric)
        {
            int n = breakdowns.Count;
            if (n <= 1)
                return 0.5f;

            int rank = 0;
            float value = metric(breakdowns[index]);
            for (int i = 0; i < n; i++)
            {
                if (metric(breakdowns[i]) < value - 0.0001f)
                    rank++;
            }

            return rank / (float)(n - 1);
        }

        private static int CompareFireGemsBranchOrder(
            IReadOnlyList<ShipFamilyPowerScoreBreakdown> breakdowns,
            float[] slotTargets,
            int a,
            int b)
        {
            int cmp = slotTargets[a].CompareTo(slotTargets[b]);
            if (cmp != 0)
                return cmp;

            cmp = breakdowns[b].firePower.CompareTo(breakdowns[a].firePower);
            if (cmp != 0)
                return cmp;

            cmp = breakdowns[a].gemCap.CompareTo(breakdowns[b].gemCap);
            if (cmp != 0)
                return cmp;

            return breakdowns[b].GetDisplayTotalForUi().CompareTo(breakdowns[a].GetDisplayTotalForUi());
        }

        private static int FindBestAnchorIndex(
            IReadOnlyList<ShipFamilyPowerScoreBreakdown> breakdowns,
            Func<ShipFamilyPowerScoreBreakdown, float> metric,
            bool preferLowerGemsOnTie,
            bool preferLowerFireOnTie,
            int excludedIndex = -1)
        {
            int n = breakdowns.Count;
            int best = -1;
            float bestVal = float.NegativeInfinity;
            float bestGems = float.PositiveInfinity;
            float bestFire = float.PositiveInfinity;
            float bestTotal = float.NegativeInfinity;

            for (int i = 0; i < n; i++)
            {
                if (i == excludedIndex)
                    continue;

                float v = metric(breakdowns[i]);
                float gems = breakdowns[i].gemCap;
                float fire = breakdowns[i].firePower;
                float total = breakdowns[i].GetDisplayTotalForUi();
                if (v > bestVal + 0.0001f)
                {
                    bestVal = v;
                    bestGems = gems;
                    bestFire = fire;
                    bestTotal = total;
                    best = i;
                    continue;
                }

                if (!Mathf.Approximately(v, bestVal))
                    continue;

                if (preferLowerGemsOnTie && gems < bestGems - 0.0001f)
                {
                    bestGems = gems;
                    bestFire = fire;
                    bestTotal = total;
                    best = i;
                    continue;
                }

                if (preferLowerFireOnTie && fire < bestFire - 0.0001f)
                {
                    bestFire = fire;
                    bestGems = gems;
                    bestTotal = total;
                    best = i;
                    continue;
                }

                if (Mathf.Approximately(gems, bestGems) && Mathf.Approximately(fire, bestFire) && total > bestTotal + 0.0001f)
                {
                    bestTotal = total;
                    best = i;
                }
            }

            return best;
        }

        /// <summary>Reorders <paramref name="items"/> left→right for one planet tier row using <see cref="ComputeBranchLayoutOrder"/>.</summary>
        public static void ReorderListByBranchLayout<T>(List<T> items, Func<T, ShipFamilyPowerScoreBreakdown> selector)
        {
            if (items == null || items.Count <= 1 || selector == null)
                return;

            var breakdowns = new ShipFamilyPowerScoreBreakdown[items.Count];
            for (int i = 0; i < items.Count; i++)
                breakdowns[i] = selector(items[i]);

            int[] order = ComputeBranchLayoutOrder(breakdowns);
            if (order.Length != items.Count)
                return;

            var reordered = new List<T>(items.Count);
            for (int slot = 0; slot < order.Length; slot++)
                reordered.Add(items[order[slot]]);

            items.Clear();
            items.AddRange(reordered);
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

        [Tooltip("Chassis component mass from this tier's prefab (sum of part scale factors). Matches Starship componentMass / speedometer MASS at level 1 with empty cargo.")]
        public float componentMass;

        [Tooltip("Editor: category totals and per-stat values for powerScore (offense + defense + energy + mobility + capacity).")]
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
    /// Baseline ship-level stat totals used when a family has no authored default fallbacks.
    /// Values match v1 component scan suggestions for a minimal functional ship (one weapon, cockpit, thruster, fin, wing).
    /// </summary>
    public static class ShipFamilyDefaultFallbackStats
    {
        public static ShipComponentAbilityStats CreateBaseline()
        {
            const int v = 1;
            float weaponFirePower = ShipComponentWeaponSuggestions.GetSuggestedFirePower(v);
            float weaponBulletSpeed = ShipComponentWeaponSuggestions.GetSuggestedBulletSpeed(v);
            float weaponFireRate = ShipComponentWeaponSuggestions.FireRate;
            var weaponEnergy = new ShipComponentAbilityStats
            {
                firePower = weaponFirePower,
                fireRate = weaponFireRate,
                fireRatePerLevel = ShipComponentWeaponSuggestions.FireRatePerLevel
            };
            ShipComponentWeaponSuggestions.ApplyBalancedEnergy(ref weaponEnergy);
            float energyCap = weaponEnergy.energyCap;
            float energyRegen = weaponEnergy.energyRegen;
            float maxGems = 8f * v;
            float maxPeople = ShipComponentPeopleCapacitySuggestions.GetSuggestedPeopleCapacity(v);
            float perLevel = ShipPropulsionAggregation.PerLevelFractionOfBase;
            const float baselineMoveSpeed = 9f;
            const float baselineTurnSpeed = 14f;
            float moveSpeed = baselineMoveSpeed;
            float accelerationCap = ShipPropulsionAggregation.ApplyOverallPropulsionSpeedScale(
                ShipPropulsionAggregation.GetSuggestedPropulsionAccelerationCap(v));
            float turnSpeed = baselineTurnSpeed;

            return new ShipComponentAbilityStats
            {
                firePower = weaponFirePower,
                firePowerPerLevel = weaponFirePower * perLevel,
                bulletSpeed = weaponBulletSpeed,
                bulletSpeedPerLevel = weaponBulletSpeed * perLevel,
                fireRate = weaponFireRate,
                fireRatePerLevel = ShipComponentWeaponSuggestions.FireRatePerLevel,
                rammingPower = ShipComponentRammingSuggestions.GetSuggestedRammingPower(v),
                rammingPowerPerLevel = ShipComponentRammingSuggestions.GetSuggestedRammingPowerPerLevel(v),
                healthCap = ShipComponentHealthSuggestions.GetSuggestedHealthCap(v),
                healthCapPerLevel = ShipComponentHealthSuggestions.GetSuggestedHealthCapPerLevel(v),
                healthRegen = ShipComponentHealthSuggestions.GetSuggestedHealthRegen(v),
                healthRegenPerLevel = ShipComponentHealthSuggestions.GetSuggestedHealthRegenPerLevel(v),
                energyCap = energyCap,
                energyCapPerLevel = energyCap * perLevel,
                energyRegen = energyRegen,
                energyRegenPerLevel = energyRegen * perLevel,
                moveSpeed = moveSpeed,
                moveSpeedPerLevel = moveSpeed * ShipPropulsionAggregation.PropulsionPerLevelFractionOfBase,
                accelerationCap = accelerationCap,
                accelerationCapPerLevel = ShipPropulsionAggregation.GetSuggestedPropulsionAccelerationCapPerLevel(v),
                turnSpeed = turnSpeed,
                turnSpeedPerLevel = turnSpeed * perLevel,
                maxGems = maxGems,
                maxGemsPerLevel = maxGems * perLevel,
                tractorBeamDistance = ShipComponentTractorBeamSuggestions.GetSuggestedTractorDistance(v),
                tractorBeamDistancePerLevel = ShipComponentTractorBeamSuggestions.GetSuggestedTractorDistancePerLevel(v),
                tractorBeamPower = ShipComponentTractorBeamSuggestions.GetSuggestedTractorPower(v),
                tractorBeamPowerPerLevel = ShipComponentTractorBeamSuggestions.GetSuggestedTractorPowerPerLevel(v),
                maxPeople = maxPeople,
                maxPeoplePerLevel = ShipComponentPeopleCapacitySuggestions.GetSuggestedPeopleCapacityPerLevel(v)
            };
        }
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

        [Header("Default Stat Fallbacks")]
        [Tooltip("Ship-level totals used when summed component stats for a stat are zero (e.g. missing weapon → fire power fallback). Leave all zero to use the global baseline.")]
        public ShipComponentAbilityStats defaultFallbackStats;

        [Header("Bullets")]
        [Tooltip("Index into CombatSystem's Bullet Prefab Bank (CombatSystem.bulletPrefabBank). 0 = first prefab. Weapon components can override per-cannon via ShipFamilyComponentEntry.bulletPrefabIndex. Same list/order on all builds for networking.")]
        public int bulletPrefabIndex = 0;

        [Header("Components")]
        [Tooltip("All components (cockpit, wings, engines, weapons, etc.) and their ability stat modifiers for this family.")]
        public List<ShipFamilyComponentEntry> components = new List<ShipFamilyComponentEntry>();

        [Header("Mass")]
        [Tooltip("Sum of chassis part scale factors on Mass Reference Prefab (or first upgrade-tree prefab). Matches Starship componentMass before hullMassScale (~0.7 on ship prefab) and level HP bulk.")]
        [SerializeField] private float totalComponentMass;
        [Tooltip("Prefab used to compute Total Component Mass. When unset, Recalculate uses the first upgrade-tree entry with a prefab.")]
        [SerializeField] private GameObject massReferencePrefab;
        [Tooltip("Typical Starship hullMassScale — HUD movement mass ≈ totalComponentMass × this at level 1 with empty cargo.")]
        public const float DefaultHullMassScale = 0.7f;

        /// <summary>Chassis mass from reference prefab part scales (see <see cref="ChassisComponentStats.ComputeComponentMass"/>).</summary>
        public float TotalComponentMass => totalComponentMass;

        /// <summary>Reference prefab for <see cref="totalComponentMass"/>; optional.</summary>
        public GameObject MassReferencePrefab => massReferencePrefab;

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
        private static readonly Regex PropulsionIdUnderscoreFormRegex =
            new Regex(@"^(Engine|Thruster|Wing)_(\d+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex PropulsionIdCompactFormRegex =
            new Regex(@"^(Engine|Thruster|Wing)(\d+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

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

        /// <summary>Maps Engine1 ↔ Engine_1 (and thruster/wing variants) so prefab suffixes match family entries.</summary>
        internal static string GetAlternateComponentIdForm(string componentId)
        {
            if (string.IsNullOrWhiteSpace(componentId))
                return string.Empty;

            string s = NormalizeComponentId(componentId);
            Match underscored = PropulsionIdUnderscoreFormRegex.Match(s);
            if (underscored.Success)
                return underscored.Groups[1].Value + underscored.Groups[2].Value;

            Match compact = PropulsionIdCompactFormRegex.Match(s);
            if (compact.Success)
                return compact.Groups[1].Value + "_" + compact.Groups[2].Value;

            return string.Empty;
        }

        private static void RegisterComponentStatsLookupKey(
            Dictionary<string, ShipComponentAbilityStats> lookup,
            string key,
            ShipComponentAbilityStats stats)
        {
            if (string.IsNullOrWhiteSpace(key))
                return;
            lookup[key.Trim()] = stats;
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
            EnsureDefaultFallbackStats();
            InvalidateComponentStatsLookup();
            _runtimeProceduralCards = null;
        }

        /// <summary>
        /// Effective default fallbacks: authored <see cref="defaultFallbackStats"/> when any field is set, otherwise the global baseline.
        /// </summary>
        public ShipComponentAbilityStats GetEffectiveDefaultFallbackStats()
        {
            return defaultFallbackStats.IsAllZero()
                ? ShipFamilyDefaultFallbackStats.CreateBaseline()
                : defaultFallbackStats;
        }

        /// <summary>
        /// Applies <see cref="GetEffectiveDefaultFallbackStats"/> to any zero fields in summed ship stats.
        /// </summary>
        public ShipComponentAbilityStats ApplyStatFallbacks(ShipComponentAbilityStats summedStats)
        {
            return summedStats.WithZeroStatFallbacks(GetEffectiveDefaultFallbackStats());
        }

        /// <summary>Populates <see cref="defaultFallbackStats"/> from the global baseline when unset.</summary>
        public void EnsureDefaultFallbackStats()
        {
            if (!defaultFallbackStats.IsAllZero())
                return;
            defaultFallbackStats = ShipFamilyDefaultFallbackStats.CreateBaseline();
        }

        /// <summary>Resets <see cref="defaultFallbackStats"/> to the global baseline values.</summary>
        public void ResetDefaultFallbackStatsToBaseline()
        {
            defaultFallbackStats = ShipFamilyDefaultFallbackStats.CreateBaseline();
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
                   stats.tractorBeamDistance != 0f || stats.tractorBeamDistancePerLevel != 0f ||
                   stats.tractorBeamPower != 0f || stats.tractorBeamPowerPerLevel != 0f ||
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
                    RegisterComponentStatsLookupKey(_lookup, raw, entry.stats);
                    string canonical = NormalizeComponentId(raw);
                    RegisterComponentStatsLookupKey(_lookup, canonical, entry.stats);
                    string alternate = GetAlternateComponentIdForm(raw);
                    RegisterComponentStatsLookupKey(_lookup, alternate, entry.stats);
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
            if (!string.IsNullOrEmpty(canonical) && _lookup.TryGetValue(canonical, out stats))
                return true;
            string alternate = GetAlternateComponentIdForm(raw);
            return !string.IsNullOrEmpty(alternate) && _lookup.TryGetValue(alternate, out stats);
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

        /// <summary>
        /// Component mass from a ship prefab hierarchy (direct-child part scales + weapons).
        /// Same formula as <see cref="Entities.Starship"/> and the speedometer MASS line at level 1 with no gems.
        /// </summary>
        public float ComputeComponentMassFromPrefab(GameObject prefab)
        {
            if (prefab == null)
                return 0f;

            string prefix = !string.IsNullOrWhiteSpace(familyId)
                ? familyId.Trim()
                : DeriveFamilyPrefixFromPrefabName(prefab.name);
            return ChassisComponentStats.ComputeComponentMassFromTransform(prefab.transform, prefix);
        }

        /// <summary>HUD-style hull mass at level 1 with empty cargo: component mass × <see cref="DefaultHullMassScale"/>.</summary>
        public float ComputeHudHullMassFromPrefab(GameObject prefab) =>
            Mathf.Max(0.5f, ComputeComponentMassFromPrefab(prefab) * DefaultHullMassScale);

        /// <summary>HUD-style hull mass from <see cref="totalComponentMass"/>.</summary>
        public float ComputeHudHullMassFromTotal() =>
            Mathf.Max(0.5f, totalComponentMass * DefaultHullMassScale);

        /// <summary>
        /// Refreshes <see cref="totalComponentMass"/> from <see cref="massReferencePrefab"/> or the first upgrade-tree prefab.
        /// Also updates <see cref="ShipFamilyChassisTierEntry.componentMass"/> on tiers that have prefabs.
        /// </summary>
        public void RecalculateTotalComponentMass()
        {
            GameObject reference = massReferencePrefab;
            if (reference == null && upgradeTree != null)
            {
                for (int i = 0; i < upgradeTree.Count; i++)
                {
                    if (upgradeTree[i]?.prefab != null)
                    {
                        reference = upgradeTree[i].prefab;
                        break;
                    }
                }
            }

            totalComponentMass = reference != null ? ComputeComponentMassFromPrefab(reference) : 0f;

            if (upgradeTree == null)
                return;

            string prefix = !string.IsNullOrWhiteSpace(familyId) ? familyId.Trim() : string.Empty;
            for (int i = 0; i < upgradeTree.Count; i++)
            {
                ShipFamilyChassisTierEntry tier = upgradeTree[i];
                if (tier == null || tier.prefab == null)
                    continue;

                string tierPrefix = !string.IsNullOrEmpty(prefix)
                    ? prefix
                    : DeriveFamilyPrefixFromPrefabName(tier.prefab.name);
                tier.componentMass = ChassisComponentStats.ComputeComponentMassFromTransform(tier.prefab.transform, tierPrefix);
            }
        }

        private static string DeriveFamilyPrefixFromPrefabName(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName))
                return "AstroEagle";

            string name = prefabName;
            int cloneIdx = name.IndexOf("(Clone)", System.StringComparison.Ordinal);
            if (cloneIdx > 0)
                name = name.Substring(0, cloneIdx).TrimEnd();

            int i = name.Length - 1;
            while (i >= 0 && char.IsDigit(name[i]))
                i--;

            if (i < name.Length - 1)
                name = name.Substring(0, i + 1);

            return string.IsNullOrEmpty(name) ? "AstroEagle" : name;
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

