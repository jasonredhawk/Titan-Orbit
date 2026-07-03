using Unity.Mathematics;

namespace TitanOrbit.Generation
{
    /// <summary>Toroidal map wrapping for ECS simulation.</summary>
    public static class ToroidalMapEcs
    {
        static float s_MapWidth = 1000f;
        static float s_MapHeight = 1000f;

        public static float MapWidth => s_MapWidth;
        public static float MapHeight => s_MapHeight;

        public static void SetMapSize(float width, float height)
        {
            s_MapWidth = math.max(100f, width);
            s_MapHeight = math.max(100f, height);
        }

        /// <summary>Burst-safe wrap using explicit map dimensions from MapStateSingleton.</summary>
        public static float3 Wrap(float3 position, float mapWidth, float mapHeight)
        {
            float halfW = mapWidth * 0.5f;
            float halfH = mapHeight * 0.5f;
            position.x = math.fmod(position.x + halfW, mapWidth);
            if (position.x < 0f) position.x += mapWidth;
            position.x -= halfW;
            position.z = math.fmod(position.z + halfH, mapHeight);
            if (position.z < 0f) position.z += mapHeight;
            position.z -= halfH;
            return position;
        }

        public static float3 Wrap(float3 position) => Wrap(position, s_MapWidth, s_MapHeight);

        public static float3 ShortestOffsetXZ(float3 from, float3 to, float mapWidth, float mapHeight)
        {
            float dx = to.x - from.x;
            float dz = to.z - from.z;
            dx -= math.round(dx / mapWidth) * mapWidth;
            dz -= math.round(dz / mapHeight) * mapHeight;
            return new float3(dx, 0f, dz);
        }

        public static float ToroidalDistance(float3 a, float3 b, float mapWidth, float mapHeight)
        {
            float3 d = ShortestOffsetXZ(a, b, mapWidth, mapHeight);
            return math.length(new float2(d.x, d.z));
        }

        public static float3 ToroidalDirection(float3 from, float3 to, float mapWidth, float mapHeight)
        {
            float3 offset = ShortestOffsetXZ(from, to, mapWidth, mapHeight);
            if (math.lengthsq(offset) < 0.0001f)
                return new float3(0f, 0f, 1f);
            return math.normalize(offset);
        }

        /// <summary>Nearest toroidal copy of <paramref name="logicalPos"/> to <paramref name="referencePos"/>.</summary>
        public static float3 GetDisplayPosition(float3 logicalPos, float3 referencePos, float mapWidth, float mapHeight)
        {
            float dx = referencePos.x - logicalPos.x;
            float dz = referencePos.z - logicalPos.z;
            int k = (int)math.round(dx / mapWidth);
            int m = (int)math.round(dz / mapHeight);
            return new float3(logicalPos.x + k * mapWidth, logicalPos.y, logicalPos.z + m * mapHeight);
        }

        public static float3 GetDisplayPosition(float3 logicalPos, float3 referencePos) =>
            GetDisplayPosition(logicalPos, referencePos, s_MapWidth, s_MapHeight);

        /// <summary>Display tile with hysteresis to avoid pops near tile boundaries.</summary>
        public static float3 GetDisplayPositionWithHysteresis(
            float3 logicalPos,
            float3 referencePos,
            ref int tileK,
            ref int tileM,
            float mapWidth,
            float mapHeight,
            float switchMarginFraction = 0.35f)
        {
            float dx = referencePos.x - logicalPos.x;
            float dz = referencePos.z - logicalPos.z;
            int candidateK = (int)math.round(dx / mapWidth);
            int candidateM = (int)math.round(dz / mapHeight);

            if (tileK == int.MinValue)
            {
                tileK = candidateK;
                tileM = candidateM;
            }
            else if (candidateK != tileK || candidateM != tileM)
            {
                float3 current = new float3(
                    logicalPos.x + tileK * mapWidth,
                    logicalPos.y,
                    logicalPos.z + tileM * mapHeight);
                float3 candidate = new float3(
                    logicalPos.x + candidateK * mapWidth,
                    logicalPos.y,
                    logicalPos.z + candidateM * mapHeight);
                float currentDistSq = (referencePos.x - current.x) * (referencePos.x - current.x)
                    + (referencePos.z - current.z) * (referencePos.z - current.z);
                float candidateDistSq = (referencePos.x - candidate.x) * (referencePos.x - candidate.x)
                    + (referencePos.z - candidate.z) * (referencePos.z - candidate.z);
                float margin = math.max(1f, switchMarginFraction * math.min(mapWidth, mapHeight));
                if (candidateDistSq < currentDistSq - margin * margin)
                {
                    tileK = candidateK;
                    tileM = candidateM;
                }
            }

            return new float3(
                logicalPos.x + tileK * mapWidth,
                logicalPos.y,
                logicalPos.z + tileM * mapHeight);
        }

        public static float3 GetDisplayPositionWithHysteresis(
            float3 logicalPos,
            float3 referencePos,
            ref int tileK,
            ref int tileM,
            float switchMarginFraction = 0.35f) =>
            GetDisplayPositionWithHysteresis(
                logicalPos,
                referencePos,
                ref tileK,
                ref tileM,
                s_MapWidth,
                s_MapHeight,
                switchMarginFraction);
    }
}
