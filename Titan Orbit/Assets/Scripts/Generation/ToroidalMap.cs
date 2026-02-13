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
        /// Use this for display position so the entity is always visible when the camera is near edges.
        /// </summary>
        public static Vector3 GetDisplayPosition(Vector3 logicalPos, Vector3 cameraPos)
        {
            float halfW = mapWidth / 2f;
            float halfH = mapHeight / 2f;
            float bestX = logicalPos.x;
            float bestZ = logicalPos.z;
            float bestDistSq = float.MaxValue;

            for (int k = -1; k <= 1; k++)
            {
                for (int m = -1; m <= 1; m++)
                {
                    float x = logicalPos.x + k * mapWidth;
                    float z = logicalPos.z + m * mapHeight;
                    float dx = x - cameraPos.x;
                    float dz = z - cameraPos.z;
                    float distSq = dx * dx + dz * dz;
                    if (distSq < bestDistSq)
                    {
                        bestDistSq = distSq;
                        bestX = x;
                        bestZ = z;
                    }
                }
            }

            return new Vector3(bestX, logicalPos.y, bestZ);
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
        /// Gets the shortest distance between two points on a toroidal map
        /// </summary>
        public static float ToroidalDistance(Vector3 a, Vector3 b)
        {
            float dx = Mathf.Abs(b.x - a.x);
            float dz = Mathf.Abs(b.z - a.z);

            // Wrap distances
            if (dx > mapWidth / 2f)
            {
                dx = mapWidth - dx;
            }

            if (dz > mapHeight / 2f)
            {
                dz = mapHeight - dz;
            }

            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>
        /// Gets the shortest direction vector between two points on a toroidal map
        /// </summary>
        public static Vector3 ToroidalDirection(Vector3 from, Vector3 to)
        {
            Vector3 direction = to - from;

            // Wrap direction
            if (direction.x > mapWidth / 2f)
            {
                direction.x -= mapWidth;
            }
            else if (direction.x < -mapWidth / 2f)
            {
                direction.x += mapWidth;
            }

            if (direction.z > mapHeight / 2f)
            {
                direction.z -= mapHeight;
            }
            else if (direction.z < -mapHeight / 2f)
            {
                direction.z += mapHeight;
            }

            return direction.normalized;
        }
    }
}
