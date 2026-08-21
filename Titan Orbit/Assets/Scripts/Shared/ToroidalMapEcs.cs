using Unity.Mathematics;

namespace TitanOrbit.Generation
{
    /// <summary>
    /// Compatibility facade over <see cref="SphericalMapEcs"/>. The playable map is a sphere;
    /// these names remain so existing callers compile while they migrate.
    /// <c>mapWidth</c>/<c>mapHeight</c> are the designer linear size (square). Distances are
    /// geodesics. <see cref="Wrap"/> projects onto the shell. Display helpers are identity —
    /// one object, real world pose.
    /// </summary>
    public static class ToroidalMapEcs
    {
        public const float MinValidMapSize = SphericalMapEcs.MinValidMapSize;

        public static float MapWidth => SphericalMapEcs.MapSize;

        public static float MapHeight => SphericalMapEcs.MapSize;

        public static bool HasValidMapSize => SphericalMapEcs.HasValidMapSize;

        public static bool IsValidMapSize(float width, float height) =>
            SphericalMapEcs.IsValidMapSize(width, height);

        public static void SetMapSize(float width, float height)
        {
            SphericalMapEcs.SetMapSize(width, height);
            ToroidalMap.SetMapSize(width, height);
        }

        public static void ClearMapSize()
        {
            SphericalMapEcs.ClearMapSize();
            ToroidalMap.ClearMapSize();
        }

        public static bool TryGetMapSize(out float mapW, out float mapH) =>
            SphericalMapEcs.TryGetMapSize(out mapW, out mapH);

        public static bool ResolveMapSize(
            float preferredWidth,
            float preferredHeight,
            out float mapW,
            out float mapH) =>
            SphericalMapEcs.ResolveMapSize(preferredWidth, preferredHeight, out mapW, out mapH);

        /// <summary>Project onto the sphere shell (replaces XZ wrap).</summary>
        public static float3 Wrap(float3 position, float mapWidth, float mapHeight)
        {
            float radius = SphericalMapEcs.RadiusFromMapAxes(mapWidth, mapHeight);
            return SphericalMapEcs.ProjectToSphere(position, radius);
        }

        public static float3 Wrap(float3 position) => SphericalMapEcs.ProjectToSphere(position);

        /// <summary>Tangent geodesic offset (3D — Y is not forced to 0).</summary>
        public static float3 ShortestOffsetXZ(float3 from, float3 to, float mapWidth, float mapHeight)
        {
            float radius = SphericalMapEcs.RadiusFromMapAxes(mapWidth, mapHeight);
            return SphericalMapEcs.GeodesicOffset(from, to, radius);
        }

        public static float ToroidalDistance(float3 a, float3 b, float mapWidth, float mapHeight)
        {
            float radius = SphericalMapEcs.RadiusFromMapAxes(mapWidth, mapHeight);
            return SphericalMapEcs.GeodesicDistance(a, b, radius);
        }

        public static float3 ToroidalDirection(float3 from, float3 to, float mapWidth, float mapHeight)
        {
            float radius = SphericalMapEcs.RadiusFromMapAxes(mapWidth, mapHeight);
            return SphericalMapEcs.GeodesicDirection(from, to, radius);
        }

        /// <summary>Identity — one object, real world pose. No display tiles.</summary>
        public static float3 GetDisplayPosition(float3 logicalPos, float3 referencePos, float mapWidth, float mapHeight) =>
            logicalPos;

        public static float3 GetDisplayPosition(float3 logicalPos, float3 referencePos) => logicalPos;

        public static float3 GetDisplayPositionWithHysteresis(
            float3 logicalPos,
            float3 referencePos,
            ref int tileK,
            ref int tileM,
            float mapWidth,
            float mapHeight,
            float switchMarginFraction = 0.35f)
        {
            tileK = 0;
            tileM = 0;
            return logicalPos;
        }

        public static float3 GetDisplayPositionWithHysteresis(
            float3 logicalPos,
            float3 referencePos,
            ref int tileK,
            ref int tileM,
            float switchMarginFraction = 0.35f)
        {
            tileK = 0;
            tileM = 0;
            return logicalPos;
        }
    }
}
