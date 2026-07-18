using UnityEngine;

namespace TitanOrbit.Generation
{
    /// <summary>
    /// Vector3 / minimap twin of <see cref="ToroidalMapEcs"/>. Same Pac-Man wrap and shortest-path
    /// math for legacy GameObject and UI code that does not use Unity.Mathematics.
    /// Lives in TitanOrbit.Shared (with ToroidalMapEcs) so ECS bootstrap and NetCode RPCs can set size.
    /// Prefer <see cref="ToroidalMapEcs"/> in ECS systems.
    /// </summary>
    public static class ToroidalMap
    {
        /// <summary>Current map width in world units (set at match bootstrap).</summary>
        private static float mapWidth = 1000f;

        /// <summary>Current map height in world units (set at match bootstrap).</summary>
        private static float mapHeight = 1000f;

        /// <summary>
        /// Updates cached map dimensions after procedural generation rolls map size.
        /// </summary>
        public static void SetMapSize(float width, float height)
        {
            mapWidth = width;
            mapHeight = height;
        }

        /// <summary>Cached map width for minimap and legacy distance helpers.</summary>
        public static float GetMapWidth() => mapWidth;

        /// <summary>Cached map height for minimap and legacy distance helpers.</summary>
        public static float GetMapHeight() => mapHeight;

        /// <summary>
        /// Returns the toroidal copy of <paramref name="logicalPos"/> closest to
        /// <paramref name="cameraPos"/>. Supports the ship flying arbitrarily far from origin.
        /// </summary>
        public static Vector3 GetDisplayPosition(Vector3 logicalPos, Vector3 cameraPos)
        {
            // --- Same integer-tile formula as ToroidalMapEcs.GetDisplayPosition ---
            float dx = cameraPos.x - logicalPos.x;
            float dz = cameraPos.z - logicalPos.z;
            int k = (int)Mathf.Round(dx / mapWidth);
            int m = (int)Mathf.Round(dz / mapHeight);
            return new Vector3(logicalPos.x + k * mapWidth, logicalPos.y, logicalPos.z + m * mapHeight);
        }

        /// <summary>
        /// Like <see cref="GetDisplayPosition"/> but keeps the same map tile until another tile is
        /// clearly closer — avoids pops near tile boundaries. Initialize tiles with int.MinValue.
        /// </summary>
        public static Vector3 GetDisplayPositionWithHysteresis(
            Vector3 logicalPos,
            Vector3 referencePos,
            ref int tileK,
            ref int tileM,
            float switchMarginFraction = 0.35f)
        {
            // --- Candidate tile ---
            float dx = referencePos.x - logicalPos.x;
            float dz = referencePos.z - logicalPos.z;
            int candidateK = (int)Mathf.Round(dx / mapWidth);
            int candidateM = (int)Mathf.Round(dz / mapHeight);

            if (tileK == int.MinValue)
            {
                tileK = candidateK;
                tileM = candidateM;
            }
            else if (candidateK != tileK || candidateM != tileM)
            {
                // --- Hysteresis: switch only when clearly closer ---
                Vector3 current = new Vector3(
                    logicalPos.x + tileK * mapWidth,
                    logicalPos.y,
                    logicalPos.z + tileM * mapHeight);
                Vector3 candidate = new Vector3(
                    logicalPos.x + candidateK * mapWidth,
                    logicalPos.y,
                    logicalPos.z + candidateM * mapHeight);
                float currentDistSq = (referencePos.x - current.x) * (referencePos.x - current.x)
                    + (referencePos.z - current.z) * (referencePos.z - current.z);
                float candidateDistSq = (referencePos.x - candidate.x) * (referencePos.x - candidate.x)
                    + (referencePos.z - candidate.z) * (referencePos.z - candidate.z);
                float margin = Mathf.Max(1f, switchMarginFraction * Mathf.Min(mapWidth, mapHeight));
                if (candidateDistSq < currentDistSq - margin * margin)
                {
                    tileK = candidateK;
                    tileM = candidateM;
                }
            }

            return new Vector3(
                logicalPos.x + tileK * mapWidth,
                logicalPos.y,
                logicalPos.z + tileM * mapHeight);
        }

        /// <summary>
        /// Wraps a position into canonical toroidal space:
        /// X in <c>[-halfWidth, halfWidth)</c>, Z in <c>[-halfHeight, halfHeight)</c>.
        /// </summary>
        public static Vector3 WrapPosition(Vector3 position)
        {
            // --- [UNITY] Mathf.Repeat maps into [0, length); then recenter ---
            float halfWidth = mapWidth / 2f;
            float halfHeight = mapHeight / 2f;

            position.x = Mathf.Repeat(position.x + halfWidth, mapWidth) - halfWidth;
            position.z = Mathf.Repeat(position.z + halfHeight, mapHeight) - halfHeight;

            return position;
        }

        /// <summary>
        /// Shortest XZ offset from A to B on the torus (works for arbitrary world coordinates).
        /// </summary>
        public static Vector3 ShortestWorldOffsetXZ(Vector3 worldA, Vector3 worldB)
        {
            // --- Periodic delta ---
            float dx = worldB.x - worldA.x;
            float dz = worldB.z - worldA.z;
            dx -= Mathf.Round(dx / mapWidth) * mapWidth;
            dz -= Mathf.Round(dz / mapHeight) * mapHeight;
            return new Vector3(dx, 0f, dz);
        }

        /// <summary>Shortest distance between two points on the toroidal map (XZ only).</summary>
        public static float ToroidalDistance(Vector3 a, Vector3 b)
        {
            // --- ToroidalDistance ---
            Vector3 d = ShortestWorldOffsetXZ(a, b);
            return Mathf.Sqrt(d.x * d.x + d.z * d.z);
        }

        /// <summary>Normalized shortest direction from <paramref name="from"/> toward <paramref name="to"/>.</summary>
        public static Vector3 ToroidalDirection(Vector3 from, Vector3 to)
        {
            // --- ToroidalDirection ---
            Vector3 d = ShortestWorldOffsetXZ(from, to);
            if (d.sqrMagnitude < 0.0001f)
                return Vector3.forward;
            return d.normalized;
        }

        /// <summary>
        /// Shortest signed XZ offset as Vector2 for UI / point-in-triangle helpers.
        /// Result x in (-mapWidth/2, mapWidth/2], z in (-mapHeight/2, mapHeight/2].
        /// </summary>
        public static Vector2 ShortestOffsetXZ(Vector3 fromCanonical, Vector3 toCanonical)
        {
            // --- Clamp-style wrap (equivalent to round-subtract when |delta| is within one map) ---
            float dx = toCanonical.x - fromCanonical.x;
            float dz = toCanonical.z - fromCanonical.z;
            float halfW = mapWidth / 2f;
            float halfH = mapHeight / 2f;
            if (dx > halfW) dx -= mapWidth;
            else if (dx <= -halfW) dx += mapWidth;
            if (dz > halfH) dz -= mapHeight;
            else if (dz <= -halfH) dz += mapHeight;
            return new Vector2(dx, dz);
        }
    }
}
