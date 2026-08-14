using System;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Designer-tunable planetary defense turret recipe for one ship / planet family.
    /// Holds the turret mesh prefab, bullet bank, regen knobs, and Level-1 → Level-6
    /// combat ranges (HP, damage, fire rate, absolute engage range, bullet speed), possession
    /// camera view radius, plus gem costs that default to Solfeggio / cymatic frequencies.
    /// Level 7 is the crown rung (963) — only unlockable when the planet is max level and the
    /// gem-moon reservoir is full. Runtime resolve: <see cref="ResolveForFamily"/>.
    /// <para>
    /// [TITAN-ORBIT] Turrets are not NetCode ghosts — this asset only tunes build costs,
    /// combat stats, client Instantiates prefabs, and possession camera framing.
    /// Engage range is absolute world units (independent of planet size / pad→orbit gap),
    /// defaulting to 20 at Lv1 then +4 per level above 1 (Lv2=24, Lv3=28, …) so equal-level
    /// ships (30 base + 4/level) can out-range turrets while lower-level ships step into fire sooner.
    /// <see cref="cameraViewRadiusAtLevel1"/> / <see cref="cameraViewRadiusAtLevel6"/> control
    /// how far <c>CameraFollowEcs</c> zooms while piloting (match engage range to line up framing).
    /// Acquisition uses per-level engageRange; bullet <c>MaxDistance</c> is computed at fire
    /// time via <c>PlanetaryDefenseAimMath.ComputeBulletMaxDistance</c> (at least engageRange,
    /// longer when lead intercept sits past the acquire sphere).
    /// </para>
    /// </summary>
    [CreateAssetMenu(fileName = "PlanetaryDefenseConfig", menuName = "Titan Orbit/Planetary Defense Config")]
    public class PlanetaryDefenseConfig : ScriptableObject
    {
        /// <summary>Resources path used when a family leaves its turret recipe empty.</summary>
        public const string DefaultResourcesPath = "PlanetaryDefenseConfig";

        /// <summary>
        /// Highest turret level (crown). Levels 1–6 follow the planet; 7 needs the moon gate.
        /// Matches <c>PlanetaryDefenseMath.CrownTurretLevel</c>.
        /// </summary>
        public const int MaxTurretLevel = 7;

        /// <summary>
        /// Standard ladder top used for Level-1 → Level-6 combat lerps (planet max).
        /// Level 7 extrapolates one step past this.
        /// </summary>
        public const int StandardLadderMaxLevel = 6;

        /// <summary>Cached default asset from Resources (or runtime fallback).</summary>
        static PlanetaryDefenseConfig s_Default;

        /// <summary>
        /// True after the first successful ladder build this play session.
        /// [TITAN-ORBIT] GetLevelStats used to call EnsureLevelsInitialized every fire/visual tick,
        /// and EnsureSecondaryRowsExist allocated a fresh defaults array even when levels were full
        /// (Profiler: thousands of allocs/sec → ~half of frame GC). Latch once; Editor OnValidate resets.
        /// </summary>
        [NonSerialized] bool _runtimeLadderReady;

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

        [Tooltip(
            "Seconds the ship must stay nearly still inside a pad zone before gem auto-deposit " +
            "starts. Motion resets the timer. Take Control uses the same still gate on the client.")]
        public float depositRequireStillSeconds = 2f;

        [Tooltip(
            "Planar (XZ) speed below this counts as still for deposit / Take Control " +
            "(world units per second from ShipKinematics).")]
        public float depositStillSpeedEpsilon = 0.15f;

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

        [Header("Level 1 → Level 6 Ranges (linear interpolate; Lv7 extrapolates one step)")]
        [Tooltip("Max HP at turret level 1. Levels 2–5 lerp toward Level 6; Level 7 steps past 6.")]
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
            "Max fire distance from the turret pad at level 1 (world units). " +
            "Independent of planet size — not a pad→orbit multiplier. " +
            "Default 20 (base only — the +4/level step starts at level 2).")]
        public float engageRangeAtLevel1 = 20f;

        [Tooltip(
            "Max fire distance from the turret pad at level 6 (world units). " +
            "Default 40 = 20 at Lv1 + 4 × (levels above 1). Lv2=24, Lv3=28, … Lv6=40.")]
        public float engageRangeAtLevel6 = 40f;

        [Tooltip(
            "Gameplay camera view radius while piloting a level-1 turret (world units). " +
            "CameraFollowEcs zooms so this circle fits on screen. Match engageRangeAtLevel1 " +
            "to line framing with fire distance, or set higher/lower for feel.")]
        public float cameraViewRadiusAtLevel1 = 20f;

        [Tooltip(
            "Gameplay camera view radius while piloting a level-6 turret (world units). " +
            "Lerped like engage range; Lv7 extrapolates one step past Lv6.")]
        public float cameraViewRadiusAtLevel6 = 40f;

        [Tooltip(
            "Bullet speed (world units/sec) at level 1 — lerped into the ladder. " +
            "Combat uses this same value for lead aiming AND spawned BulletElement.Velocity. " +
            "Production Resources asset is slower (~8) than this script default.")]
        public float bulletSpeedAtLevel1 = 20f;

        [Tooltip(
            "Bullet speed (world units/sec) at level 6. Same aim/spawn contract as Level 1. " +
            "Production Resources asset is ~16.")]
        public float bulletSpeedAtLevel6 = 35f;

        // --- Gem cost (Solfeggio / cymatic frequencies by default) ---

        [Header("Gem Cost")]
        [Tooltip(
            "When true, gem costs use Solfeggio frequencies " +
            "(174, 285, 396, 528, 639, 852, 963) — grounding through crown. " +
            "Level 7 (963) is only buildable when planet is max level and the moon gem pool is full. " +
            "When false, costs linear-lerp gemsAtLevel1 → gemsAtLevel6 for Lv1–6, and use gemsAtLevel7 for crown.")]
        public bool useSolfeggioGemCosts = true;

        [Tooltip(
            "Gems for empty → level 1 when useSolfeggioGemCosts is off. " +
            "Default matches Solfeggio foundation (174).")]
        public float gemsAtLevel1 = 174f;

        [Tooltip(
            "Gems for level 5 → level 6 when useSolfeggioGemCosts is off. " +
            "Default matches Solfeggio LA (852).")]
        public float gemsAtLevel6 = 852f;

        [Tooltip(
            "Gems for level 6 → level 7 (crown) when useSolfeggioGemCosts is off. " +
            "Default matches Solfeggio crown (963).")]
        public float gemsAtLevel7 = 963f;

        /// <summary>
        /// Solfeggio tones (Hz) used as gem costs for turret levels 1–7 when
        /// <see cref="useSolfeggioGemCosts"/> is true. Spans foundation → crown:
        /// 174, 285, 396, 528, 639, 852, 963. Level 7 is gameplay-gated by the full moon.
        /// </summary>
        public static readonly float[] SolfeggioGemCostsByLevel =
        {
            174f, // Lv1 — foundation / grounding
            285f, // Lv2 — healing / quantum field (extended Solfeggio)
            396f, // Lv3 — UT
            528f, // Lv4 — MI (often called the “miracle” / transformation tone)
            639f, // Lv5 — FA
            852f, // Lv6 — LA
            963f, // Lv7 — crown (only when planet L6 + moon gems full)
        };

        // --- VFX / bullets ---

        [Header("Bullets")]
        [Tooltip(
            "Unused by combat. Planetary defense fires the owning family's default " +
            "ShipFamilyDefinition.bulletPrefabIndex (and that bank's stat multipliers).")]
        public string bulletBankCategoryName = "";

        [Tooltip(
            "Baseline cosmetic tracer scale (level-1 fire power). Higher turret damage grows " +
            "ScaleMultiplier via BulletVisualScale — same path as ship guns.")]
        public float bulletVisualScale = 1.15f;

        // [TITAN-ORBIT] Bullet MaxDistance is NOT a separate designer knob — combat sets it from
        // engageRange + lead intercept distance (see PlanetaryDefenseAimMath). Lifetime is unused
        // for PD (Lifetime = 0 → distance-only cull in BulletSimulationSystem).
        // bulletSpeedAtLevel* MUST stay in sync with aim: combat passes GetLevelStats(...).bulletSpeed
        // into both the quadratic and BulletElement.Velocity.

        // --- Secondary per-level ladder (visual / hit) ---

        [Header("Per-Level Secondary (visual / hit)")]
        [Tooltip(
            "Rows for levels 1–7. Combat stats and gem costs are overwritten from the ranges / " +
            "Solfeggio table above. Edit visualScale and hitRadius here (or leave empty for defaults).")]
        public TurretLevelStats[] levels = Array.Empty<TurretLevelStats>();

        /// <summary>
        /// One rung on the turret upgrade ladder. Combat + gem fields are filled from the
        /// asset's ranges / Solfeggio table; visualScale / hitRadius stay designer-authored.
        /// </summary>
        [Serializable]
        public struct TurretLevelStats
        {
            /// <summary>
            /// Gems required to activate this level (Solfeggio table or gems range / crown field).
            /// </summary>
            public float gemsToReachLevel;

            /// <summary>Max HP at this turret level (from health range lerp / crown step).</summary>
            public float maxHealth;

            /// <summary>Damage per shot (from damage range lerp / crown step).</summary>
            public float damage;

            /// <summary>Shots per second (from fire-rate range lerp / crown step).</summary>
            public float fireRate;

            /// <summary>Bullet speed (world units / sec).</summary>
            public float bulletSpeed;

            /// <summary>
            /// Absolute engage distance from the turret pad (world units).
            /// From engage-range Level 1→6 lerp (default 20 → 40 = +4 per level above 1); Lv7 steps past 6.
            /// </summary>
            public float engageRange;

            /// <summary>
            /// Camera view radius while a player pilots this level (world units).
            /// From cameraViewRadius Level 1→6 lerp — presentation only, not combat.
            /// </summary>
            public float cameraViewRadius;

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
        /// Ensures the 7-row ladder exists, then overwrites combat + gem stats from ranges /
        /// Solfeggio. Preserves authored visualScale / hitRadius when already set.
        /// Safe to call every frame — after the first success it is a single bool check.
        /// </summary>
        public void EnsureLevelsInitialized()
        {
            // Hot path: combat + client visuals call this via GetLevelStats every tick.
            if (_runtimeLadderReady && levels != null && levels.Length >= MaxTurretLevel)
                return;

            EnsureSecondaryRowsExist();
            ApplyCombatRangesToLevels();
            _runtimeLadderReady = levels != null && levels.Length >= MaxTurretLevel;
        }

        /// <summary>
        /// Linear t for combat ranges: 0 at level 1, 1 at level 6, 1.2 at level 7
        /// (one extrapolated step past the standard ladder).
        /// </summary>
        public static float LevelLerpT(int turretLevel)
        {
            int level = Mathf.Max(1, turretLevel);
            return (level - 1) / (float)(StandardLadderMaxLevel - 1);
        }

        /// <summary>
        /// Interpolates (or extrapolates past Lv6) a Level-1 → Level-6 authored pair.
        /// </summary>
        public static float LerpLevelRange(float atLevel1, float atLevel6, int turretLevel)
        {
            return Mathf.LerpUnclamped(atLevel1, atLevel6, LevelLerpT(turretLevel));
        }

        /// <summary>
        /// Absolute engage range (world units from the pad) for this turret level.
        /// </summary>
        public float GetEngageRange(int turretLevel)
        {
            EnsureLevelsInitialized();
            return Mathf.Max(0.5f, GetLevelStats(turretLevel).engageRange);
        }

        /// <summary>
        /// Camera view radius (world units) while piloting this turret level.
        /// Used by client possession framing — not combat.
        /// </summary>
        public float GetCameraViewRadius(int turretLevel)
        {
            EnsureLevelsInitialized();
            return Mathf.Max(0.5f, GetLevelStats(turretLevel).cameraViewRadius);
        }

        /// <summary>
        /// Stats for turret level L (1..7). Combat fields always reflect the authored ranges.
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
            // Designer changed ranges — force a rebuild so Inspector preview matches knobs.
            _runtimeLadderReady = false;
            EnsureLevelsInitialized();
        }
