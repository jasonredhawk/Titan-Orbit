using System;
using TitanOrbit.Core;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// [TITAN-ORBIT] Per-level tunables for store-bought space mines.
    /// One asset at <c>Resources/MineCatalog</c> so Editor and player builds share the same file.
    /// Purchase stamps <c>EquippedEquipmentElement.ItemLevel = min(ship, docked planet)</c>;
    /// deploy and explode read that locked level and do not follow the ship after buy.
    /// Infinite-mine Editor debug is the exception — those drops use the live ship level.
    /// <para>
    /// Mesh is <c>visualPrefab</c> (Bomb_4). Explosion VFX is the catalog-level FireballsV2
    /// impact for the owner team, scaled by the row's <c>explosionVfxScale</c> — applied
    /// directly, not through the bullet-bank 0.25 global multiplier.
    /// </para>
    /// Paired with <c>ShipMineDeploySystem</c> (server place) and <c>MineSimulationSystem</c> (detonate).
    /// </summary>
    [CreateAssetMenu(
        fileName = "MineCatalog",
        menuName = "Titan Orbit/Mine Catalog",
        order = 63)]
    public class MineCatalog : ScriptableObject
    {
        /// <summary>[UNITY] Sole asset path — Resources so builds can <see cref="Resources.Load"/>.</summary>
        public const string ResourcesAssetPath = "Assets/Resources/MineCatalog.asset";

        /// <summary>Name passed to <see cref="Resources.Load"/> (no folder / extension).</summary>
        public const string ResourcesLoadName = "MineCatalog";

        /// <summary>
        /// Design reference max level. Rows above this clamp to the last authored row.
        /// Matches rocket / drone / ship chassis ladder (level 6).
        /// </summary>
        public const int ReferenceMaxLevel = 6;

        /// <summary>[TITAN-ORBIT] Seconds a mine sits in space before it self-destructs (5 minutes).</summary>
        public const float DefaultLifetimeSeconds = 300f;

        /// <summary>Short gap between E taps when a row leaves deployCooldown at 0.</summary>
        public const float DefaultDeployCooldownSeconds = 0.35f;

        /// <summary>Contact radius when a row leaves hitRadius at 0.</summary>
        public const float DefaultHitRadius = 1.2f;

        /// <summary>Concussive AoE radius when a row leaves blastRadius at 0.</summary>
        public const float DefaultBlastRadius = 6f;

        /// <summary>Knockback force when a row leaves blastForce at 0.</summary>
        public const float DefaultBlastForce = 10f;

        /// <summary>FireballsV2-style burst size when a row leaves explosionVfxScale at 0.</summary>
        public const float DefaultExplosionVfxScale = 2f;

        /// <summary>
        /// One purchased mine tier. Index in <see cref="levels"/> is <c>level - 1</c>
        /// (row 0 = level 1).
        /// </summary>
        [Serializable]
        public struct LevelStats
        {
            [Tooltip("Center damage on the contact target and at the blast origin.")]
            public float firePower;

            [Tooltip("Seconds until the mine self-destructs if nothing touches it. 300 = 5 minutes.")]
            public float lifetime;

            [Tooltip("Bomb_4 mesh size vs a 1× mine (2 = double size). 0 = derive from damage vs level 1.")]
            public float visualScale;

            [Tooltip("Contact trigger radius in world units (added to the other body's hull).")]
            public float hitRadius;

            [Tooltip("Concussive AoE radius. Linear falloff to 0 at the edge.")]
            public float blastRadius;

            [Tooltip("Knockback impulse applied to enemy ships in the blast.")]
            public float blastForce;

            [Tooltip("Burst size for a 1× mine. Final VFX = visualScale × this (2 = twice authored FireballsV2 at scale 1).")]
            public float explosionVfxScale;

            [Tooltip("Seconds after a place before the next mine may drop.")]
            public float deployCooldown;
        }

        [Tooltip("Per-level rows. Missing or short arrays fall back to baked defaults.")]
        public LevelStats[] levels;

        [Header("Presentation (all levels)")]
        [Tooltip("Bomb_4 (or equivalent) mesh instantiated by MineVisualDriver.")]
        public GameObject visualPrefab;

        [Tooltip("TeamA — FireballsV2 RedFireImpactV2.")]
        public GameObject explosionVfxRed;

        [Tooltip("TeamB — FireballsV2 BlueFireImpactV2.")]
        public GameObject explosionVfxBlue;

        [Tooltip("TeamC — FireballsV2 GreenFireImpactV2.")]
        public GameObject explosionVfxGreen;

        [Tooltip("TeamD — FireballsV2 YellowFireImpactV2 (bank has no orange impact).")]
        public GameObject explosionVfxYellow;

        [Tooltip("TeamE — FireballsV2 PurpleFireImpactV2.")]
        public GameObject explosionVfxPurple;

        static MineCatalog _cached;

        /// <summary>
        /// Loads the Resources asset once per domain. Missing asset uses baked level-1..6 defaults
        /// so Editor Play still works before the .asset is created.
        /// </summary>
        public static MineCatalog LoadDefault()
        {
            // --- Cache ---
            // [UNITY] Resources.Load is cheap after the first hit; deploy / HUD do not reload every tick.
            if (_cached != null)
                return _cached;

            _cached = Resources.Load<MineCatalog>(ResourcesLoadName);
            if (_cached == null)
            {
                _cached = CreateInstance<MineCatalog>();
                _cached.levels = CreateBakedDefaults();
            }

            return _cached;
        }

        /// <summary>
        /// Stats for a stamped purchase level. Level is clamped to ≥ 1; extra levels reuse the
        /// last authored row (or baked level 6).
        /// </summary>
        /// <param name="itemLevel">Store purchase level (1-based).</param>
        public static LevelStats Get(int itemLevel)
        {
            var catalog = LoadDefault();
            return catalog != null ? catalog.GetStats(itemLevel) : GetBakedStats(itemLevel);
        }

        /// <summary>
        /// FireballsV2 impact prefab for the owner team. Same color map as the bullet bank
        /// (A red, B blue, C green, D yellow, E purple). Falls back to red, then any assigned slot.
        /// </summary>
        public GameObject GetExplosionVfx(TeamId team)
        {
            GameObject picked;
            switch (team)
            {
                case TeamId.TeamB:
                    picked = explosionVfxBlue;
                    break;
                case TeamId.TeamC:
                    picked = explosionVfxGreen;
                    break;
                case TeamId.TeamD:
                    picked = explosionVfxYellow;
                    break;
                case TeamId.TeamE:
                    picked = explosionVfxPurple;
                    break;
                default:
                    picked = explosionVfxRed;
                    break;
            }

            if (picked != null)
                return picked;
            if (explosionVfxRed != null) return explosionVfxRed;
            if (explosionVfxBlue != null) return explosionVfxBlue;
            if (explosionVfxGreen != null) return explosionVfxGreen;
            if (explosionVfxYellow != null) return explosionVfxYellow;
            return explosionVfxPurple;
        }

        /// <summary>
        /// Instance lookup used by the Inspector-owned asset. Same clamp rules as <see cref="Get"/>.
        /// </summary>
        public LevelStats GetStats(int itemLevel)
        {
            // --- Clamp ---
            int level = Mathf.Max(1, itemLevel);
            if (levels == null || levels.Length < 1)
                return GetBakedStats(level);

            int index = Mathf.Min(level, levels.Length) - 1;
            LevelStats row = levels[index];
            return Sanitize(row);
        }

        /// <summary>
        /// Fills empty / zero fields so a half-authored row still detonates.
        /// </summary>
        public static LevelStats Sanitize(LevelStats row)
        {
            // --- Defaults ---
            // [TITAN-ORBIT] Designers can leave 0 on a new row; we never spawn a zero-damage mine.
            if (row.firePower <= 0.01f) row.firePower = 35f;
            if (row.lifetime <= 0.01f) row.lifetime = DefaultLifetimeSeconds;
            if (row.hitRadius <= 0.01f) row.hitRadius = DefaultHitRadius;
            if (row.blastRadius <= 0.01f) row.blastRadius = DefaultBlastRadius;
            if (row.blastForce <= 0.01f) row.blastForce = DefaultBlastForce;
            if (row.explosionVfxScale <= 0.01f) row.explosionVfxScale = DefaultExplosionVfxScale;
            if (row.deployCooldown <= 0.01f) row.deployCooldown = DefaultDeployCooldownSeconds;
            // visualScale 0 = MineShotMath derives size from damage vs L1 (do not force 1).
            return row;
        }

        /// <summary>Baked ladder used when the Resources asset is missing or a row is absent.</summary>
        public static LevelStats GetBakedStats(int itemLevel)
        {
            LevelStats[] baked = CreateBakedDefaults();
            int level = Mathf.Clamp(Mathf.Max(1, itemLevel), 1, baked.Length);
            return Sanitize(baked[level - 1]);
        }

        /// <summary>
        /// Level 1 is a compact pack; higher levels hit harder, sit larger, and blast wider.
        /// Lifetime stays 5 minutes on every row.
        /// </summary>
        public static LevelStats[] CreateBakedDefaults()
        {
            // --- Ladder ---
            // Damage 35 → 100. Mesh 1.0 → 2.5. Hit 1.2 → 2.4. Blast 6 → 12. VFX 2 → 4.
            return new[]
            {
                Row(35f, 300f, 1.00f, 1.20f, 6.0f, 10f, 2.0f, 0.35f),
                Row(48f, 300f, 1.30f, 1.44f, 7.2f, 12f, 2.4f, 0.35f),
                Row(61f, 300f, 1.60f, 1.68f, 8.4f, 14f, 2.8f, 0.35f),
                Row(74f, 300f, 1.90f, 1.92f, 9.6f, 16f, 3.2f, 0.35f),
                Row(87f, 300f, 2.20f, 2.16f, 10.8f, 18f, 3.6f, 0.35f),
                Row(100f, 300f, 2.50f, 2.40f, 12.0f, 20f, 4.0f, 0.35f),
            };
        }

        /// <summary>Builds one baked row. Team explosion prefabs live on the catalog, not the row.</summary>
        static LevelStats Row(
            float firePower,
            float lifetime,
            float visualScale,
            float hitRadius,
            float blastRadius,
            float blastForce,
            float explosionVfxScale,
            float deployCooldown)
        {
            return new LevelStats
            {
                firePower = firePower,
                lifetime = lifetime,
                visualScale = visualScale,
                hitRadius = hitRadius,
                blastRadius = blastRadius,
                blastForce = blastForce,
                explosionVfxScale = explosionVfxScale,
                deployCooldown = deployCooldown,
            };
        }
    }
}
