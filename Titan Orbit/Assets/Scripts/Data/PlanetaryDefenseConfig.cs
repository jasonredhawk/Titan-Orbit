using System;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Designer-tunable planetary defense turret recipe for one ship / planet family.
    /// Holds the turret mesh prefab, bullet bank, regen knobs, and Level-1 → Level-6
    /// combat ranges (HP, damage, fire rate, engage-distance multiplier). Runtime systems
    /// resolve this via <see cref="ResolveForFamily"/>:
    /// <c>ShipFamilyDefinition.planetaryDefense</c> → optional
    /// <see cref="PlanetShipFamilyConfig.ShipFamilyEntry.defenseConfig"/> override →
    /// <c>Resources/PlanetaryDefenseConfig</c>.
    /// <para>
    /// [TITAN-ORBIT] Turrets are not NetCode ghosts — this asset only tunes build costs,
    /// combat stats, and which GameObject prefabs the client Instantiates at ghosted slots.
    /// </para>
    /// </summary>
    [CreateAssetMenu(fileName = "PlanetaryDefenseConfig", menuName = "Titan Orbit/Planetary Defense Config")]
    public class PlanetaryDefenseConfig : ScriptableObject
    {
        /// <summary>Resources path used when a family leaves its turret recipe empty.</summary>
        public const string DefaultResourcesPath = "PlanetaryDefenseConfig";

        /// <summary>Max planet / turret level — mirrors economy max without a Simulation cycle.</summary>
        public const int MaxTurretLevel = 6;

        /// <summary>Cached default asset from Resources (or runtime fallback).</summary>
        static PlanetaryDefenseConfig s_Default;

        // --- Prefabs ---

        [Header("Prefabs")]
        [Tooltip("Active turret mesh Instantiated on each pad (static pose; client rotates to aim).")]
        public GameObject visualPrefab;

        [Tooltip("Optional empty/building pad mesh. When null, a simple primitive pad is used.")]
        public GameObject placeholderPrefab;

        // --- Zones ---

        [Header("Placement & Zones")]
        [Tooltip("World-unit radius of the gem auto-deposit zone around each slot.")]
        public float depositZoneRadius = 2.5f;

        // Slot ring radius is not tuned here — PlanetaryDefenseMath places pads at the midpoint
        // between planet surface and orbit-ring centerline (minimap level-dot angles).

        // --- Deposit metronome ---

        [Header("Gem Deposit")]
        [Tooltip("Seconds between automatic cargo→slot gem chunks while a ship sits in the zone.")]
        public float depositChunkIntervalSeconds = 0.5f;

        // --- Health regen (ship-style: delayed after last damage) ---

        [Header("Health Regen")]
        [Tooltip(
            "When true, damaged turrets slowly regenerate HP after they have not taken " +
            "damage for healthRegenDelayAfterDamage seconds (same pattern as ship hull regen).")]
        public bool regenerateHealth = true;

        [Tooltip("HP per second after the out-of-combat delay. Keep low — turrets are durable bases.")]
        public float healthRegenPerSecond = 3f;

        [Tooltip(
            "Seconds after the last hit before regen starts. Ships use ~0.35s; turrets use a " +
            "longer pause so sustained fire keeps them from healing mid-fight.")]
        public float healthRegenDelayAfterDamage = 2.5f;

        // --- Level 1 → Level 6 combat ranges (primary designer knobs) ---

        [Header("Level 1 → Level 6 Ranges (linear interpolate)")]
        [Tooltip("Max HP at turret level 1. Levels 2–5 lerp toward Level 6.")]
        public float healthAtLevel1 = 165f;

        [Tooltip("Max HP at turret level 6.")]
        public float healthAtLevel6 = 660f;

        [Tooltip("Damage per shot at turret level 1 (fire power).")]
        public float damageAtLevel1 = 2f;

        [Tooltip("Damage per shot at turret level 6.")]
        public float damageAtLevel6 = 12.4f;

        [Tooltip("Shots per second at turret level 1.")]
        public float fireRateAtLevel1 = 3f;

        [Tooltip("Shots per second at turret level 6.")]
        public float fireRateAtLevel6 = 3f;

        [Tooltip(
            "Engage range as a multiple of the pad→orbit-ring gap at level 1. " +
            "2 = fire out to twice that radial distance from the pad.")]
        public float engageRangeMultiplierAtLevel1 = 2f;

        [Tooltip(
            "Engage range multiplier at level 6 (same units as Level 1). " +
            "Default 3 = three times the pad→orbit gap.")]
        public float engageRangeMultiplierAtLevel6 = 3f;

        [Tooltip("Bullet speed (world units/sec) at level 1 — also lerped into the ladder.")]
        public float bulletSpeedAtLevel1 = 20f;

        [Tooltip("Bullet speed (world units/sec) at level 6.")]
        public float bulletSpeedAtLevel6 = 35f;

        // --- VFX / bullets ---

        [Header("Bullets")]
        [Tooltip(
            "BulletVfxBank category name (e.g. \"Bullets\"). When empty or unknown, " +
            "combat falls back to the owning ShipFamilyDefinition.bulletPrefabIndex.")]
        public string bulletBankCategoryName = "Bullets";

        [Tooltip(
            "Baseline cosmetic tracer scale (level-1 fire power). Higher turret damage grows " +
            "ScaleMultiplier via BulletVisualScale — same path as ship guns.")]
        public float bulletVisualScale = 1.15f;

        [Tooltip("Max bullet travel distance (world units). Keep ≥ longest engage range.")]
        public float bulletMaxDistance = 55f;

        [Tooltip("Bullet lifetime seconds.")]
        public float bulletLifetimeSeconds = 2.5f;

        // --- Secondary per-level ladder (gems / visual / hit — not driven by ranges) ---

        [Header("Per-Level Secondary (gems / visual / hit)")]
        [Tooltip(
            "Rows for levels 1–6. Combat stats (HP / damage / fire rate / engage / bullet speed) " +
            "are overwritten from the Level 1→6 ranges above. Edit gemsToReachLevel, visualScale, " +
            "and hitRadius here (or leave empty for defaults).")]
        public TurretLevelStats[] levels = Array.Empty<TurretLevelStats>();

        /// <summary>
        /// One rung on the turret upgrade ladder. Combat fields are filled from the asset's
        /// Level-1 / Level-6 ranges; gems / visualScale / hitRadius stay designer-authored.
        /// </summary>
        [Serializable]
        public struct TurretLevelStats
        {
            /// <summary>Gems required to activate this level from the previous rung (or empty).</summary>
            public float gemsToReachLevel;

            /// <summary>Max HP at this turret level (from health range lerp).</summary>
            public float maxHealth;

            /// <summary>Damage per shot (from damage range lerp).</summary>
            public float damage;

            /// <summary>Shots per second (from fire-rate range lerp).</summary>
            public float fireRate;

            /// <summary>Bullet speed (world units / sec).</summary>
            public float bulletSpeed;

            /// <summary>
            /// Engage distance = (pad→orbit gap) × this multiplier.
            /// From engage-range Level 1→6 lerp (default 2 → 3).
            /// </summary>
            public float engageRangeMultiplier;

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
            // Production uses GenericSpaceship4 via the Resources PlanetaryDefenseConfig asset.
            s_Default.EnsureLevelsInitialized();
            return s_Default;
        }

        /// <summary>
        /// Resolves the turret recipe for a planet's ship-family config index.
        /// Order: <see cref="ShipFamilyDefinition.planetaryDefense"/> →
        /// <see cref="PlanetShipFamilyConfig.ShipFamilyEntry.defenseConfig"/> →
        /// <see cref="LoadDefault"/>.
        /// </summary>
        public static PlanetaryDefenseConfig ResolveForFamily(
            PlanetShipFamilyConfig familyConfig,
            int shipFamilyConfigIndex)
        {
            if (familyConfig != null)
            {
                var entry = familyConfig.GetFamilyByConfigIndex(shipFamilyConfigIndex);
                if (entry != null)
                {
                    // --- Primary: authored on the Ship Family Definition ---
                    if (entry.shipFamilyDefinition != null &&
                        entry.shipFamilyDefinition.planetaryDefense != null)
                    {
                        entry.shipFamilyDefinition.planetaryDefense.EnsureLevelsInitialized();
                        return entry.shipFamilyDefinition.planetaryDefense;
                    }

                    // --- Optional list-slot override (rare) ---
                    if (entry.defenseConfig != null)
                    {
                        entry.defenseConfig.EnsureLevelsInitialized();
                        return entry.defenseConfig;
                    }
                }
            }

            return LoadDefault();
        }

        /// <summary>
        /// Ensures the 6-row ladder exists, then overwrites combat stats from the Level 1→6 ranges.
        /// Preserves authored gems / visualScale / hitRadius when already set.
        /// </summary>
        public void EnsureLevelsInitialized()
        {
            EnsureSecondaryRowsExist();
            ApplyCombatRangesToLevels();
        }

        /// <summary>
        /// Linear t for turret level L (1..6): 0 at level 1, 1 at level 6.
        /// </summary>
        public static float LevelLerpT(int turretLevel)
        {
            int level = Mathf.Clamp(turretLevel, 1, MaxTurretLevel);
            return (level - 1) / (float)(MaxTurretLevel - 1);
        }

        /// <summary>
        /// Interpolates a Level-1 → Level-6 authored pair for the given turret level.
        /// </summary>
        public static float LerpLevelRange(float atLevel1, float atLevel6, int turretLevel)
        {
            return Mathf.Lerp(atLevel1, atLevel6, LevelLerpT(turretLevel));
        }

        /// <summary>
        /// Pad→orbit engage multiplier for this turret level (from the fire-distance range).
        /// </summary>
        public float GetEngageRangeMultiplier(int turretLevel)
        {
            EnsureLevelsInitialized();
            return Mathf.Max(0.05f, GetLevelStats(turretLevel).engageRangeMultiplier);
        }

        /// <summary>
        /// Stats for turret level L (1..6). Combat fields always reflect the authored ranges.
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
            if (next < 1 || next > MaxTurretLevel)
                return 0f;
            return Mathf.Max(1f, GetLevelStats(next).gemsToReachLevel);
        }