#endif

        /// <summary>
        /// Allocates / pads the secondary ladder (visual, hit) with defaults when missing.
        /// Does <b>not</b> allocate when <see cref="levels"/> is already length <see cref="MaxTurretLevel"/>.
        /// </summary>
        void EnsureSecondaryRowsExist()
        {
            // --- Fast path: ladder already sized (common after first EnsureLevelsInitialized) ---
            // [TITAN-ORBIT] Must return BEFORE GenerateDefaultSecondaryLevelStats — that new[] was
            // the Profiler GC storm (thousands of allocs/sec when GetLevelStats ran every tick).
            if (levels != null && levels.Length >= MaxTurretLevel)
                return;

            var defaults = GenerateDefaultSecondaryLevelStats();
            if (levels == null || levels.Length == 0)
            {
                levels = defaults;
                return;
            }

            var merged = new TurretLevelStats[MaxTurretLevel];
            for (int i = 0; i < MaxTurretLevel; i++)
                merged[i] = i < levels.Length ? levels[i] : defaults[i];
            levels = merged;
        }

        /// <summary>
        /// Writes HP / damage / fire rate / engage / bullet speed / gem cost from the
        /// Level-1 → Level-6 ranges (Lv7 extrapolated) and Solfeggio / crown gem table.
        /// </summary>
        void ApplyCombatRangesToLevels()
        {
            if (levels == null || levels.Length < MaxTurretLevel)
                return;

            for (int i = 0; i < MaxTurretLevel; i++)
            {
                int level = i + 1;
                var row = levels[i];

                // --- Combat from authored ranges (Lv7 = one step past Lv6) ---
                row.maxHealth = Mathf.Max(1f, LerpLevelRange(healthAtLevel1, healthAtLevel6, level));
                row.damage = Mathf.Max(0.05f, LerpLevelRange(damageAtLevel1, damageAtLevel6, level));
                row.fireRate = Mathf.Max(0.05f, LerpLevelRange(fireRateAtLevel1, fireRateAtLevel6, level));
                // Absolute world units — not pad→orbit gap × multiplier.
                // Defaults 20 → 40: Lv1 = 20 base, then +4 each level above 1 (Lv2=24 … Lv6=40).
                row.engageRange = Mathf.Max(
                    0.5f,
                    LerpLevelRange(engageRangeAtLevel1, engageRangeAtLevel6, level));
                // [TITAN-ORBIT] Possession camera framing — independent of engage so designers
                // can match fire range or bias zoom without changing combat reach.
                row.cameraViewRadius = Mathf.Max(
                    0.5f,
                    LerpLevelRange(cameraViewRadiusAtLevel1, cameraViewRadiusAtLevel6, level));
                row.bulletSpeed = Mathf.Max(
                    1f,
                    LerpLevelRange(bulletSpeedAtLevel1, bulletSpeedAtLevel6, level));

                // --- Gem cost: Solfeggio / crown, or linear + crown field ---
                // [TITAN-ORBIT] Exact Solfeggio Hz values (not a lerp) so each pad level
                // lands on a tone associated with distinct cymatic geometry.
                if (useSolfeggioGemCosts)
                {
                    row.gemsToReachLevel = Mathf.Max(1f, SolfeggioGemCostsByLevel[i]);
                }
                else if (level >= MaxTurretLevel)
                {
                    row.gemsToReachLevel = Mathf.Max(1f, gemsAtLevel7);
                }
                else
                {
                    row.gemsToReachLevel = Mathf.Max(
                        1f,
                        LerpLevelRange(gemsAtLevel1, gemsAtLevel6, level));
                }

                // --- Secondary: fill zeros with defaults so empty rows still look sane ---
                var fallback = GenerateDefaultSecondaryLevelStats()[i];
                if (row.visualScale <= 0f)
                    row.visualScale = fallback.visualScale;
                if (row.hitRadius <= 0f)
                    row.hitRadius = fallback.hitRadius;

                levels[i] = row;
            }
        }

        /// <summary>
        /// Default visualScale / hitRadius (and placeholder combat/gems) for empty ladders.
        /// Combat + gem columns are immediately overwritten by <see cref="ApplyCombatRangesToLevels"/>.
        /// </summary>
        public static TurretLevelStats[] GenerateDefaultSecondaryLevelStats()
        {
            // visualScale / hitRadius grow gently; Lv7 is a small crown bump.
            // Gem placeholders match Solfeggio 174 → 963.
            return new[]
            {
                new TurretLevelStats { gemsToReachLevel = 174f, visualScale = 0.56f, hitRadius = 0.50f },
                new TurretLevelStats { gemsToReachLevel = 285f, visualScale = 0.64f, hitRadius = 0.55f },
                new TurretLevelStats { gemsToReachLevel = 396f, visualScale = 0.72f, hitRadius = 0.60f },
                new TurretLevelStats { gemsToReachLevel = 528f, visualScale = 0.80f, hitRadius = 0.65f },
                new TurretLevelStats { gemsToReachLevel = 639f, visualScale = 0.88f, hitRadius = 0.70f },
                new TurretLevelStats { gemsToReachLevel = 852f, visualScale = 1.00f, hitRadius = 0.80f },
                new TurretLevelStats { gemsToReachLevel = 963f, visualScale = 1.10f, hitRadius = 0.85f },
            };
        }
    }
}
