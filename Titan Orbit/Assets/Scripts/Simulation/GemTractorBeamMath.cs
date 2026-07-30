using TitanOrbit.Generation;
using Unity.Mathematics;

namespace TitanOrbit.Simulation
{
    /// <summary>
    /// Shared reach and pull strength for gem tractor beams. Server physics and client
    /// <see cref="Game.GemTractorBeamVisual"/> read the same formulas so beam length and pull
    /// feel match. Wing-mounted beams use <see cref="ShipWingTractorBeamParams"/>; legacy
    /// max-gems fallback remains for ships without explicit tractor stats.
    /// [TITAN-ORBIT] Restored from original NGO <c>GemTractorBeamSettings</c>: each wing has its
    /// own distance/power; pull speed comes from that wing (not a global gem-mass base speed).
    /// Wings collect via <see cref="GemTractorBeamAssignment"/>: sticky locks, unique gems first,
    /// and spare beams may assist (stack pull) only when there are more wings than free gems,
    /// capped by designer <c>TractorBeamSettings.MaxCooperatingBeams</c>.
    /// Stacked assists use diminishing returns — primary full strength, each extra
    /// <see cref="AdditionalTractorBeamPullScale"/> (or settings AssistPullScale) of its own
    /// strength (not a linear sum). Global range/power multipliers live on TractorBeamSettings
    /// and are applied by ECS/Game call sites after these formulas.
    /// </summary>
    public static class GemTractorBeamMath
    {
        /// <summary>[TITAN-ORBIT] Gem search radius in world units when not in orbit zone.
        /// Legacy reference: wing v1 maxGems (8) maps to this reach in normal space.</summary>
        public const float SearchRadiusNormal = 3f;
        public const float SearchRadiusOrbit = 4.5f;

        /// <summary>Legacy reference: wing v1 maxGems (8) maps to this raw pull speed before gameplay scale.</summary>
        public const float AttractionSpeedNormal = 10f;
        public const float AttractionSpeedOrbit = 16f;

        /// <summary>Scales authored tractor power into slower in-game pull speeds.</summary>
        public const float GameplayPullSpeedScale = 0.38f;
        public const float MinGameplayPullSpeed = 0.75f;
        public const float MaxGameplayPullSpeed = 5.5f;

        /// <summary>
        /// [TITAN-ORBIT] When several beams pull one gem, the primary lock contributes 100% of its
        /// pull speed and each additional assist contributes this fraction of <em>its own</em> speed.
        /// Equal beams: 1 → 100%, 2 → 125%, 3 → 150% (not 200% / 300%).
        /// Used by server <c>GemTractorBeamSystem</c> and client pull presentation so both match.
        /// </summary>
        public const float AdditionalTractorBeamPullScale = 0.25f;

        /// <summary>MaxGems → search radius (m). Wing1 with maxGems=8 → 3m in normal space.</summary>
        public const float MaxGemsToSearchRadius = SearchRadiusNormal / 8f;

        /// <summary>MaxGems → raw pull speed before gameplay scale. Wing1 with maxGems=8 → 10 m/s.</summary>
        public const float MaxGemsToAttractionSpeed = AttractionSpeedNormal / 8f;

        /// <summary>Reference gem size for optional mass factor on top of wing power.</summary>
        public const float ReferenceGemSizeForPull = 0.35f;
        public const float MinGemSizeForPull = 0.2f;
        public const float MinGemMassPullFactor = 0.55f;
        public const float MaxGemMassPullFactor = 1.85f;

        /// <summary>Min speed toward ship (m/s) before a gem counts as actively tractor-pulled.</summary>
        public const float ActivePullTowardSpeedThreshold = 0.22f;

        /// <summary>
        /// Deploy VFX: thin line shoots from wing → gem, then cone mouth opens, then pull starts.
        /// Kept short for snappy feel but long enough to read (not instant).
        /// </summary>
        public const float ExtendLineSpeed = 11f;
        public const float MinExtendDuration = 0.12f;
        public const float MaxExtendDuration = 0.42f;
        public const float WidthExpandDuration = 0.14f;

        /// <summary>
        /// Converts wing Max Gems Capacity (at current ship level) into tractor reach and pull strength.
        /// Used when distance/power authoring fields are unset (legacy wing components).
        /// </summary>
        public static void GetTractorBeamFromMaxGems(float effectiveMaxGems, bool inOrbitZone, out float searchRadius, out float attractionSpeed)
        {
            // --- MaxGems → radius and raw speed ---
            float gems = math.max(0f, effectiveMaxGems);
            searchRadius = gems * MaxGemsToSearchRadius;
            attractionSpeed = gems * MaxGemsToAttractionSpeed;

            ApplyOrbitTractorMultipliers(inOrbitZone, ref searchRadius, ref attractionSpeed);
            searchRadius = math.max(0.5f, searchRadius);
            attractionSpeed = ScaleToGameplayPullSpeed(attractionSpeed);
        }

