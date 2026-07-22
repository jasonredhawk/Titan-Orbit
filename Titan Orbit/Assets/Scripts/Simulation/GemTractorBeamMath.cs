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
    /// Multiple wings collect in parallel via <see cref="GemTractorBeamAssignment"/> (one gem per
    /// wing, nearest wing owns each gem) — idle beams never stack pull on the same gem.
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
        /// </summary>
        public static float ResolvePullSpeedFromWing(
            float wingAttractionSpeed,
            float gemValue,
            float gemSize) =>
            math.max(0f, wingAttractionSpeed) *
            ComputeGemMassPullFactor(ResolveGemSizeForPull(gemValue, gemSize));

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
        /// Shortest XZ distance on the Pac-Man map. Delegates to <see cref="Generation.ToroidalMapEcs"/>
        /// so beam reach matches combat/docking across seams.
        /// </summary>
        public static float ToroidalDistance(float3 a, float3 b, float mapW, float mapH)
        {
            // --- ToroidalDistance (shortest path on torus) ---
            float3 d = ShortestOffsetXZ(a, b, mapW, mapH);
            return math.length(new float2(d.x, d.z));
        }

        /// <summary>Shortest XZ offset from → to on the torus (Y zeroed).</summary>
        public static float3 ShortestOffsetXZ(float3 from, float3 to, float mapW, float mapH)
        {
            // --- Periodic delta — same formula as ToroidalMapEcs.ShortestOffsetXZ ---
            float dx = to.x - from.x;
            float dz = to.z - from.z;
            dx -= math.round(dx / mapW) * mapW;
            dz -= math.round(dz / mapH) * mapH;
            return new float3(dx, 0f, dz);
        }

        /// <summary>Normalized shortest direction from → to on the torus; +Z if coincident.</summary>
        public static float3 ToroidalDirection(float3 from, float3 to, float mapW, float mapH)
        {
            // --- ToroidalDirection ---
            float3 offset = ShortestOffsetXZ(from, to, mapW, mapH);
            if (math.lengthsq(offset) < 0.0001f)
                return new float3(0f, 0f, 1f);
            return math.normalize(offset);
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
