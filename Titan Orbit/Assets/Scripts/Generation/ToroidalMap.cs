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