        /// <summary>Applies <see cref="GameplayPullSpeedScale"/> and clamps to the playable pull band.</summary>
        public static float ScaleToGameplayPullSpeed(float authoredPullSpeed)
        {
            float speed = math.max(0f, authoredPullSpeed) * GameplayPullSpeedScale;
            return math.clamp(speed, MinGameplayPullSpeed, MaxGameplayPullSpeed);
        }

        /// <summary>Orbit ring widens reach and increases pull (original NGO multipliers).</summary>
        public static void ApplyOrbitTractorMultipliers(bool inOrbitZone, ref float searchRadius, ref float attractionSpeed)
        {
            if (!inOrbitZone)
                return;
            searchRadius *= SearchRadiusOrbit / SearchRadiusNormal;
            attractionSpeed *= AttractionSpeedOrbit / AttractionSpeedNormal;
        }

        /// <summary>Orbit-only search radius widen (when pull speed is resolved separately).</summary>
        public static void ApplyOrbitSearchRadiusMultiplier(bool inOrbitZone, ref float searchRadius)
        {
            if (!inOrbitZone)
                return;
            searchRadius *= SearchRadiusOrbit / SearchRadiusNormal;
        }

        /// <summary>Resolves visual/collision gem size for the optional mass pull factor.</summary>
        public static float ResolveGemSizeForPull(float gemValue, float gemSize)
        {
            if (gemSize > 0.001f)
                return gemSize;

            return math.clamp(math.sqrt(math.max(0.25f, gemValue)) * 0.2f, MinGemSizeForPull, 0.5f);
        }

        /// <summary>
        /// Larger gems pull slightly slower, smaller gems slightly faster — mild feel tweak on top
        /// of wing power (does not replace per-wing <see cref="ShipWingTractorBeamParams.TractorBeamPower"/>).
        /// </summary>
        public static float ComputeGemMassPullFactor(float gemSize)
        {
            float size = math.max(MinGemSizeForPull, gemSize);
            float factor = ReferenceGemSizeForPull / size;
            return math.clamp(factor, MinGemMassPullFactor, MaxGemMassPullFactor);
        }

        /// <summary>
        /// Final pull speed for a gem assigned to a wing: wing attraction speed × mild mass factor.
        /// Does <b>not</b> apply multi-beam stacking — callers multiply by
        /// <see cref="StackedBeamPullScale"/> when several wings share one gem.
        /// </summary>
        public static float ResolvePullSpeedFromWing(
            float wingAttractionSpeed,
            float gemValue,
            float gemSize) =>
            math.max(0f, wingAttractionSpeed) *
            ComputeGemMassPullFactor(ResolveGemSizeForPull(gemValue, gemSize));

        /// <summary>
        /// Multi-beam stack weight for one wing's contribution toward a shared gem.
        /// Uses the legacy constant <see cref="AdditionalTractorBeamPullScale"/> (0.25).
        /// Prefer the overload that takes designer AssistPullScale from TractorBeamSettings.
        /// </summary>
        /// <param name="isPrimary">
        /// True for the gem's primary lock (ghost <c>TractorWingIndex</c> / sticky owner).
        /// False for spare-wing assists that only join when no unique free gem is left.
        /// </param>
        /// <returns>
        /// 1.0 for the primary beam; <see cref="AdditionalTractorBeamPullScale"/> for each assist.
        /// </returns>
        public static float StackedBeamPullScale(bool isPrimary) =>
            StackedBeamPullScale(isPrimary, AdditionalTractorBeamPullScale);

        /// <summary>
        /// Multi-beam stack weight with a designer-tunable assist fraction.
        /// </summary>
        /// <param name="isPrimary">True for the gem's primary lock; false for assists.</param>
        /// <param name="assistPullScale">
        /// Fraction of an assist wing's own pull that stacks onto the gem (settings default 0.25).
        /// Clamped to [0, 1] so bad Inspector values cannot invert or explode pull.
        /// </param>
        /// <returns>1.0 for primary; clamped <paramref name="assistPullScale"/> for assists.</returns>
        public static float StackedBeamPullScale(bool isPrimary, float assistPullScale)
        {
            if (isPrimary)
                return 1f;
            // [STANDARD] Clamp — callers may pass raw ScriptableObject fields.
            if (assistPullScale < 0f)
                return 0f;
            if (assistPullScale > 1f)
                return 1f;
            return assistPullScale;
        }

