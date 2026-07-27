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
    /// Map size is set from <see cref="TitanOrbit.ECS.MapStateSingleton"/> at match bootstrap
    /// or from <c>MapSessionMetaRpc</c> on dedicated clients — never invented as a silent default.
    /// Burst-safe: pure static math, no managed allocations.
    /// </summary>
    public static class ToroidalMapEcs
    {
        /// <summary>
        /// Smallest side length we treat as a real rolled map (world units).
        /// Below this, size is considered missing — callers must skip toroidal math, not invent a period.
        /// </summary>
        public const float MinValidMapSize = 100f;

        // [TITAN-ORBIT] 0 = unset. Never seed 1000×1000 — a wrong period looks fine near the
        // map center but breaks seam reach, tractor lock, gem pickup, and display retile.
        static float s_MapWidth;
        static float s_MapHeight;

        /// <summary>Current world width in Unity units (XZ plane). 0 until <see cref="SetMapSize"/>.</summary>
        public static float MapWidth => s_MapWidth;

        /// <summary>Current world height in Unity units (XZ plane). 0 until <see cref="SetMapSize"/>.</summary>
        public static float MapHeight => s_MapHeight;

        /// <summary>True when both axes look like a real rolled map (≥ <see cref="MinValidMapSize"/>).</summary>
        public static bool HasValidMapSize => IsValidMapSize(s_MapWidth, s_MapHeight);

        /// <summary>
        /// True when both dimensions look like a real rolled map (not missing / not inventing).
        /// </summary>
        public static bool IsValidMapSize(float width, float height) =>
            width >= MinValidMapSize && height >= MinValidMapSize;

        /// <summary>
        /// Called when map generation completes or session meta arrives.
        /// Ignores invalid sizes — does <b>not</b> invent a fallback period.
        /// </summary>
        /// <param name="width">Rolled map width in world units (≥ <see cref="MinValidMapSize"/>).</param>
        /// <param name="height">Rolled map height in world units (≥ <see cref="MinValidMapSize"/>).</param>
        public static void SetMapSize(float width, float height)
        {
            // --- Reject missing / garbage sizes (do not invent 1000 or 100) ---
            if (!IsValidMapSize(width, height))
                return;

            s_MapWidth = width;
            s_MapHeight = height;

            // --- Keep Vector3 twin in lockstep (minimap / UI) ---
            ToroidalMap.SetMapSize(s_MapWidth, s_MapHeight);
        }

        /// <summary>
        /// Clears cached size when leaving a match so the next join cannot reuse a stale period.
        /// </summary>
        public static void ClearMapSize()
        {
            s_MapWidth = 0f;
            s_MapHeight = 0f;
            ToroidalMap.ClearMapSize();
        }

        /// <summary>
        /// Reads the latched cache when valid.
        /// </summary>
        /// <param name="mapW">Width when true; otherwise 0.</param>
        /// <param name="mapH">Height when true; otherwise 0.</param>
        /// <returns>False when size has not been set yet — caller must skip toroidal work.</returns>
        public static bool TryGetMapSize(out float mapW, out float mapH)
        {
            if (!HasValidMapSize)
            {
                mapW = 0f;
                mapH = 0f;
                return false;
            }

            mapW = s_MapWidth;
            mapH = s_MapHeight;
            return true;
        }

        /// <summary>
        /// Picks the map period for toroidal gameplay / presentation math.
        /// Prefers an authoritative <paramref name="preferredWidth"/> / <paramref name="preferredHeight"/>
        /// (from <c>MapStateSingleton</c> or session meta) over the static cache.
        /// Returns false when neither source is valid — never invents 1000×1000.
        /// </summary>
        /// <param name="preferredWidth">
        /// Authoritative width when ≥ <see cref="MinValidMapSize"/>; otherwise ignored.
        /// </param>
        /// <param name="preferredHeight">
        /// Authoritative height when ≥ <see cref="MinValidMapSize"/>; otherwise ignored.
        /// </param>
        /// <param name="mapW">Resolved width when true; otherwise 0.</param>
        /// <param name="mapH">Resolved height when true; otherwise 0.</param>
        /// <returns>True when a real rolled period is available.</returns>
        public static bool ResolveMapSize(
            float preferredWidth,
            float preferredHeight,
            out float mapW,
            out float mapH)
        {
            // --- Prefer caller’s rolled size (MapState / session meta) ---
            if (IsValidMapSize(preferredWidth, preferredHeight))
            {
                mapW = preferredWidth;
                mapH = preferredHeight;
                return true;
            }

            // --- Fall back only to a previously latched real size ---
            return TryGetMapSize(out mapW, out mapH);
        }

        /// <summary>
        /// Wraps a world position into canonical toroidal space using explicit map dimensions.
        /// Valid range: X in <c>[-halfW, halfW)</c>, Z in <c>[-halfH, halfH)</c>. Y unchanged.
        /// [TITAN-ORBIT] Prefer this overload inside Burst systems that already read MapStateSingleton.
        /// Caller must pass a real rolled size (≥ <see cref="MinValidMapSize"/>).
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

        /// <summary>
        /// Wrap using cached <see cref="MapWidth"/> / <see cref="MapHeight"/>.
        /// No-op (returns input) when size is not latched yet.
        /// </summary>
        public static float3 Wrap(float3 position)
        {
            if (!HasValidMapSize)
                return position;
            return Wrap(position, s_MapWidth, s_MapHeight);
        }

        /// <summary>
        /// Shortest XZ offset from <paramref name="from"/> toward <paramref name="to"/> on the torus.
        /// Result length is at most half a map side on each axis. Y is zeroed.
        /// Caller must pass a real rolled size; tiny/zero sizes only get epsilon protection (no invented period).
        /// </summary>
        public static float3 ShortestOffsetXZ(float3 from, float3 to, float mapWidth, float mapHeight)
        {
            // --- Divide-by-zero guard only (do not invent a playable 1000 / 100 period) ---
            float w = math.max(1e-3f, mapWidth);
            float h = math.max(1e-3f, mapHeight);

            // --- Periodic delta: subtract nearest whole map tile ---
            // [TITAN-ORBIT] Same formula as minimap / GemTractorBeamVisual neighbor tiles.
            float dx = to.x - from.x;
            float dz = to.z - from.z;
            dx -= math.round(dx / w) * w;
            dz -= math.round(dz / h) * h;
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

        /// <summary>
        /// Overload using cached map size. Returns logical position unchanged when size is unset.
        /// </summary>
        public static float3 GetDisplayPosition(float3 logicalPos, float3 referencePos)
        {
            if (!HasValidMapSize)
                return logicalPos;
            return GetDisplayPosition(logicalPos, referencePos, s_MapWidth, s_MapHeight);
        }

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

        /// <summary>
        /// Overload using cached map dimensions. Returns logical position when size is unset.
        /// </summary>
        public static float3 GetDisplayPositionWithHysteresis(
            float3 logicalPos,
            float3 referencePos,
            ref int tileK,
            ref int tileM,
            float switchMarginFraction = 0.35f)
        {
            if (!HasValidMapSize)
                return logicalPos;
            return GetDisplayPositionWithHysteresis(
                logicalPos,
                referencePos,
                ref tileK,
                ref tileM,
                s_MapWidth,
                s_MapHeight,
                switchMarginFraction);
        }
    }
}
