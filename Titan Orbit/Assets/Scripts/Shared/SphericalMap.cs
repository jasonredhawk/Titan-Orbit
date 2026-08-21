using UnityEngine;

namespace TitanOrbit.Generation
{
    /// <summary>
    /// Vector3 / UI twin of <see cref="SphericalMapEcs"/>. Same sphere-surface math for
    /// GameObject and UI code that does not use Unity.Mathematics.
    /// Size starts unset (0) — never invents a silent radius.
    /// </summary>
    public static class SphericalMap
    {
        static float s_MapSize;
        static float s_Radius;

        /// <summary>Designer linear map size (0 until set).</summary>
        public static float MapSize => s_MapSize;

        /// <summary>Playable sphere radius (0 until set).</summary>
        public static float Radius => s_Radius;

        /// <summary>True when a real rolled map is latched.</summary>
        public static bool HasValidMapSize => SphericalMapEcs.IsValidMapSize(s_MapSize);

        /// <summary>
        /// Latches designer size after generation / session meta. Ignores invalid sizes.
        /// </summary>
        public static void SetMapSize(float mapSize)
        {
            if (!SphericalMapEcs.IsValidMapSize(mapSize))
                return;

            s_MapSize = mapSize;
            s_Radius = SphericalMapEcs.RadiusFromMapSize(mapSize);
        }

        /// <summary>Square-map overload (uses the larger axis).</summary>
        public static void SetMapSize(float width, float height) =>
            SetMapSize(Mathf.Max(width, height));

        /// <summary>Clears cached size when leaving a match.</summary>
        public static void ClearMapSize()
        {
            s_MapSize = 0f;
            s_Radius = 0f;
        }

        /// <summary>Reads latched designer size when valid.</summary>
        public static bool TryGetMapSize(out float mapSize)
        {
            if (!HasValidMapSize)
            {
                mapSize = 0f;
                return false;
            }

            mapSize = s_MapSize;
            return true;
        }

        /// <summary>Reads latched designer size as a square pair.</summary>
        public static bool TryGetMapSize(out float width, out float height)
        {
            if (!TryGetMapSize(out float mapSize))
            {
                width = 0f;
                height = 0f;
                return false;
            }

            width = mapSize;
            height = mapSize;
            return true;
        }

        /// <summary>Reads latched radius when valid.</summary>
        public static bool TryGetRadius(out float radius)
        {
            if (!SphericalMapEcs.IsValidRadius(s_Radius))
            {
                radius = 0f;
                return false;
            }

            radius = s_Radius;
            return true;
        }

        /// <summary>Cached designer size (0 until set).</summary>
        public static float GetMapWidth() => s_MapSize;

        /// <summary>Cached designer size (square — same as width).</summary>
        public static float GetMapHeight() => s_MapSize;

        /// <summary>Cached radius (0 until set).</summary>
        public static float GetRadius() => s_Radius;

        /// <summary>
        /// Identity: one object, real world pose. Kept so leftover display callers compile.
        /// </summary>
        public static Vector3 GetDisplayPosition(Vector3 logicalPos, Vector3 _) => logicalPos;

        /// <summary>Identity with unused hysteresis refs.</summary>
        public static Vector3 GetDisplayPositionWithHysteresis(
            Vector3 logicalPos,
            Vector3 _,
            ref int tileK,
            ref int tileM,
            float switchMarginFraction = 0.35f)
        {
            tileK = 0;
            tileM = 0;
            return logicalPos;
        }

        /// <summary>Project onto the latched shell. Returns input when radius is unset.</summary>
        public static Vector3 ProjectToSphere(Vector3 position)
        {
            if (!TryGetRadius(out float radius))
                return position;
            return (Vector3)SphericalMapEcs.ProjectToSphere((Unity.Mathematics.float3)position, radius);
        }

        /// <summary>Great-circle distance. 0 when radius is unset.</summary>
        public static float GeodesicDistance(Vector3 a, Vector3 b)
        {
            if (!TryGetRadius(out float radius))
                return 0f;
            return SphericalMapEcs.GeodesicDistance((Unity.Mathematics.float3)a, (Unity.Mathematics.float3)b, radius);
        }

