using UnityEngine;

namespace TitanOrbit.Generation
{
    /// <summary>
    /// Handles toroidal map wrapping for all entities
    /// </summary>
    public static class ToroidalMap
    {
        private static float mapWidth = 1000f;
        private static float mapHeight = 1000f;

        public static void SetMapSize(float width, float height)
        {
            mapWidth = width;
            mapHeight = height;
        }

        public static float GetMapWidth() => mapWidth;
        public static float GetMapHeight() => mapHeight;

        /// <summary>
        /// Returns the toroidal copy of logicalPos that is closest to cameraPos.
        /// Supports the ship flying arbitrarily far (e.g. 100, 15000): we pick the copy in the same
        /// "tile" as the camera so planets/asteroids always reposition around the local ship.
        /// </summary>
        public static Vector3 GetDisplayPosition(Vector3 logicalPos, Vector3 cameraPos)
        {
            // Nearest copy = logical + (k*W, m*H) for integers k,m that minimize distance to camera.
            // k = round((camera.x - logical.x) / W), m = round((camera.z - logical.z) / H).
            float dx = cameraPos.x - logicalPos.x;
            float dz = cameraPos.z - logicalPos.z;
            int k = (int)Mathf.Round(dx / mapWidth);
            int m = (int)Mathf.Round(dz / mapHeight);
            float bestX = logicalPos.x + k * mapWidth;
            float bestZ = logicalPos.z + m * mapHeight;
            return new Vector3(bestX, logicalPos.y, bestZ);
        }

        /// <summary>
        /// Like <see cref="GetDisplayPosition"/> but keeps the same map tile until another tile is clearly closer.
        /// Prevents planets/moons from popping a full map width when the reference point hovers near a tile boundary.
        /// </summary>
        public static Vector3 GetDisplayPositionWithHysteresis(
            Vector3 logicalPos,
            Vector3 referencePos,
            ref int tileK,
            ref int tileM,
            float switchMarginFraction = 0.35f)
        {
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
        /// Wraps a position to the toroidal map. Uses modulo for consistent wrapping.
        /// Valid range: [-halfWidth, halfWidth) for X, [-halfHeight, halfHeight) for Z
        /// </summary>
        public static Vector3 WrapPosition(Vector3 position)
        {
            float halfWidth = mapWidth / 2f;
            float halfHeight = mapHeight / 2f;

            // Use modulo for consistent, seamless wrapping (handles any magnitude)
            position.x = Mathf.Repeat(position.x + halfWidth, mapWidth) - halfWidth;
            position.z = Mathf.Repeat(position.z + halfHeight, mapHeight) - halfHeight;

            return position;
        }

        /// <summary>
        /// Shortest XZ offset from <paramref name="worldA"/> to <paramref name="worldB"/> on the torus.
        /// Ships and other objects can sit many map tiles from each other in raw world space; this uses
        /// periodic wrapping so distance/direction match gameplay (same as <see cref="GetDisplayPosition"/>).
        /// </summary>
        public static Vector3 ShortestWorldOffsetXZ(Vector3 worldA, Vector3 worldB)
        {
            float dx = worldB.x - worldA.x;
            float dz = worldB.z - worldA.z;
            dx -= Mathf.Round(dx / mapWidth) * mapWidth;
            dz -= Mathf.Round(dz / mapHeight) * mapHeight;
            return new Vector3(dx, 0f, dz);
        }

        /// <summary>
        /// Gets the shortest distance between two points on a toroidal map (works for arbitrary world coordinates).
        /// </summary>
        public static float ToroidalDistance(Vector3 a, Vector3 b)
        {
            Vector3 d = ShortestWorldOffsetXZ(a, b);
            return Mathf.Sqrt(d.x * d.x + d.z * d.z);
        }

        /// <summary>
        /// Gets the shortest direction vector from <paramref name="from"/> toward <paramref name="to"/> on a toroidal map.
        /// </summary>
        public static Vector3 ToroidalDirection(Vector3 from, Vector3 to)
        {
            Vector3 d = ShortestWorldOffsetXZ(from, to);
            if (d.sqrMagnitude < 0.0001f)
                return Vector3.forward;
            return d.normalized;
        }

        /// <summary>
        /// Shortest signed offset from one canonical position to another in XZ (for toroidal point-in-triangle).
        /// Result x is in (-mapWidth/2, mapWidth/2], z in (-mapHeight/2, mapHeight/2].
        /// </summary>
        public static Vector2 ShortestOffsetXZ(Vector3 fromCanonical, Vector3 toCanonical)
        {
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
