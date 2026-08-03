using System;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Designer-tunable planetary defense turret recipe for one planet flavor.
    /// Different planet families can point at different assets (machine-gun vs cannon, etc.).
    /// Runtime systems load via <see cref="LoadDefault"/> or
    /// <see cref="PlanetShipFamilyConfig.ShipFamilyEntry.defenseConfig"/>.
    /// <para>
    /// [TITAN-ORBIT] Turrets are not NetCode ghosts — this asset only tunes build costs, combat
    /// stats, and which GameObject prefabs the client Instantiates at ghosted slot positions.
    /// </para>
    /// </summary>
    [CreateAssetMenu(fileName = "PlanetaryDefenseConfig", menuName = "Titan Orbit/Planetary Defense Config")]
    public class PlanetaryDefenseConfig : ScriptableObject
    {
        /// <summary>Resources path used when a family entry leaves <c>defenseConfig</c> empty.</summary>
        public const string DefaultResourcesPath = "PlanetaryDefenseConfig";

        /// <summary>Cached default asset from Resources (or runtime fallback).</summary>
        static PlanetaryDefenseConfig s_Default;

        // --- Prefabs ---

        [Header("Prefabs")]
        [Tooltip("Active turret mesh. Default: FighterDrone (static pose, rotates to aim).")]
        public GameObject visualPrefab;

        [Tooltip("Optional empty/building pad mesh. When null, a simple primitive pad is used.")]
        public GameObject placeholderPrefab;

        // --- Zones & range ---

        [Header("Placement & Zones")]
        [Tooltip("World-unit radius of the gem auto-deposit zone around each slot.")]
        public float depositZoneRadius = 2.5f;

        [Tooltip(
            "Engage range is measured from the turret pad (not planet center): " +
            "(distance from pad to orbit-ring centerline) × (1 + this). " +
            "1.0 ≈ twice the pad→orbit gap (fire out to 2× that distance).")]
        [Range(0f, 2f)]
        public float rangeBeyondOrbitOuter = 1.0f;

        // Slot ring radius is not tuned here — PlanetaryDefenseMath places pads at the midpoint
        // between planet surface and orbit-ring centerline (minimap level-dot angles).

        // --- Deposit metronome ---

        [Header("Gem Deposit")]
        [Tooltip("Seconds between automatic cargo→slot gem chunks while a ship sits in the zone.")]
        public float depositChunkIntervalSeconds = 0.5f;

        // --- Health regen (off by default) ---

        [Header("Health")]
        [Tooltip("When false (default), turrets never regenerate HP.")]
        public bool regenerateHealth = false;

        [Tooltip("HP per second when regenerateHealth is true. Ignored when regen is off.")]
        public float healthRegenPerSecond = 0f;

        // --- VFX ---

        [Header("Bullets")]
        [Tooltip("BulletVfxBank category name (matches fighter drones: \"Bullets\").")]
        public string bulletBankCategoryName = "Bullets";

        [Tooltip(
            "Baseline cosmetic tracer scale (level-1 fire power). Higher turret damage grows " +
            "ScaleMultiplier via BulletVisualScale — same path as ship guns.")]
        public float bulletVisualScale = 1.15f;

        [Tooltip("Max bullet travel distance (world units).")]
        public float bulletMaxDistance = 55f;

        [Tooltip("Bullet lifetime seconds.")]
        public float bulletLifetimeSeconds = 2.5f;

        // --- Per-level ladder (index 0 = turret level 1) ---

        [Header("Turret Levels (1..6)")]
        [Tooltip("Stats for turret levels 1–6. Empty entries fall back to GenerateDefaultLevelStats.")]
        public TurretLevelStats[] levels = Array.Empty<TurretLevelStats>();

        /// <summary>
        /// One rung on the turret upgrade ladder. Gems to reach this level fill from the previous
        /// level (or from empty for level 1).
        /// </summary>
        [Serializable]
        public struct TurretLevelStats
        {
            /// <summary>Gems required to activate this level from the previous rung (or empty).</summary>
            public float gemsToReachLevel;

            /// <summary>Max HP at this turret level.</summary>
            public float maxHealth;

            /// <summary>Damage per shot.</summary>
            public float damage;

            /// <summary>Shots per second.</summary>
            public float fireRate;

            /// <summary>Bullet speed (world units / sec).</summary>
            public float bulletSpeed;

            /// <summary>Visual scale multiplier on the prefab root (1 = authored size).</summary>
            public float visualScale;

            /// <summary>Authoritative hit-sphere radius (world units) at this level.</summary>
            public float hitRadius;
        }

        /// <summary>
        /// Loads <c>Resources/PlanetaryDefenseConfig</c>, or builds an in-memory fallback so
        /// Editor/play never hard-crashes when the asset is missing.
        /// </summary>
        public static PlanetaryDefenseConfig LoadDefault()
        {
            if (s_Default != null)
                return s_Default;

            s_Default = Resources.Load<PlanetaryDefenseConfig>(DefaultResourcesPath);
            if (s_Default != null)
            {
                s_Default.EnsureLevelsInitialized();
                return s_Default;
            }

            // --- Runtime fallback (no asset on disk yet) ---
            s_Default = CreateInstance<PlanetaryDefenseConfig>();
            s_Default.name = "PlanetaryDefenseConfig_RuntimeFallback";
            s_Default.visualPrefab = Resources.Load<GameObject>("FighterDrone");
            s_Default.EnsureLevelsInitialized();
            return s_Default;
        }

        /// <summary>
        /// Resolves config for a planet family index. Falls back to <see cref="LoadDefault"/> when
        /// the family entry is missing or has no override.
        /// </summary>
        public static PlanetaryDefenseConfig ResolveForFamily(
            PlanetShipFamilyConfig familyConfig,
            int shipFamilyConfigIndex)
        {
            if (familyConfig != null)
            {
                var entry = familyConfig.GetFamilyByConfigIndex(shipFamilyConfigIndex);
                if (entry != null && entry.defenseConfig != null)
                {
                    entry.defenseConfig.EnsureLevelsInitialized();
                    return entry.defenseConfig;
                }
            }

            return LoadDefault();
        }

        /// <summary>Fills missing level rows with sensible defaults (1..6).</summary>
        public void EnsureLevelsInitialized()
        {
            if (levels != null && levels.Length >= PlanetEconomyMathMaxLevel)
                return;

            var defaults = GenerateDefaultLevelStats();
            if (levels == null || levels.Length == 0)
            {
                levels = defaults;
                return;
            }

            // Keep authored rows; pad the rest.
            var merged = new TurretLevelStats[PlanetEconomyMathMaxLevel];
            for (int i = 0; i < PlanetEconomyMathMaxLevel; i++)
                merged[i] = i < levels.Length ? levels[i] : defaults[i];
            levels = merged;
        }

        /// <summary>Max planet/turret level — mirrors <c>PlanetEconomyMath.MaxPlanetLevel</c> without a Simulation reference cycle.</summary>
        const int PlanetEconomyMathMaxLevel = 6;

        /// <summary>Default ladder used when the asset array is empty.</summary>
        public static TurretLevelStats[] GenerateDefaultLevelStats()
        {
            // Gems roughly track a short contribution session.
            // Damage sits well above fighter-drone chips (~0.7–1.5) and near/above starter ship
            // guns (~3) so planetary defense feels like a real base threat.
            return new[]
            {
                // Fire rate is flat 3/s at every level. Bullet speed starts near regular ship
                // guns (~20) and steps up with turret level (same spirit as ship chassis leveling).
                // Damage = prior ladder × 0.4 (−60%). Lv1 = 2.
                new TurretLevelStats { gemsToReachLevel = 40f,  maxHealth = 55f,  damage = 2f,   fireRate = 3f, bulletSpeed = 20f, visualScale = 0.55f, hitRadius = 0.40f },
                new TurretLevelStats { gemsToReachLevel = 70f,  maxHealth = 75f,  damage = 3.2f, fireRate = 3f, bulletSpeed = 23f, visualScale = 0.65f, hitRadius = 0.45f },
                new TurretLevelStats { gemsToReachLevel = 110f, maxHealth = 100f, damage = 4.8f, fireRate = 3f, bulletSpeed = 26f, visualScale = 0.75f, hitRadius = 0.50f },
                new TurretLevelStats { gemsToReachLevel = 160f, maxHealth = 130f, damage = 6.8f, fireRate = 3f, bulletSpeed = 29f, visualScale = 0.85f, hitRadius = 0.55f },
                new TurretLevelStats { gemsToReachLevel = 220f, maxHealth = 170f, damage = 9.2f, fireRate = 3f, bulletSpeed = 32f, visualScale = 0.95f, hitRadius = 0.60f },
                new TurretLevelStats { gemsToReachLevel = 300f, maxHealth = 220f, damage = 12.4f, fireRate = 3f, bulletSpeed = 35f, visualScale = 1.10f, hitRadius = 0.70f },
            };
        }

        /// <summary>
        /// Stats for turret level L (1..6). Returns defaults when out of range.
        /// </summary>
        public TurretLevelStats GetLevelStats(int turretLevel)
        {
            EnsureLevelsInitialized();
            int idx = Mathf.Clamp(turretLevel, 1, levels.Length) - 1;
            return levels[idx];
        }

        /// <summary>
        /// Gems needed to go from <paramref name="currentTurretLevel"/> to the next rung.
        /// <paramref name="currentTurretLevel"/> 0 means empty → level 1.
        /// </summary>
        public float GetGemsToNextLevel(int currentTurretLevel)
        {
            int next = currentTurretLevel + 1;
            if (next < 1 || next > PlanetEconomyMathMaxLevel)
                return 0f;
            return Mathf.Max(1f, GetLevelStats(next).gemsToReachLevel);
        }
    }
}