        /// <summary>
        /// Resolves authored tractor stats, falling back to maxGems conversion when distance/power are unset.
        /// </summary>
        public static void GetTractorBeamFromStats(
            float tractorBeamDistance,
            float tractorBeamDistancePerLevel,
            float tractorBeamPower,
            float tractorBeamPowerPerLevel,
            float maxGems,
            float maxGemsPerLevel,
            int shipLevel,
            bool inOrbitZone,
            out float searchRadius,
            out float attractionSpeed)
        {
            // --- Level-scaled authoring ---
            int perLvl = math.max(0, shipLevel - 1);
            searchRadius = tractorBeamDistance + tractorBeamDistancePerLevel * perLvl;
            attractionSpeed = tractorBeamPower + tractorBeamPowerPerLevel * perLvl;

            // --- Legacy fallback: only MaxGems was authored ---
            if (searchRadius <= 0f && attractionSpeed <= 0f)
            {
                float effectiveMaxGems = math.max(0f, maxGems + maxGemsPerLevel * perLvl);
                GetTractorBeamFromMaxGems(effectiveMaxGems, inOrbitZone, out searchRadius, out attractionSpeed);
                return;
            }

            ApplyOrbitTractorMultipliers(inOrbitZone, ref searchRadius, ref attractionSpeed);
            searchRadius = math.max(0.5f, searchRadius);
            attractionSpeed = ScaleToGameplayPullSpeed(attractionSpeed);
        }

        /// <summary>Per-wing params → search radius and gameplay pull speed for that wing.</summary>
        public static void GetWingTractorParams(
            in ShipWingTractorBeamParams wing,
            int shipLevel,
            bool inOrbitZone,
            out float searchRadius,
            out float attractionSpeed)
        {
            GetTractorBeamFromStats(
                wing.TractorBeamDistance,
                wing.TractorBeamDistancePerLevel,
                wing.TractorBeamPower,
                wing.TractorBeamPowerPerLevel,
                wing.MaxGems,
                wing.MaxGemsPerLevel,
                shipLevel,
                inOrbitZone,
                out searchRadius,
                out attractionSpeed);
        }

        public static bool IsWithinReach(float3 gemPos, float3 beamOrigin, float searchRadius, float mapW, float mapH)
        {
            return ToroidalDistance(gemPos, beamOrigin, mapW, mapH) <= searchRadius;
        }

        public static float ComputeExtendDuration(float toroidalDistance)
        {
            float dist = math.max(0f, toroidalDistance);
            return math.clamp(dist / ExtendLineSpeed, MinExtendDuration, MaxExtendDuration);
        }

        /// <summary>
        /// Shortest XZ distance on the Pac-Man map. Delegates to <see cref="ToroidalMapEcs"/>
        /// so beam reach matches combat/docking across seams.
        /// </summary>
        public static float ToroidalDistance(float3 a, float3 b, float mapW, float mapH)
        {
            // --- ToroidalDistance (shortest path on torus) ---
            // [TITAN-ORBIT] Single source of truth — do not reimplement round-period math here.
            return ToroidalMapEcs.ToroidalDistance(a, b, mapW, mapH);
        }

        /// <summary>Shortest XZ offset from → to on the torus (Y zeroed).</summary>
        public static float3 ShortestOffsetXZ(float3 from, float3 to, float mapW, float mapH)
        {
            // --- Periodic delta — ToroidalMapEcs owns the formula ---
            return ToroidalMapEcs.ShortestOffsetXZ(from, to, mapW, mapH);
        }

        /// <summary>Normalized shortest direction from → to on the torus; +Z if coincident.</summary>
        public static float3 ToroidalDirection(float3 from, float3 to, float mapW, float mapH)
        {
            // --- ToroidalDirection ---
            return ToroidalMapEcs.ToroidalDirection(from, to, mapW, mapH);
        }

        public static float3 ResolveWingWorldPosition(float3 shipPos, quaternion shipRot, float3 localPosition)
        {
            // --- Resolve value ---
            float3 pos = shipPos + math.rotate(shipRot, localPosition);
            pos.y = shipPos.y;
            return pos;
        }
    }

    /// <summary>
    /// Per-wing tractor beam tuning baked from ship components. Passed to
    /// <see cref="GemTractorBeamMath.GetWingTractorParams"/> at runtime.
    /// </summary>
    public struct ShipWingTractorBeamParams
    {
        public float3 LocalPosition;
        public float TractorBeamDistance;
        public float TractorBeamDistancePerLevel;
        public float TractorBeamPower;
        public float TractorBeamPowerPerLevel;
        public float MaxGems;
        public float MaxGemsPerLevel;

        public static ShipWingTractorBeamParams DefaultWing => new ShipWingTractorBeamParams
        {
            TractorBeamDistance = 3f,
            TractorBeamDistancePerLevel = 0.75f,
            TractorBeamPower = 4f,
            TractorBeamPowerPerLevel = 1f,
            MaxGems = 8f,
            MaxGemsPerLevel = 2f,
        };
    }
}
