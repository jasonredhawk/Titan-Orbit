using Unity.Mathematics;

namespace TitanOrbit.Generation
{
    /// <summary>
    /// Flat-world XZ math helpers used by ECS sim and minimap. The game no longer wraps positions
    /// at map edges (toroidal play was removed); method names retain "Toroidal" for call-site
    /// stability but implement plain Euclidean distance. MapWidth/MapHeight define world extent
    /// for generation and UI scaling only. Updated from <see cref="MapStateSingleton"/> at boot.
    /// </summary>
    public static class ToroidalMapEcs
    {
        // [TITAN-ORBIT] Defaults until MapGenerationSettings bake or runtime singleton sets size.
        static float s_MapWidth = 1000f;
        static float s_MapHeight = 1000f;

        /// <summary>Current world width in Unity units (XZ plane).</summary>
        public static float MapWidth => s_MapWidth;

        /// <summary>Current world height in Unity units (XZ plane).</summary>
        public static float MapHeight => s_MapHeight;

        /// <summary>
        /// Called when map generation completes or settings load — clamps to minimum 100 units.
        /// </summary>
        public static void SetMapSize(float width, float height)
        {
            // --- Clamp designer input to playable minimum ---
            s_MapWidth = math.max(100f, width);
            s_MapHeight = math.max(100f, height);
        }

        /// <summary>[LEGACY name] Flat world — returns position unchanged (no wrap).</summary>
        public static float3 Wrap(float3 position, float mapWidth, float mapHeight) => position;

        /// <summary>Overload using cached map dimensions.</summary>
        public static float3 Wrap(float3 position) => position;

        /// <summary>XZ offset from → to on a flat plane (Y ignored).</summary>
        public static float3 ShortestOffsetXZ(float3 from, float3 to, float mapWidth, float mapHeight) =>
            new float3(to.x - from.x, 0f, to.z - from.z);

        /// <summary>Euclidean distance on the XZ plane between two world positions.</summary>
        public static float ToroidalDistance(float3 a, float3 b, float mapWidth, float mapHeight)
        {
            // --- Flat XZ Euclidean distance (Y ignored) ---
            float dx = b.x - a.x;
            float dz = b.z - a.z;
            return math.sqrt(dx * dx + dz * dz);
        }

        /// <summary>Normalized direction on XZ from one point toward another; default +Z if coincident.</summary>
        public static float3 ToroidalDirection(float3 from, float3 to, float mapWidth, float mapHeight)
        {
            // --- XZ direction; default forward if points coincide ---
            float3 offset = new float3(to.x - from.x, 0f, to.z - from.z);
            if (math.lengthsq(offset) < 0.0001f)
                return new float3(0f, 0f, 1f);
            return math.normalize(offset);
        }

        /// <summary>[LEGACY] Display position equals logical position on flat map.</summary>
        public static float3 GetDisplayPosition(float3 logicalPos, float3 referencePos, float mapWidth, float mapHeight) =>
            logicalPos;

        /// <summary>Overload using cached map size.</summary>
        public static float3 GetDisplayPosition(float3 logicalPos, float3 referencePos) => logicalPos;

        /// <summary>
        /// [LEGACY] Hysteresis tile indices unused on flat map — always returns logical position.
        /// </summary>
        public static float3 GetDisplayPositionWithHysteresis(
            float3 logicalPos,
            float3 referencePos,
            ref int tileK,
            ref int tileM,
            float mapWidth,
            float mapHeight,
            float switchMarginFraction = 0.35f)
        {
            // --- [LEGACY] Hysteresis tiles unused on flat map ---
            tileK = 0;
            tileM = 0;
            return logicalPos;
        }

        /// <summary>Overload using cached map dimensions.</summary>
        public static float3 GetDisplayPositionWithHysteresis(
            float3 logicalPos,
            float3 referencePos,
            ref int tileK,
            ref int tileM,
            float switchMarginFraction = 0.35f)
        {
            // --- [LEGACY] Same as flat display — parameters kept for API stability ---
            tileK = 0;
            tileM = 0;
            return logicalPos;
        }
    }
}
