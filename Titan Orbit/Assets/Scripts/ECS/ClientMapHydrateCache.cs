using UnityEngine;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [TITAN-ORBIT] Managed progress for client seed-based map hydrate.
    /// Loading UI and GoInGame gating read this instead of GhostSpawn Instantiates proxy counts.
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

        /// <summary>Expected planet+asteroid bodies from the recipe (denominator).</summary>
        public static int ExpectedBodies { get; private set; }

        /// <summary>Bodies Instantiated locally so far (numerator).</summary>
        public static int BuiltBodies { get; private set; }

        /// <summary>True when local hydrate finished (all bodies + claims applied).</summary>
        public static bool IsComplete { get; private set; }

        /// <summary>True when hydrate ran this session (even if ExpectedBodies was 0).</summary>
        public static bool HydrateStarted { get; private set; }

        /// <summary>0–1 local build progress for the loading bar.</summary>
        public static float Progress01
        {
            get
            {
                if (IsComplete)
                    return 1f;
                if (!HasRecipe || ExpectedBodies <= 0)
                    return HasRecipe ? 0.05f : 0f;
                return Mathf.Clamp01((float)BuiltBodies / ExpectedBodies);
            }
        }

        /// <summary>
        /// Latches full recipe when MapSessionMeta arrives (before hydrate runs).
        /// </summary>
        public static void ApplyRecipe(
            uint matchSeed,
            int expectedBodies,
            in MapGenerationConfig config,
            in MapGenerationLogic.AsteroidBodyTuning asteroidBody,
            bool hasFullRecipe)
        {
            MatchSeed = matchSeed;
            ExpectedBodies = Mathf.Max(0, expectedBodies);
            RecipeConfig = config;
            AsteroidBody = asteroidBody;
            HasFullRecipe = hasFullRecipe && matchSeed != 0;
            HasRecipe = HasFullRecipe || expectedBodies > 0;
            if (!HydrateStarted)
            {
                BuiltBodies = 0;
                IsComplete = false;
            }
        }

        /// <summary>Marks that the hydrate system began Instantiates.</summary>
        public static void MarkHydrateStarted(int expectedBodies)
        {
            HydrateStarted = true;
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
            if (ExpectedBodies > 0)
                BuiltBodies = ExpectedBodies;
        }

        /// <summary>Clears session state (disconnect / Play Mode).</summary>
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
        }

#if UNITY_EDITOR
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => Clear();
#endif
    }
}
