using Unity.Mathematics;

namespace TitanOrbit.Simulation
{
    /// <summary>Shared reach and pull strength for gem tractor beams (server physics + client visuals).</summary>
    public static class GemTractorBeamMath
    {
        public const float SearchRadiusNormal = 3f;
        public const float SearchRadiusOrbit = 4.5f;
        public const float BasePullSpeedNormal = 5f;
        public const float BasePullSpeedOrbit = 8f;
        public const float MaxGemsToSearchRadius = SearchRadiusNormal / 8f;
        public const float ReferenceGemSizeForPull = 0.35f;
        public const float MinGemSizeForPull = 0.2f;
        public const float MinGemMassPullFactor = 0.55f;
        public const float MaxGemMassPullFactor = 1.85f;
        public const float ActivePullTowardSpeedThreshold = 2.5f;

        public const float ExtendLineSpeed = 16f;
        public const float MinExtendDuration = 0.07f;
        public const float MaxExtendDuration = 0.32f;
        public const float WidthExpandDuration = 0.05f;

        public static void GetTractorBeamFromMaxGems(float effectiveMaxGems, bool inOrbitZone, out float searchRadius, out float attractionSpeed)
        {
            float gems = math.max(0f, effectiveMaxGems);
            searchRadius = gems * MaxGemsToSearchRadius;
            ApplyOrbitSearchRadiusMultiplier(inOrbitZone, ref searchRadius);
            searchRadius = math.max(0.5f, searchRadius);
            attractionSpeed = GetBasePullSpeed(inOrbitZone);
        }

        public static float GetBasePullSpeed(bool inOrbitZone) =>
            inOrbitZone ? BasePullSpeedOrbit : BasePullSpeedNormal;

        public static float ResolveGemSizeForPull(float gemValue, float gemSize)
        {
            if (gemSize > 0.001f)
                return gemSize;

            return math.clamp(math.sqrt(math.max(0.25f, gemValue)) * 0.2f, MinGemSizeForPull, 0.5f);
        }

        public static float ComputeGemMassPullFactor(float gemSize)
        {
            float size = math.max(MinGemSizeForPull, gemSize);
            float factor = ReferenceGemSizeForPull / size;
            return math.clamp(factor, MinGemMassPullFactor, MaxGemMassPullFactor);
        }

        public static float ResolvePullSpeed(float gemValue, float gemSize, bool inOrbitZone) =>
            GetBasePullSpeed(inOrbitZone) * ComputeGemMassPullFactor(ResolveGemSizeForPull(gemValue, gemSize));

        public static void ApplyOrbitSearchRadiusMultiplier(bool inOrbitZone, ref float searchRadius)
        {
            if (!inOrbitZone)
                return;
            searchRadius *= SearchRadiusOrbit / SearchRadiusNormal;
        }

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
            int perLvl = math.max(0, shipLevel - 1);
            searchRadius = tractorBeamDistance + tractorBeamDistancePerLevel * perLvl;

            if (searchRadius <= 0f && tractorBeamPower <= 0f && tractorBeamPowerPerLevel <= 0f)
            {
                float effectiveMaxGems = math.max(0f, maxGems + maxGemsPerLevel * perLvl);
                GetTractorBeamFromMaxGems(effectiveMaxGems, inOrbitZone, out searchRadius, out attractionSpeed);
                return;
            }

            ApplyOrbitSearchRadiusMultiplier(inOrbitZone, ref searchRadius);
            searchRadius = math.max(0.5f, searchRadius);
            attractionSpeed = GetBasePullSpeed(inOrbitZone);
        }

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

        public static float ToroidalDistance(float3 a, float3 b, float mapW, float mapH)
        {
            float3 d = ShortestOffsetXZ(a, b, mapW, mapH);
            return math.length(new float2(d.x, d.z));
        }

        public static float3 ShortestOffsetXZ(float3 from, float3 to, float mapW, float mapH)
        {
            float dx = to.x - from.x;
            float dz = to.z - from.z;
            dx -= math.round(dx / mapW) * mapW;
            dz -= math.round(dz / mapH) * mapH;
            return new float3(dx, 0f, dz);
        }

        public static float3 ToroidalDirection(float3 from, float3 to, float mapW, float mapH)
        {
            float3 offset = ShortestOffsetXZ(from, to, mapW, mapH);
            if (math.lengthsq(offset) < 0.0001f)
                return new float3(0f, 0f, 1f);
            return math.normalize(offset);
        }

        public static float3 ResolveWingWorldPosition(float3 shipPos, quaternion shipRot, float3 localPosition)
        {
            float3 pos = shipPos + math.rotate(shipRot, localPosition);
            pos.y = shipPos.y;
            return pos;
        }
    }

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