#if UNITY_EDITOR
        /// <summary>
        /// [EDITOR] Keep the ladder in sync when scrubbing Level 1 / Level 6 knobs in the Inspector.
        /// </summary>
        void OnValidate()
        {
            EnsureLevelsInitialized();
        }
#endif

        /// <summary>
        /// Allocates / pads the secondary ladder (gems, visual, hit) with defaults when missing.
        /// </summary>
        void EnsureSecondaryRowsExist()
        {
            var defaults = GenerateDefaultSecondaryLevelStats();
            if (levels == null || levels.Length == 0)
            {
                levels = defaults;
                return;
            }

            if (levels.Length >= MaxTurretLevel)
                return;

            var merged = new TurretLevelStats[MaxTurretLevel];
            for (int i = 0; i < MaxTurretLevel; i++)
                merged[i] = i < levels.Length ? levels[i] : defaults[i];
            levels = merged;
        }

        /// <summary>
        /// Writes HP / damage / fire rate / engage multiplier / bullet speed from the
        /// Level-1 → Level-6 range fields into every ladder row.
        /// </summary>
        void ApplyCombatRangesToLevels()
        {
            if (levels == null || levels.Length < MaxTurretLevel)
                return;

            for (int i = 0; i < MaxTurretLevel; i++)
            {
                int level = i + 1;
                var row = levels[i];

                // --- Combat from authored ranges ---
                row.maxHealth = Mathf.Max(1f, LerpLevelRange(healthAtLevel1, healthAtLevel6, level));
                row.damage = Mathf.Max(0.05f, LerpLevelRange(damageAtLevel1, damageAtLevel6, level));
                row.fireRate = Mathf.Max(0.05f, LerpLevelRange(fireRateAtLevel1, fireRateAtLevel6, level));
                row.engageRangeMultiplier = Mathf.Max(
                    0.05f,
                    LerpLevelRange(engageRangeMultiplierAtLevel1, engageRangeMultiplierAtLevel6, level));
                row.bulletSpeed = Mathf.Max(
                    1f,
                    LerpLevelRange(bulletSpeedAtLevel1, bulletSpeedAtLevel6, level));

                // --- Secondary: fill zeros with defaults so empty rows still look sane ---
                var fallback = GenerateDefaultSecondaryLevelStats()[i];
                if (row.gemsToReachLevel <= 0f)
                    row.gemsToReachLevel = fallback.gemsToReachLevel;
                if (row.visualScale <= 0f)
                    row.visualScale = fallback.visualScale;
                if (row.hitRadius <= 0f)
                    row.hitRadius = fallback.hitRadius;

                levels[i] = row;
            }
        }

        /// <summary>
        /// Default gems / visualScale / hitRadius (and placeholder combat) for empty ladders.
        /// Combat columns are immediately overwritten by <see cref="ApplyCombatRangesToLevels"/>.
        /// Visual scales are ~20% smaller than the pre-bulk GenericSpaceship4 ladder.
        /// </summary>
        public static TurretLevelStats[] GenerateDefaultSecondaryLevelStats()
        {
            // Gems roughly track a short contribution session.
            // visualScale / hitRadius grow gently with level (~20% under the older pad-gun sizes).
            return new[]
            {
                new TurretLevelStats { gemsToReachLevel = 40f,  visualScale = 0.56f, hitRadius = 0.50f },
                new TurretLevelStats { gemsToReachLevel = 70f,  visualScale = 0.64f, hitRadius = 0.55f },
                new TurretLevelStats { gemsToReachLevel = 110f, visualScale = 0.72f, hitRadius = 0.60f },
                new TurretLevelStats { gemsToReachLevel = 160f, visualScale = 0.80f, hitRadius = 0.65f },
                new TurretLevelStats { gemsToReachLevel = 220f, visualScale = 0.88f, hitRadius = 0.70f },
                new TurretLevelStats { gemsToReachLevel = 300f, visualScale = 1.00f, hitRadius = 0.80f },
            };
        }
    }
}
