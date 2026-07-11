using UnityEngine;

namespace TitanOrbit.Generation
{
    /// <summary>
    /// [LEGACY] Flat (non-wrapping) world helpers for legacy Vector3/minimap code. The toroidal map
    /// was removed; distance/direction/display are now plain Euclidean XZ. Map width/height are kept
    /// as world extent for minimap scaling only. New code should prefer
    /// <see cref="Shared.ToroidalMapEcs"/> in ECS paths. Set size via
    /// <see cref="MapGenerationSettings"/> at match start.
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

        /// <summary>[LEGACY] No tile offset — returns logical position unchanged.</summary>
        public static Vector3 GetDisplayPosition(Vector3 logicalPos, Vector3 cameraPos) => logicalPos;

        /// <summary>[LEGACY] Hysteresis stubs — always returns logical pos with tile indices zeroed.</summary>
        public static Vector3 GetDisplayPositionWithHysteresis(
            Vector3 logicalPos,
            Vector3 referencePos,
            ref int tileK,
            ref int tileM,
            float switchMarginFraction = 0.35f)
        {
            tileK = 0;
            tileM = 0;
            return logicalPos;
        }

        /// <summary>[LEGACY] No wrap — position unchanged.</summary>
        public static Vector3 WrapPosition(Vector3 position) => position;

        /// <summary>Plain XZ offset from A to B (Y ignored).</summary>
        public static Vector3 ShortestWorldOffsetXZ(Vector3 worldA, Vector3 worldB) =>
            new Vector3(worldB.x - worldA.x, 0f, worldB.z - worldA.z);

        /// <summary>Euclidean distance on the XZ plane.</summary>
        public static float ToroidalDistance(Vector3 a, Vector3 b)
        {
            // --- ToroidalDistance ---
            float dx = b.x - a.x;
            float dz = b.z - a.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>Normalized direction on XZ from <paramref name="from"/> toward <paramref name="to"/>.</summary>
        public static Vector3 ToroidalDirection(Vector3 from, Vector3 to)
        {
            // --- ToroidalDirection ---
            Vector3 d = new Vector3(to.x - from.x, 0f, to.z - from.z);
            if (d.sqrMagnitude < 0.0001f)
                return Vector3.forward;
            return d.normalized;
        }

        /// <summary>XZ offset as Vector2 for UI/minimap math.</summary>
        public static Vector2 ShortestOffsetXZ(Vector3 fromCanonical, Vector3 toCanonical) =>
            new Vector2(toCanonical.x - fromCanonical.x, toCanonical.z - fromCanonical.z);
    }
}
