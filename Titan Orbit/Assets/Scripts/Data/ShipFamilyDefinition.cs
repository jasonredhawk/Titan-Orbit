using System;
using System.Collections.Generic;
using UnityEngine;

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
        public float moveSpeed;            // Move Speed (engine thrust / max speed contribution)
        public float moveSpeedPerLevel;    // Move Speed gained per ship level
        public float turnSpeed;            // Turn Speed (rotation speed)
        public float turnSpeedPerLevel;    // Turn Speed gained per ship level

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

        /// <summary>Multiply all ability values by a factor (e.g. normalized scale: scale.x * scale.y * scale.z). Used so stretched components contribute proportionally.</summary>
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

        /// <summary>Normalized scale factor from transform: product of x*y*z. (1,1,1)=1; (4,0.5,1)=2. Use to scale component abilities by physical size.</summary>
        public static float GetNormalizedScaleFromTransform(Transform t)
        {
            if (t == null) return 1f;
            Vector3 s = t.localScale;
            return s.x * s.y * s.z;
        }

        /// <summary>True if the component is a weapon (componentId starts with "Weapon"). Weapons use x*y for fire power and 1/z for fire rate.</summary>
        public static bool IsWeaponComponent(string componentId)
        {
            return !string.IsNullOrEmpty(componentId) && componentId.TrimStart().StartsWith("Weapon", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>True if the component is an engine (componentId starts with "Engine"). Max speed uses largest engine only; thrust sums all engines.</summary>
        public static bool IsEngineComponent(string componentId)
        {
            return !string.IsNullOrEmpty(componentId) && componentId.TrimStart().StartsWith("Engine", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Scale stats by transform.
        /// Weapons: fire power and bullet speed scale by x*y (size); fire rate scales by 1/z (smaller z = faster).
        ///          Other weapon properties (health, energy, etc.) are NOT scaled by transform.
        /// Non-weapons: all stats scale by x*y*z.
        /// </summary>
        public static ShipComponentAbilityStats ScaleStatsByTransform(ShipComponentAbilityStats stats, Transform t, string componentId)
        {
            if (t == null) return stats;
            float x = t.localScale.x;
            float y = t.localScale.y;
            float z = Mathf.Max(t.localScale.z, 0.01f);

            if (IsWeaponComponent(componentId))
            {
                float firePowerScale = x * y;       // size of bullet, damage
                float fireRateScale = 1f / z;       // smaller z = faster rate of fire
                return new ShipComponentAbilityStats
                {
                    firePower = stats.firePower * firePowerScale,
                    firePowerPerLevel = stats.firePowerPerLevel * firePowerScale,
                    bulletSpeed = stats.bulletSpeed * firePowerScale,
                    bulletSpeedPerLevel = stats.bulletSpeedPerLevel * firePowerScale,
                    fireRate = stats.fireRate * fireRateScale,
                    fireRatePerLevel = stats.fireRatePerLevel * fireRateScale,
                    // All other properties are left unscaled for weapons so z-scale only affects fire rate and xy-scale only affects fire power/bullet.
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

            float scale = x * y * z;
            return stats * scale;
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

        [Tooltip("Minimum home planet level required to unlock this chassis in the upgrade tree.")]
        public int minHomePlanetLevel = 1;

        [Tooltip("Approximate overall power score used for auto-ordering (higher = stronger). Sum of power score breakdown categories.")]
        public float powerScore;

        [Tooltip("Editor: heuristic parts of powerScore (offense + defense + energy + mobility + capacity).")]
        public ShipFamilyPowerScoreBreakdown powerScoreBreakdown;
    }

    /// <summary>
    /// ScriptableObject describing all component stats for a single ship family (e.g. AstroEagle).
    /// Child GameObjects named "Family_ComponentId" can be mapped to entries here.
    /// </summary>
    [CreateAssetMenu(fileName = "NewShipFamily", menuName = "Titan Orbit/Ship Family Definition")]
    public class ShipFamilyDefinition : ScriptableObject
    {
        /// <summary>
        /// Multiplier applied at runtime to summed <see cref="ShipComponentAbilityStats.turnSpeed"/> and
        /// <see cref="ShipComponentAbilityStats.turnSpeedPerLevel"/> from chassis preview stats. Lets authored
        /// definition values use a smaller numeric range (e.g. divide assets by 10 when this is 10) while keeping the same turn rate.
        /// </summary>
        public const float AppliedTurnSpeedScale = 10f;

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
    }
}

