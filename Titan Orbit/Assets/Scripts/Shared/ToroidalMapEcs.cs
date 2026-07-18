using Unity.Mathematics;

namespace TitanOrbit.Generation
{
    /// <summary>
    /// Pac-Man (toroidal) XZ map math for ECS simulation and presentation.
    /// Logical positions live in a centered rectangle
    /// <c>[-MapWidth/2, MapWidth/2) × [-MapHeight/2, MapHeight/2)</c>; flying off one edge
    /// wraps to the opposite edge. Distance and direction use the shortest path on that torus
    /// so combat, docking, mining, and beams work across seams. Display helpers pick the
    /// nearest map-tile copy of a logical point relative to a reference (usually the local ship)
    /// so GameObject proxies do not jump a full map width when the owner wraps.
    /// Map size is set from <see cref="TitanOrbit.ECS.MapStateSingleton"/> at match bootstrap.
    /// Burst-safe: pure static math, no managed allocations.
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

            // --- Keep Vector3 twin in lockstep (minimap / UI) ---
            ToroidalMap.SetMapSize(s_MapWidth, s_MapHeight);
        }

        /// <summary>
        /// Wraps a world position into canonical toroidal space using explicit map dimensions.
        /// Valid range: X in <c>[-halfW, halfW)</c>, Z in <c>[-halfH, halfH)</c>. Y unchanged.
        /// [TITAN-ORBIT] Prefer this overload inside Burst systems that already read MapStateSingleton.
        /// </summary>
        public static float3 Wrap(float3 position, float mapWidth, float mapHeight)
        {
            // --- Shift into [0, size), modulo, shift back to centered [-half, half) ---
            // [STANDARD] fmod can return negative for negative inputs; we re-add size when needed.
            float halfW = mapWidth * 0.5f;
            float halfH = mapHeight * 0.5f;

            position.x = math.fmod(position.x + halfW, mapWidth);
            if (position.x < 0f)
                position.x += mapWidth;
            position.x -= halfW;

            position.z = math.fmod(position.z + halfH, mapHeight);
            if (position.z < 0f)
                position.z += mapHeight;
            position.z -= halfH;

            return position;
        }

        /// <summary>Wrap using cached <see cref="MapWidth"/> / <see cref="MapHeight"/>.</summary>
        public static float3 Wrap(float3 position) => Wrap(position, s_MapWidth, s_MapHeight);

        /// <summary>
        /// Shortest XZ offset from <paramref name="from"/> toward <paramref name="to"/> on the torus.
        /// Result length is at most half a map side on each axis. Y is zeroed.
        /// </summary>
        public static float3 ShortestOffsetXZ(float3 from, float3 to, float mapWidth, float mapHeight)
        {
            // --- Periodic delta: subtract nearest whole map tile ---
            // [TITAN-ORBIT] Same formula as minimap / GemTractorBeamVisual neighbor tiles.
            float dx = to.x - from.x;
            float dz = to.z - from.z;
            dx -= math.round(dx / mapWidth) * mapWidth;
            dz -= math.round(dz / mapHeight) * mapHeight;
            return new float3(dx, 0f, dz);
        }

        /// <summary>Shortest distance on the XZ torus between two world positions (Y ignored).</summary>
        public static float ToroidalDistance(float3 a, float3 b, float mapWidth, float mapHeight)
        {
            // --- Length of shortest offset ---
            float3 d = ShortestOffsetXZ(a, b, mapWidth, mapHeight);
            return math.length(new float2(d.x, d.z));
        }

        /// <summary>
        /// Normalized XZ direction from <paramref name="from"/> toward <paramref name="to"/> along the
        /// shortest toroidal path. Returns +Z if the points coincide.
        /// </summary>
        public static float3 ToroidalDirection(float3 from, float3 to, float mapWidth, float mapHeight)
        {
            // --- Normalize shortest offset; default forward if zero ---
            float3 offset = ShortestOffsetXZ(from, to, mapWidth, mapHeight);
            if (math.lengthsq(offset) < 0.0001f)
                return new float3(0f, 0f, 1f);
            return math.normalize(offset);
        }

        /// <summary>
        /// Nearest toroidal copy of <paramref name="logicalPos"/> relative to <paramref name="referencePos"/>.
        /// Uses integer tile indices so the local ship may fly arbitrarily far (many map widths);
        /// each body independently picks the copy nearest that ship.
        /// </summary>
        public static float3 GetDisplayPosition(float3 logicalPos, float3 referencePos, float mapWidth, float mapHeight)
        {
            // --- k,m = how many map tiles to shift logical so it sits near the (possibly unbounded) ship ---
            float dx = referencePos.x - logicalPos.x;
            float dz = referencePos.z - logicalPos.z;
            int k = (int)math.round(dx / mapWidth);
            int m = (int)math.round(dz / mapHeight);
            return new float3(logicalPos.x + k * mapWidth, logicalPos.y, logicalPos.z + m * mapHeight);
        }

        /// <summary>Overload using cached map size.</summary>
        public static float3 GetDisplayPosition(float3 logicalPos, float3 referencePos) =>
            GetDisplayPosition(logicalPos, referencePos, s_MapWidth, s_MapHeight);

        /// <summary>
        /// Like <see cref="GetDisplayPosition"/> but keeps the same map tile until another tile is
        /// clearly closer. Prevents planets/moons from popping a full map width when the reference
        /// hovers near a tile boundary. Pass <c>tileK/tileM = int.MinValue</c> to initialize.
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
            // --- Candidate tile from continuous nearest-copy ---
            float dx = referencePos.x - logicalPos.x;
            float dz = referencePos.z - logicalPos.z;
            int candidateK = (int)math.round(dx / mapWidth);
            int candidateM = (int)math.round(dz / mapHeight);

            // --- First call: latch candidate ---
            if (tileK == int.MinValue)
            {
                tileK = candidateK;
                tileM = candidateM;
            }
            else if (candidateK != tileK || candidateM != tileM)
            {
                // --- Only switch when candidate is clearly closer (margin in world units) ---
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

        /// <summary>Overload using cached map dimensions.</summary>
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
