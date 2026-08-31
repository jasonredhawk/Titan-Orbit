using UnityEngine;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [TITAN-ORBIT] Single source of truth for client seed-hydrate join progress.
    /// <para>
    /// Loading UI, GoInGame, and <see cref="ClientMapHydrateSystem"/> all read this cache.
    /// It is <b>not</b> a fake progress animator — the World bar only moves when asteroids
    /// are actually Instantiated (or when InGame catch-up frames tick after hydrate).
    /// </para>
    /// <para>
    /// <see cref="SessionGeneration"/> increments on Clear and on a fresh recipe so the
    /// hydrate ISystem can drop stale blueprint lists after disconnect / Play without Domain Reload.
    /// </para>
    /// Reset on session leave / Play Mode enter.
    /// </summary>
    public static class ClientMapHydrateCache
    {
        /// <summary>True after a recipe with a valid match seed was latched.</summary>
        public static bool HasRecipe { get; private set; }

        /// <summary>True when the RPC carried a full generation recipe (seed + config).</summary>
        public static bool HasFullRecipe { get; private set; }

        /// <summary>Match seed from <see cref="NetCode.MapSessionMetaRpc"/>.</summary>
        public static uint MatchSeed { get; private set; }

        /// <summary>Generation config copied from the recipe RPC.</summary>
        public static MapGenerationConfig RecipeConfig { get; private set; }

        /// <summary>Asteroid body tuning copied from the recipe RPC (matches server AsteroidSettings).</summary>
        public static MapGenerationLogic.AsteroidBodyTuning AsteroidBody { get; private set; }

        /// <summary>Expected asteroid bodies from the recipe (World bar denominator).</summary>
        public static int ExpectedBodies { get; private set; }

        /// <summary>Asteroids Instantiated locally so far (World bar numerator).</summary>
        public static int BuiltBodies { get; private set; }

        /// <summary>True when local hydrate finished (all asteroids spawned).</summary>
        public static bool IsComplete { get; private set; }

        /// <summary>True when hydrate began Instantiates this generation.</summary>
        public static bool HydrateStarted { get; private set; }

        /// <summary>
        /// True while a full recipe is latched but ClientWorld has no
        /// <c>GamePrefabs.Asteroid</c> yet (SubScene still streaming).
        /// </summary>
        public static bool WaitingForPrefabs { get; set; }

        /// <summary>
        /// Monotonic join generation. <see cref="ClientMapHydrateSystem"/> rebuilds its blueprint
        /// whenever this value changes — leftover ISystem fields cannot stall a new join.
        /// </summary>
        public static int SessionGeneration { get; private set; }

        /// <summary>0–1 local asteroid build progress. 0 until hydrate actually starts.</summary>
        public static float Progress01
        {
            get
            {
                if (IsComplete)
                    return 1f;
                if (!HasFullRecipe || ExpectedBodies <= 0 || !HydrateStarted)
                    return 0f;
                return Mathf.Clamp01((float)BuiltBodies / ExpectedBodies);
            }
        }

        /// <summary>
        /// Short World-bar overlay (counts, or a wait phrase when there is no denominator yet).
        /// Honest — never “looks busy” while waiting.
        /// </summary>
        public static string GetWorldBarStatusLabel()
        {
            if (IsComplete && ExpectedBodies > 0)
                return BuiltBodies + " / " + ExpectedBodies;
            if (HydrateStarted && ExpectedBodies > 0)
                return BuiltBodies + " / " + ExpectedBodies;
            if (HasFullRecipe && WaitingForPrefabs)
                return "Loading map prefabs";
            if (HasFullRecipe)
                return "Preparing map";
            return "Waiting for map recipe";
        }

        /// <summary>
        /// Latches the match recipe. Identical in-flight resends (same seed, hydrate not finished)
        /// are idempotent so the server can retry the RPC without restarting Instantiates.
        /// A leftover complete session, a seed change, or Clear() starts a new generation.
        /// </summary>
        public static void ApplyRecipe(
            uint matchSeed,
            int expectedBodies,
            in MapGenerationConfig config,
            in MapGenerationLogic.AsteroidBodyTuning asteroidBody,
            bool hasFullRecipe)
        {
            bool full = hasFullRecipe && matchSeed != 0;
            bool sameInFlight = HasFullRecipe &&
                                full &&
                                MatchSeed == matchSeed &&
                                HydrateStarted &&
                                !IsComplete;

            MatchSeed = matchSeed;
            RecipeConfig = config;
            AsteroidBody = asteroidBody;
            HasFullRecipe = full;
            HasRecipe = HasFullRecipe || expectedBodies > 0;
            ExpectedBodies = Mathf.Max(ExpectedBodies, Mathf.Max(0, expectedBodies));

            if (sameInFlight)
                return;

            // --- Fresh hydrate generation ---
            // [TITAN-ORBIT] Previous Play (no Domain Reload) or a completed join can leave
            // IsComplete/HydrateStarted true. Forcing a new generation makes the ISystem
            // dispose stale lists and spawn again.
            BuiltBodies = 0;
            IsComplete = false;
            HydrateStarted = false;
            WaitingForPrefabs = false;
            SessionGeneration++;
            // #region agent log
            TitanOrbit.Diagnostics.TitanOrbitDebugSessionLog.Write(
                "D",
                "ClientMapHydrateCache.ApplyRecipe",
                "recipe-applied",
                "{\"full\":" + (full ? "true" : "false") +
                ",\"seed\":" + matchSeed +
                ",\"expectedBodies\":" + ExpectedBodies +
                ",\"sameInFlight\":" + (sameInFlight ? "true" : "false") +
                ",\"gen\":" + SessionGeneration + "}");
            // #endregion
        }

        /// <summary>Marks that the hydrate system began Instantiates for this generation.</summary>
        public static void MarkHydrateStarted(int expectedBodies)
        {
            HydrateStarted = true;
            WaitingForPrefabs = false;
            ExpectedBodies = Mathf.Max(ExpectedBodies, Mathf.Max(0, expectedBodies));
        }

        /// <summary>Updates built count after each hydrate batch.</summary>
        public static void SetBuiltBodies(int built)
        {
            BuiltBodies = Mathf.Max(0, built);
        }

        /// <summary>Marks hydrate finished — GoInGame may proceed.</summary>
        public static void MarkComplete()
        {
            IsComplete = true;
            WaitingForPrefabs = false;
            if (ExpectedBodies > 0)
                BuiltBodies = ExpectedBodies;
        }

        /// <summary>Clears session state (disconnect / Play Mode) and bumps generation.</summary>
        public static void Clear()
        {
            HasRecipe = false;
            HasFullRecipe = false;
            MatchSeed = 0;
            RecipeConfig = default;
            AsteroidBody = default;
            ExpectedBodies = 0;
            BuiltBodies = 0;
            IsComplete = false;
            HydrateStarted = false;
            WaitingForPrefabs = false;
            SessionGeneration++;
            // #region agent log
            TitanOrbit.Diagnostics.TitanOrbitDebugSessionLog.Write(
                "B",
                "ClientMapHydrateCache.Clear",
                "recipe-cleared",
                "{\"gen\":" + SessionGeneration + "}");
            // #endregion
        }

#if UNITY_EDITOR
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => Clear();
#endif
    }
}
