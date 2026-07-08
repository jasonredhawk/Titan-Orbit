using Unity.Mathematics;

namespace TitanOrbit.Generation
{
    /// <summary>
    /// Flat (non-wrapping) world helpers. The toroidal map was removed in favor of a standard DOTS
    /// flat world, so these methods now use plain Euclidean XZ math. MapWidth/MapHeight are retained
    /// purely as world extent for map generation and minimap scaling — positions are never wrapped.
    /// </summary>
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

        // Flat world: no wrapping.
        public static float3 Wrap(float3 position, float mapWidth, float mapHeight) => position;

        public static float3 Wrap(float3 position) => position;

        public static float3 ShortestOffsetXZ(float3 from, float3 to, float mapWidth, float mapHeight) =>
            new float3(to.x - from.x, 0f, to.z - from.z);

        public static float ToroidalDistance(float3 a, float3 b, float mapWidth, float mapHeight)
        {
            float dx = b.x - a.x;
            float dz = b.z - a.z;
            return math.sqrt(dx * dx + dz * dz);
        }

        public static float3 ToroidalDirection(float3 from, float3 to, float mapWidth, float mapHeight)
        {
            float3 offset = new float3(to.x - from.x, 0f, to.z - from.z);
            if (math.lengthsq(offset) < 0.0001f)
                return new float3(0f, 0f, 1f);
            return math.normalize(offset);
        }

        public static float3 GetDisplayPosition(float3 logicalPos, float3 referencePos, float mapWidth, float mapHeight) =>
            logicalPos;

        public static float3 GetDisplayPosition(float3 logicalPos, float3 referencePos) => logicalPos;

        public static float3 GetDisplayPositionWithHysteresis(
            float3 logicalPos,
            float3 referencePos,
            ref int tileK,
            ref int tileM,
            float mapWidth,
            float mapHeight,
            float switchMarginFraction = 0.35f)
        {
            tileK = 0;
            tileM = 0;
            return logicalPos;
        }

        public static float3 GetDisplayPositionWithHysteresis(
            float3 logicalPos,
            float3 referencePos,
            ref int tileK,
            ref int tileM,
            float switchMarginFraction = 0.35f)
        {
            tileK = 0;
            tileM = 0;
            return logicalPos;
        }
    }
}
