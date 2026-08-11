using System;
using System.Collections.Generic;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// One designer / AI balancing request — plain-language goals the Cursor rebalance pass should honor.
    /// Examples: "Ships should feel fast and nimble", "About 3 average ships should capture an equal-level planet".
    /// Stored on <see cref="RebalanceGame"/> and exported into the Cursor prompt with the linked assets.
    /// </summary>
    [Serializable]
    public class RebalanceGameRequest
    {
        /// <summary>Short label shown in the Inspector list (e.g. Capture pace).</summary>
        public string title = "New request";

        /// <summary>
        /// Full request in natural language. Cursor agents treat this as a hard design constraint
        /// when rewriting ProfileSet / family / asteroid / planet knobs.
        /// </summary>
        [TextArea(2, 8)]
        public string request =
            "Describe the feel or numeric goal (e.g. low-level ships faster but weaker than high-level).";

        /// <summary>When false, export / AI pass skips this row (keep for later).</summary>
        public bool enabled = true;

        /// <summary>Optional priority: higher runs first in the exported prompt (1–100).</summary>
        [Range(1, 100)]
        public int priority = 50;
    }

    /// <summary>
    /// One fleet-wide aggregate row cached on <see cref="RebalanceGame"/> after Refresh Review
    /// (median wings, peopleCap, DPS, …) — reviewed in the Inspector, not a CSV.
    /// </summary>
    [Serializable]
    public class RebalanceGameAggregateRow
    {
        public string metricName;
        public float min;
        public float p10;
        public float median;
        public float mean;
        public float p90;
        public float max;
        public int sampleCount;
    }

    /// <summary>
    /// One chassis outlier cached on <see cref="RebalanceGame"/> after Refresh Review.
    /// Fix-class strings match the analyzer (profile / structural / wing nerf).
    /// </summary>
    [Serializable]
    public class RebalanceGameOutlierRow
    {
        public float severity;
        public string familyId;
        public string chassisId;
        public string prefabName;
        public int shipLevel;
        public int wings;
        public int engines;
        public int thrusters;
        public int propulsion;
        public int weapons;
        public float moveSpeed;
        public float dps;
        public float gemCap;
        public float peopleCap;
        public float powerScore;
        /// <summary>Pipe-separated flags (e.g. propulsion_starvation|cargo_freak_gems).</summary>
        public string flags;
        /// <summary>Pipe-separated fix classes for designers / AI.</summary>
        public string fixClass;
    }

    /// <summary>
    /// One economy gate row (TTK, capture batches, gemCap vs target) cached after Refresh Review.
    /// </summary>
    [Serializable]
    public class RebalanceGameEconomyCheckRow
    {
        public string checkId;
        public string value;
        public string targetOrNote;
        /// <summary>PASS / FAIL / WARN / INFO.</summary>
        public string status;
    }

    /// <summary>
    /// Hub asset for Titan Orbit game balance: references every tunable Resources / family asset,
    /// holds plain-language <see cref="balanceRequests"/>, and caches fleet / outlier / economy
    /// review data for in-Inspector inspection after a rebalance pass.
    /// <para>
    /// Workflow (Editor):
    /// 1. Open this asset → Auto-Find References.
    /// 2. Edit balance request list (feel + numeric goals).
    /// 3. Export For Cursor → agents update ProfileSet / families / world SOs from the requests.
    /// 4. Apply Local Pipeline (optional seed push) → Refresh Review → read outliers &amp; aggregates here.
    /// </para>
    /// Create via Assets → Create → Titan Orbit → Rebalance Game. Prefer one asset under Resources.
    /// </summary>
    [CreateAssetMenu(
        fileName = "RebalanceGame",
        menuName = "Titan Orbit/Rebalance Game",
        order = 10)]
    public class RebalanceGame : ScriptableObject
    {
        // --- Linked balance assets (Resources + ship families) ---

        [Header("Ship families & part curves")]
        [Tooltip("Shared Part Profile curves used by Scan / Recalculate on every family.")]
        public ShipFamilyPartCalcProfileSet partCalcProfileSet;

        [Tooltip("All ShipFamilyDefinition assets (usually under Prefabs/Ships). Auto-Find fills this.")]
        public List<ShipFamilyDefinition> shipFamilies = new List<ShipFamilyDefinition>();

        [Tooltip("Planet → family ladder + chassis unlock / purchase wiring.")]
        public PlanetShipFamilyConfig planetShipFamilyConfig;

        [Header("World / combat / economy (Resources)")]
        public AsteroidSettings asteroidSettings;
        public MapGenerationSettings mapGenerationSettings;
        public GemExplosionSettings gemExplosionSettings;
        public ShipRammingSettings shipRammingSettings;
        public ShipCargoMobilitySettings shipCargoMobilitySettings;
        public TractorBeamSettings tractorBeamSettings;
        public PlanetaryDefenseConfig planetaryDefenseConfig;

        [Header("Optional / legacy")]
        [Tooltip("Legacy upgrade DAG — costs largely superseded by 2× gemCap; kept for reference.")]
        public UpgradeTree upgradeTree;

        // --- Design requests for Cursor AI ---

        [Header("Balancing requests (Cursor AI)")]
        [Tooltip("Natural-language goals exported with asset inventory for Cursor agents.")]
        public List<RebalanceGameRequest> balanceRequests = new List<RebalanceGameRequest>();

        [Tooltip("Extra notes appended to every Cursor export (session length, team sizes, …).")]
        [TextArea(2, 6)]
        public string sessionNotes =
            "2–5 teams × ~20 players, domination matches ~0.5–2 hours. " +
            "Capture all planets to win. Balance ship levels vs planet levels vs turrets.";

        // --- Cached review (Inspector — not CSV) ---

        [Header("Cached review (Refresh Review button)")]
        [Tooltip("UTC time of last Refresh Review (Inspector).")]
        public string lastReviewUtc;

        [Tooltip("How many chassis were scanned last review.")]
        public int lastChassisCount;

        [TextArea(3, 12)]
        [Tooltip("Human summary: targets header + economy PASS/FAIL lines.")]
        public string lastReviewSummary;

        [Tooltip("Global fleet aggregates (min / median / max …).")]
        public List<RebalanceGameAggregateRow> fleetAggregates = new List<RebalanceGameAggregateRow>();

        [Tooltip("Worst chassis outliers with fix-class tags.")]
        public List<RebalanceGameOutlierRow> outliers = new List<RebalanceGameOutlierRow>();

        [Tooltip("Economy cross-check rows vs GameBalanceTargets.")]
        public List<RebalanceGameEconomyCheckRow> economyChecks = new List<RebalanceGameEconomyCheckRow>();

        /// <summary>
        /// Seeds a sensible default request list the first time the asset is created.
        /// Designers edit freely afterward.
        /// </summary>
        public void EnsureDefaultBalanceRequests()
        {
            if (balanceRequests == null)
                balanceRequests = new List<RebalanceGameRequest>();
            if (balanceRequests.Count > 0)
                return;

            balanceRequests.Add(new RebalanceGameRequest
            {
                title = "Ship feel — fast & nimble",
                request =
                    "Ships should feel fast and nimble: continuous inertial thrust, readable turn rates, " +
                    "and playable single-engine hulls. Avoid Hippo-class paralysis (many wings, tiny propulsion).",
                priority = 90
            });
            balanceRequests.Add(new RebalanceGameRequest
            {
                title = "Tier progression",
                request =
                    "Low-level ships should be faster / more agile but less powerful than higher-level ships. " +
                    "Higher tiers trade some agility for firepower, cargo, and survivability.",
                priority = 85
            });
            balanceRequests.Add(new RebalanceGameRequest
            {
                title = "Capture with ~3 ships",
                request =
                    "About 3 ships with average people capacity for their level should be able to capture " +
                    "an equal-level average-sized planet (full population) without needing a 10-ship zerg. " +
                    "Coordinate with planet population caps and unload batch sizes.",
                priority = 95
            });
            balanceRequests.Add(new RebalanceGameRequest
            {
                title = "Planet regen pace",
                request =
                    "Planet population regeneration must not be too fast — freshly captured empty planets " +
                    "should stay vulnerable long enough for counter-play (current FullRefillSeconds ≈ 120s is a baseline).",
                priority = 70
            });
            balanceRequests.Add(new RebalanceGameRequest
            {
                title = "Asteroid combat loop",
                request =
                    "Mid asteroids should take roughly 8–12 seconds for a median L1 DPS ship to kill. " +
                    "Gem capacity vs mining rate should support active loops, not instant full cargo.",
                priority = 80
            });
            balanceRequests.Add(new RebalanceGameRequest
            {
                title = "Energy complementarity",
                request =
                    "Engines are the energy source; weapons spend energy to fire; overdrive bursts spend energy for speed. " +
                    "Sustained fire must drain the pool (regen below firePower×fireRate). Cards are sidegrades, not a second engine stack.",
                priority = 75
            });
            balanceRequests.Add(new RebalanceGameRequest
            {
                title = "Power score cargo weight",
                request =
                    "Gem capacity must not dominate ship power score vs firepower — keep gem power contribution ≈ rawGems/10, " +
                    "people ≈ rawPeople/4, so upgrade trees sort by combat+mobility meaningfully.",
                priority = 65
            });
        }
    }
}
