using UnityEngine;

namespace TitanOrbit.Generation
{
    /// <summary>
    /// Flat (non-wrapping) world helpers for legacy Vector3/minimap code. The toroidal map was removed;
    /// distance/direction/display are now plain Euclidean. Map width/height are kept as world extent
    /// for minimap scaling only.
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

        public static Vector3 GetDisplayPosition(Vector3 logicalPos, Vector3 cameraPos) => logicalPos;

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

        public static Vector3 WrapPosition(Vector3 position) => position;

        public static Vector3 ShortestWorldOffsetXZ(Vector3 worldA, Vector3 worldB) =>
            new Vector3(worldB.x - worldA.x, 0f, worldB.z - worldA.z);

        public static float ToroidalDistance(Vector3 a, Vector3 b)
        {
            float dx = b.x - a.x;
            float dz = b.z - a.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        public static Vector3 ToroidalDirection(Vector3 from, Vector3 to)
        {
            Vector3 d = new Vector3(to.x - from.x, 0f, to.z - from.z);
            if (d.sqrMagnitude < 0.0001f)
                return Vector3.forward;
            return d.normalized;
        }

        public static Vector2 ShortestOffsetXZ(Vector3 fromCanonical, Vector3 toCanonical) =>
            new Vector2(toCanonical.x - fromCanonical.x, toCanonical.z - fromCanonical.z);
    }
}
