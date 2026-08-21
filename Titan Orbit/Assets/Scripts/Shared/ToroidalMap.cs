using UnityEngine;

namespace TitanOrbit.Generation
{
    /// <summary>
    /// Compatibility facade over <see cref="SphericalMap"/>. Display helpers are identity.
    /// Distances are geodesics. Wrap projects onto the shell.
    /// </summary>
    public static class ToroidalMap
    {
        public static void SetMapSize(float width, float height) =>
            SphericalMap.SetMapSize(width, height);

        public static void ClearMapSize() => SphericalMap.ClearMapSize();

        public static bool HasValidMapSize => SphericalMap.HasValidMapSize;

        public static bool TryGetMapSize(out float width, out float height) =>
            SphericalMap.TryGetMapSize(out width, out height);

        public static float GetMapWidth() => SphericalMap.GetMapWidth();

        public static float GetMapHeight() => SphericalMap.GetMapHeight();

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

        public static Vector3 WrapPosition(Vector3 position) => SphericalMap.ProjectToSphere(position);

        public static Vector3 ShortestWorldOffsetXZ(Vector3 worldA, Vector3 worldB)
        {
            if (!SphericalMap.TryGetMapSize(out float w, out float h))
                return worldB - worldA;
            return SphericalMap.GeodesicOffset(worldA, worldB, w, h);
        }

        public static float ToroidalDistance(Vector3 a, Vector3 b)
        {
            if (!SphericalMap.TryGetMapSize(out float w, out float h))
                return Vector3.Distance(a, b);
            return SphericalMap.GeodesicDistance(a, b, w, h);
        }

        public static Vector3 ToroidalDirection(Vector3 from, Vector3 to)
        {
            if (!SphericalMap.TryGetMapSize(out float w, out float h))
            {
                Vector3 d = to - from;
                return d.sqrMagnitude < 0.0001f ? Vector3.forward : d.normalized;
            }

            return SphericalMap.GeodesicDirection(from, to, w, h);
        }

        public static Vector2 ShortestOffsetXZ(Vector3 fromCanonical, Vector3 toCanonical)
        {
            Vector3 off = ShortestWorldOffsetXZ(fromCanonical, toCanonical);
            return new Vector2(off.x, off.z);
        }
    }
}
