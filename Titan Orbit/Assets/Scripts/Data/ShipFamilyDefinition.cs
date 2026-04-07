using System;
using System.Collections.Generic;
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
                turnSpeed = a.turnSpeed + b.turnSpeed,
                turnSpeedPerLevel = a.turnSpeedPerLevel + b.turnSpeedPerLevel,
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
            turnSpeed += other.turnSpeed;
            turnSpeedPerLevel += other.turnSpeedPerLevel;
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
                turnSpeed = s.turnSpeed * factor,
                turnSpeedPerLevel = s.turnSpeedPerLevel * factor,
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

        /// <summary>True if the component is a weapon (componentId starts with "Weapon"). Fire power uses average(x,y); fire rate uses 1/z; bullet speed is not scaled by part size.</summary>
        public static bool IsWeaponComponent(string componentId)
        {
            return !string.IsNullOrEmpty(componentId) && componentId.TrimStart().StartsWith("Weapon", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>True if the component is an engine (componentId starts with "Engine"). Max speed uses largest engine only; thrust sums all engines.</summary>
        public static bool IsEngineComponent(string componentId)
        {
            return !string.IsNullOrEmpty(componentId) && componentId.TrimStart().StartsWith("Engine", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>True if the component is a thruster (componentId starts with "Thruster"). Thrust stacks with engines; max speed cap uses best engine first, else best thruster.</summary>
        public static bool IsThrusterComponent(string componentId)
        {
            return !string.IsNullOrEmpty(componentId) && componentId.TrimStart().StartsWith("Thruster", StringComparison.OrdinalIgnoreCase);
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
                    turnSpeed = stats.turnSpeed,
                    turnSpeedPerLevel = stats.turnSpeedPerLevel,
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
            // Do not scale engine/thruster move speed by part volume—designers tune these to match gameplay speeds.
            if (IsEngineComponent(componentId) || IsThrusterComponent(componentId))
            {
                scaled.moveSpeed = stats.moveSpeed;
                scaled.moveSpeedPerLevel = stats.moveSpeedPerLevel;
            }
            return scaled;
        }
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

        [Tooltip("Ability stat modifiers contributed by this component.")]
        public ShipComponentAbilityStats stats;

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

        [Tooltip("All components (cockpit, wings, engines, weapons, etc.) available for this family.")]
        public List<ShipFamilyComponentEntry> components = new List<ShipFamilyComponentEntry>();

        [Header("Upgrade Tree (auto-generated, editable)")]
        [Tooltip("Chassis variants for this family, ordered by power and annotated with minimum planet level.")]
        public List<ShipFamilyChassisTierEntry> upgradeTree = new List<ShipFamilyChassisTierEntry>();

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

        private void OnValidate()
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
                    _lookup[entry.componentId.Trim()] = entry.stats;
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

            return _lookup.TryGetValue(componentId.Trim(), out stats);
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
            for (int i = 0; i < components.Count; i++)
            {
                if (components[i] == null) continue;
                if (string.Equals(components[i].componentId?.Trim(), id, StringComparison.OrdinalIgnoreCase))
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

