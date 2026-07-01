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
    }
}