        /// <summary>Great-circle distance with an explicit designer size pair.</summary>
        public static float GeodesicDistance(Vector3 a, Vector3 b, float mapWidth, float mapHeight)
        {
            float radius = SphericalMapEcs.RadiusFromMapAxes(mapWidth, mapHeight);
            return SphericalMapEcs.GeodesicDistance((Unity.Mathematics.float3)a, (Unity.Mathematics.float3)b, radius);
        }

        /// <summary>Unit tangent at <paramref name="from"/> toward <paramref name="to"/>.</summary>
        public static Vector3 GeodesicDirection(Vector3 from, Vector3 to, float mapWidth, float mapHeight)
        {
            float radius = SphericalMapEcs.RadiusFromMapAxes(mapWidth, mapHeight);
            return (Vector3)SphericalMapEcs.GeodesicDirection(
                (Unity.Mathematics.float3)from, (Unity.Mathematics.float3)to, radius);
        }

        /// <summary>Tangent offset of geodesic length.</summary>
        public static Vector3 GeodesicOffset(Vector3 from, Vector3 to, float mapWidth, float mapHeight)
        {
            float radius = SphericalMapEcs.RadiusFromMapAxes(mapWidth, mapHeight);
            return (Vector3)SphericalMapEcs.GeodesicOffset(
                (Unity.Mathematics.float3)from, (Unity.Mathematics.float3)to, radius);
        }

        /// <summary>
        /// Billboard facing the gameplay camera: same “read from above” as the old Euler(-90) plates.
        /// </summary>
        public static Quaternion BillboardFacingCamera(Camera cam) =>
            BillboardFacingCamera(cam, cam != null ? cam.transform.position : Vector3.zero);

        /// <summary>
        /// World-space text/plate: local +Z points at the camera, local +Y matches camera up.
        /// Callers must use a positive Y scale (the old Euler(-90) + negative-Y flip is gone).
        /// </summary>
        public static Quaternion BillboardFacingCamera(Camera cam, Vector3 worldPos)
        {
            if (cam == null)
                return Quaternion.identity;
            Vector3 toCam = cam.transform.position - worldPos;
            if (toCam.sqrMagnitude < 1e-8f)
                toCam = -cam.transform.forward;
            return Quaternion.LookRotation(toCam.normalized, cam.transform.up);
        }

        /// <summary>Local +Y = radial so a planet/moon sits on the shell facing the camera.</summary>
        public static Quaternion SurfaceSitRotation(Vector3 position) =>
            (Quaternion)SphericalMapEcs.SurfaceSitRotation((Unity.Mathematics.float3)position);

        /// <summary>
        /// Mouse/camera ray ∩ tangent plane at <paramref name="planePoint"/> (local up = radial).
        /// Unlike <see cref="TryRaycastNear"/>, the hit can sit far past the visual pole —
        /// sphere-surface hits collapse to a few units when the camera looks along -radial.
        /// </summary>
        public static bool TryRaycastTangentPlane(
            Vector3 origin, Vector3 direction, Vector3 planePoint, out Vector3 hit)
        {
            hit = default;
            Vector3 n = planePoint.sqrMagnitude > 1e-6f ? planePoint.normalized : Vector3.up;
            float denom = Vector3.Dot(direction, n);
            if (Mathf.Abs(denom) < 1e-8f)
                return false;
            float t = Vector3.Dot(planePoint - origin, n) / denom;
            if (t < 0f)
                return false;
            hit = origin + direction * t;
            return float.IsFinite(hit.x) && float.IsFinite(hit.y) && float.IsFinite(hit.z);
        }

        /// <summary>Near-side ray hit on the latched shell. False when radius is unset or the ray misses.</summary>
        public static bool TryRaycastNear(Vector3 origin, Vector3 direction, out Vector3 hit)
        {
            hit = default;
            if (!TryGetRadius(out float radius))
                return false;
            if (!SphericalMapEcs.TryRaycastNear(
                    (Unity.Mathematics.float3)origin,
                    (Unity.Mathematics.float3)direction,
                    radius,
                    out var h))
                return false;
            hit = h;
            return true;
        }

        public static Vector2 ShortestOffsetXZ(Vector2 from, Vector2 to)
        {
            if (!TryGetRadius(out float radius))
                return to - from;

            var a = new Unity.Mathematics.float3(from.x, 0f, from.y);
            var b = new Unity.Mathematics.float3(to.x, 0f, to.y);
            var off = SphericalMapEcs.GeodesicOffset(a, b, radius);
            return new Vector2(off.x, off.z);
        }
    }
}
