using System;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// [TITAN-ORBIT] Per-level tunables for store-bought homing rockets.
    /// One asset at <c>Resources/RocketCatalog</c> so Editor and player builds share the same file.
    /// Purchase stamps <c>EquippedEquipmentElement.ItemLevel = min(ship, docked planet)</c>;
    /// fire reads that locked level here and does not follow the ship after buy.
    /// Infinite-rocket Editor debug is the exception — those shots use the live ship level.
    /// <para>
    /// Visuals still come from the reserved <c>BulletVfxBank</c> "Rockets" category
    /// (Concussive Push, asteroid bonus, tracers). This catalog owns flight + damage numbers:
    /// fire power, independent flight speed (not ship velocity), lifetime (no max-distance cull),
    /// acquire range, and a level-scaled reload. Mesh size is the row's <c>visualScale</c>
    /// (0.25 = quarter size). 0 falls back to fired-damage vs level 1.
    /// </para>
    /// Paired with <c>ShipRocketFireSystem</c> (server spawn) and <c>RocketHomingLogic</c> (turn).
    /// </summary>
    [CreateAssetMenu(
        fileName = "RocketCatalog",
        menuName = "Titan Orbit/Rocket Catalog",
        order = 62)]
    public class RocketCatalog : ScriptableObject
    {
        /// <summary>[UNITY] Sole asset path — Resources so builds can <see cref="Resources.Load"/>.</summary>
        public const string ResourcesAssetPath = "Assets/Resources/RocketCatalog.asset";

        /// <summary>Name passed to <see cref="Resources.Load"/> (no folder / extension).</summary>
        public const string ResourcesLoadName = "RocketCatalog";

        /// <summary>
        /// Design reference max level. Rows above this clamp to the last authored row.
        /// Matches drone / ship chassis ladder (level 6).
        /// </summary>
        public const int ReferenceMaxLevel = 6;

        /// <summary>
        /// [TITAN-ORBIT] Level-1 seconds between shots when a row leaves cooldown at 0.
        /// Higher rows add <see cref="FireCooldownPerLevelSeconds"/>.
        /// </summary>
        public const float DefaultFireCooldownSeconds = 3f;

        /// <summary>Added to the level-1 cooldown for each level above 1.</summary>
        public const float FireCooldownPerLevelSeconds = 0.5f;

        /// <summary>
        /// Spawn MaxDistance when a row leaves maxDistance at 0 (lifetime is the only cull).
        /// Large enough that <c>Traveled</c> never wins before the timer.
        /// </summary>
        public const float UnlimitedFlightDistance = 1000000f;

        /// <summary>
        /// [TITAN-ORBIT] Search radius when a row leaves acquireRange at 0.
        /// Level-1 default (~50). 0 is never “whole map”.
        /// </summary>
        public const float DefaultAcquireRange = 50f;

        /// <summary>
        /// One purchased rocket tier. Index in <see cref="levels"/> is <c>level - 1</c>
        /// (row 0 = level 1).
        /// </summary>
        [Serializable]
        public struct LevelStats
        {
            [Tooltip("Base damage before Rocket-bank fire-power multipliers. Higher = harder hit.")]
            public float firePower;

            [Tooltip("Flight speed in world units per second (XZ plane).")]
            public float speed;

            [Tooltip("Max yaw rate in degrees per second. Lower = easier to out-turn.")]
            public float turnSpeedDegreesPerSecond;

            [Tooltip("Seconds until the rocket expires if it never hits. Lifetime is the only flight cap.")]
            public float lifetime;

            [Tooltip("Unused when 0 — rockets die on lifetime, not range. Positive values still cull.")]
            public float maxDistance;

            [Tooltip("Toroidal search radius (world units). Closest enemy ship or turret inside this bubble is locked. Empty bubble = fly straight. Never treat 0 as whole-map — Sanitize fills a positive default.")]
            public float acquireRange;

            [Tooltip("Seconds after a shot before the next rocket may fire. Level 1 = 3s, +0.5s per level.")]
            public float fireCooldown;

            [Tooltip("Mesh size vs a 1× rocket (0.25 = quarter size). 0 = derive from fired damage vs level 1.")]
            public float visualScale;
        }

        [Tooltip("Per-level rows. Missing or short arrays fall back to baked defaults.")]
        public LevelStats[] levels;

        static RocketCatalog _cached;

        /// <summary>
        /// Loads the Resources asset once per domain. Missing asset uses baked level-1..6 defaults
        /// so Editor Play still works before the .asset is created.
        /// </summary>
        public static RocketCatalog LoadDefault()
        {
            // --- Cache ---
            // [UNITY] Resources.Load is cheap after the first hit; we keep the instance so fire
            // systems do not reload every tick.
            if (_cached != null)
                return _cached;

            _cached = Resources.Load<RocketCatalog>(ResourcesLoadName);
            if (_cached == null)
            {
                _cached = CreateInstance<RocketCatalog>();
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
        /// Fills empty / zero fields so a half-authored row still flies.
        /// Cooldown defaults to <see cref="DefaultFireCooldownSeconds"/> when ≤ 0.
        /// </summary>
        public static LevelStats Sanitize(LevelStats row)
        {
            // --- Defaults ---
            // [TITAN-ORBIT] Designers can leave 0 on a new row; we never spawn a zero-speed rocket.
            if (row.firePower <= 0.01f) row.firePower = 40f;
            if (row.speed <= 0.01f) row.speed = 16f;
            if (row.turnSpeedDegreesPerSecond <= 0.01f) row.turnSpeedDegreesPerSecond = 90f;
            if (row.lifetime <= 0.01f) row.lifetime = 10f;
            // maxDistance 0 = no range cull (lifetime only). Do not invent a travel budget.
            if (row.acquireRange <= 0.01f) row.acquireRange = DefaultAcquireRange;
            if (row.fireCooldown <= 0.01f) row.fireCooldown = DefaultFireCooldownSeconds;
            // visualScale 0 = RocketShotMath derives size from damage vs L1 (do not force 1).
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
        /// Level 1 is the fastest, shortest-lived pack; higher levels hit harder, fly slower,
        /// live longer, and reload longer. Turn rate still rises so they stay dodgeable.
        /// </summary>
        public static LevelStats[] CreateBakedDefaults()
        {
            // --- Ladder ---
            // Damage is 10× the original pack so a lock feels like a heavy strike.
            // Speed 16 − (level−1). Lifetime 10 + 2×(level−1). Cooldown 3 + 0.5×(level−1).
            // maxDistance 0 = lifetime-only cull.
            return new[]
            {
                Row(40f, 16f, 80f, 10f, 0f, 50f, 3.0f, 1.00f),
                Row(55f, 15f, 90f, 12f, 0f, 60f, 3.5f, 1.30f),
                Row(70f, 14f, 100f, 14f, 0f, 70f, 4.0f, 1.65f),
                Row(85f, 13f, 110f, 16f, 0f, 80f, 4.5f, 2.05f),
                Row(100f, 12f, 120f, 18f, 0f, 90f, 5.0f, 2.50f),
                Row(120f, 11f, 130f, 20f, 0f, 100f, 5.5f, 3.00f),
            };
        }

        /// <summary>Builds one baked row. Search radius grows with level (never 0 / unlimited).</summary>
        static LevelStats Row(
            float firePower,
            float speed,
            float turnDeg,
            float lifetime,
            float maxDistance,
            float acquireRange,
            float cooldown,
            float visualScale)
        {
            return new LevelStats
            {
                firePower = firePower,
                speed = speed,
                turnSpeedDegreesPerSecond = turnDeg,
                lifetime = lifetime,
                maxDistance = maxDistance,
                acquireRange = acquireRange,
                fireCooldown = cooldown,
                visualScale = visualScale,
            };
        }
    }
}
