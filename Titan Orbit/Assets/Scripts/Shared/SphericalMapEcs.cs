using Unity.Mathematics;

namespace TitanOrbit.Generation
{
    /// <summary>
    /// Sphere-surface map math for ECS simulation and presentation.
    /// The playable world is the outside of a sphere at the origin. Designer
    /// <c>MapSize</c> is the old square's linear extent; radius is chosen so
    /// surface area matches that square: <c>R = MapSize / (2 * sqrt(pi))</c>.
    /// Distances are geodesics. Movers stay on the shell via <see cref="ProjectToSphere"/>.
    /// There is no wrap, no display tile, and no second copy of any body.
    /// Size is set from <see cref="TitanOrbit.ECS.MapStateSingleton"/> at match bootstrap
    /// or from <c>MapSessionMetaRpc</c> — never invented as a silent default.
    /// Burst-safe: pure static math, no managed allocations.
    /// </summary>
    public static class SphericalMapEcs
    {
        /// <summary>
        /// Smallest designer map size we treat as a real rolled match (world units).
        /// Below this, size is missing — callers must skip spherical work, not invent a radius.
        /// </summary>
        public const float MinValidMapSize = 100f;

        /// <summary>
        /// Smallest radius derived from <see cref="MinValidMapSize"/> (plus slack).
        /// </summary>
        public const float MinValidRadius = 20f;

        // [TITAN-ORBIT] 0 = unset. Managed cache only — Burst jobs must use
        // <see cref="RadiusFromMapAxes"/> or <see cref="BurstSafeRadius"/> (BC1040).
        static float s_MapSize;
        static float s_Radius;

        /// <summary>Designer linear map size (old square side). 0 until <see cref="SetMapSize(float)"/>.</summary>
        public static float MapSize => s_MapSize;

        /// <summary>Playable sphere radius in world units. 0 until size is latched.</summary>
        public static float Radius => s_Radius;

        /// <summary>Sphere center (world origin).</summary>
        public static float3 Center => float3.zero;

        /// <summary>True when a real rolled map is latched.</summary>
        public static bool HasValidMapSize => IsValidMapSize(s_MapSize);

        /// <summary>True when <paramref name="mapSize"/> looks like a real rolled match.</summary>
        public static bool IsValidMapSize(float mapSize) => mapSize >= MinValidMapSize;

        /// <summary>True when both designer axes look like a real rolled square.</summary>
        public static bool IsValidMapSize(float width, float height) =>
            IsValidMapSize(math.max(width, height));

        /// <summary>True when <paramref name="radius"/> is a usable shell radius.</summary>
        public static bool IsValidRadius(float radius) => radius >= MinValidRadius;

        /// <summary>
        /// Surface-area match: old square <c>MapSize²</c> equals <c>4πR²</c>.
        /// </summary>
        public static float RadiusFromMapSize(float mapSize) =>
            mapSize / (2f * math.sqrt(math.PI));

        /// <summary>
        /// Designer size from a radius (inverse of <see cref="RadiusFromMapSize"/>).
        /// </summary>
        public static float MapSizeFromRadius(float radius) =>
            radius * 2f * math.sqrt(math.PI);

        /// <summary>
        /// Latches designer size and derived radius. Ignores invalid sizes — does not invent a fallback.
        /// </summary>
        public static void SetMapSize(float mapSize)
        {
            if (!IsValidMapSize(mapSize))
                return;

            s_MapSize = mapSize;
            s_Radius = RadiusFromMapSize(mapSize);
            SphericalMap.SetMapSize(s_MapSize);
        }

        /// <summary>
        /// Square-map overload used while session meta still carries width/height.
        /// Uses the larger axis (maps are rolled square).
        /// </summary>
        public static void SetMapSize(float width, float height) =>
            SetMapSize(math.max(width, height));

        /// <summary>
        /// Latches an explicit radius when the server already computed it.
        /// Still requires a valid designer size so UI/session meta stay consistent.
        /// </summary>
        public static void SetMapSizeAndRadius(float mapSize, float radius)
        {
            if (!IsValidMapSize(mapSize) || !IsValidRadius(radius))
                return;

            s_MapSize = mapSize;
            s_Radius = radius;
            SphericalMap.SetMapSize(s_MapSize);
        }

        /// <summary>Clears cached size when leaving a match.</summary>
        public static void ClearMapSize()
        {
            s_MapSize = 0f;
            s_Radius = 0f;
            SphericalMap.ClearMapSize();
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

        /// <summary>Reads latched designer size as a square width/height pair.</summary>
        public static bool TryGetMapSize(out float mapW, out float mapH)
        {
            if (!TryGetMapSize(out float mapSize))
            {
                mapW = 0f;
                mapH = 0f;
                return false;
            }

            mapW = mapSize;
            mapH = mapSize;
            return true;
        }

        /// <summary>Reads latched radius when valid.</summary>
        public static bool TryGetRadius(out float radius)
        {
            if (!IsValidRadius(s_Radius))
            {
                radius = 0f;
                return false;
            }

            radius = s_Radius;
            return true;
        }

        /// <summary>
        /// Prefers an authoritative size (MapState / session meta) over the static cache.
        /// Never invents a default.
        /// </summary>
        public static bool ResolveMapSize(
            float preferredWidth,
            float preferredHeight,
            out float mapW,
            out float mapH)
        {
            float preferred = math.max(preferredWidth, preferredHeight);
            if (IsValidMapSize(preferred))
            {
                mapW = preferred;
                mapH = preferred;
                return true;
            }

            return TryGetMapSize(out mapW, out mapH);
        }

        /// <summary>
        /// Prefers an authoritative radius, else derives from preferred designer size, else cache.
        /// </summary>
        public static bool ResolveRadius(float preferredRadius, float preferredMapSize, out float radius)
        {
            if (IsValidRadius(preferredRadius))
            {
                radius = preferredRadius;
                return true;
            }

            if (IsValidMapSize(preferredMapSize))
            {
                radius = RadiusFromMapSize(preferredMapSize);
                return true;
            }

            return TryGetRadius(out radius);
        }

        /// <summary>
        /// Unit radial (away from center). Defaults to +Y if <paramref name="position"/> is at the origin.
        /// </summary>
        public static float3 LocalUp(float3 position)
        {
            float lenSq = math.lengthsq(position);
            if (lenSq < 1e-10f)
                return new float3(0f, 1f, 0f);
            return position * math.rsqrt(lenSq);
        }

        /// <summary>
        /// Projects onto the shell of <paramref name="radius"/>. Origin maps to the north pole.
        /// </summary>
        public static float3 ProjectToSphere(float3 position, float radius)
        {
            float r = math.max(1e-3f, radius);
            float len = math.length(position);
            if (len < 1e-6f)
                return new float3(0f, r, 0f);
            return position * (r / len);
        }

        /// <summary>Project using the latched radius. Returns input when radius is unset.</summary>
        public static float3 ProjectToSphere(float3 position)
        {
            if (!TryGetRadius(out float radius))
                return position;
            return ProjectToSphere(position, radius);
        }

        /// <summary>
        /// Removes the radial component of <paramref name="vector"/> at <paramref name="position"/>.
        /// </summary>
        public static float3 FlattenToTangent(float3 vector, float3 position)
        {
            float3 up = LocalUp(position);
            return vector - up * math.dot(vector, up);
        }

        /// <summary>
        /// Unit tangent direction at <paramref name="position"/>. Never zeros world Y.
        /// Falls back to a stable meridian if <paramref name="direction"/> is radial.
        /// </summary>
        public static float3 UnitTangent(float3 position, float3 direction)
        {
            float3 d = FlattenToTangent(direction, position);
            if (math.lengthsq(d) < 1e-8f)
                return OrthonormalTangent(LocalUp(position));
            return math.normalize(d);
        }

        /// <summary>
        /// Great-circle distance on a sphere of <paramref name="radius"/>.
        /// </summary>
        public static float GeodesicDistance(float3 a, float3 b, float radius)
        {
            float3 na = LocalUp(a);
            float3 nb = LocalUp(b);
            float d = math.clamp(math.dot(na, nb), -1f, 1f);
            return math.max(1e-3f, radius) * math.acos(d);
        }

        /// <summary>Geodesic distance using latched radius (0 when unset).</summary>
        public static float GeodesicDistance(float3 a, float3 b)
        {
            if (!TryGetRadius(out float radius))
                return 0f;
            return GeodesicDistance(a, b, radius);
        }

        /// <summary>
        /// Unit tangent at <paramref name="from"/> along the short geodesic toward <paramref name="to"/>.
        /// Returns a stable tangent if the points coincide or are antipodal.
        /// </summary>
        public static float3 GeodesicDirection(float3 from, float3 to, float radius)
        {
            float3 n = LocalUp(from);
            float3 toTo = to - from;
            float3 tangent = FlattenToTangent(toTo, from);
            if (math.lengthsq(tangent) < 1e-10f)
            {
                // Antipodal: use the plane spanned by from and an arbitrary axis.
                float3 nb = LocalUp(to);
                float3 axis = math.cross(n, nb);
                if (math.lengthsq(axis) < 1e-10f)
                    return OrthonormalTangent(n);
                return math.normalize(math.cross(axis, n));
            }

            return math.normalize(tangent);
        }

        /// <summary>
        /// Tangent offset of geodesic length from <paramref name="from"/> toward <paramref name="to"/>.
        /// Adding this to <paramref name="from"/> is a first-order step; re-project for a surface point.
        /// </summary>
        public static float3 GeodesicOffset(float3 from, float3 to, float radius)
        {
            return GeodesicDirection(from, to, radius) * GeodesicDistance(from, to, radius);
        }

        /// <summary>
        /// Point <paramref name="distance"/> along the geodesic from <paramref name="from"/> toward
        /// <paramref name="to"/>, re-projected onto the shell.
        /// </summary>
        public static float3 SurfacePointToward(float3 from, float3 to, float distance, float radius)
        {
            float3 dir = GeodesicDirection(from, to, radius);
            return ProjectToSphere(from + dir * distance, radius);
        }

        /// <summary>
        /// Advance <paramref name="from"/> along a tangent velocity for <paramref name="dt"/>,
        /// then re-project. Also returns the tangent velocity at the new point (parallel-transported).
        /// </summary>
        public static void StepOnSphere(
            float3 from,
            float3 velocity,
            float dt,
            float radius,
            out float3 to,
            out float3 velocityOnShell)
        {
            float r = math.max(1e-3f, radius);
            float3 fromOn = ProjectToSphere(from, r);
            float3 tangentVel = FlattenToTangent(velocity, fromOn);
            float speed = math.length(tangentVel);
            float arc = speed * dt;
            if (arc < 1e-8f)
            {
                to = fromOn;
                velocityOnShell = tangentVel;
                return;
            }

            float3 up = LocalUp(fromOn);
            float3 dir = tangentVel * (1f / speed);
            float3 axis = math.cross(up, dir);
            if (math.lengthsq(axis) < 1e-12f)
            {
                to = fromOn;
                velocityOnShell = tangentVel;
                return;
            }

            axis = math.normalize(axis);
            float angle = arc / r;
            float s = math.sin(angle);
            float c = math.cos(angle);
            // Rodrigues: rotate the shell point and parallel-transport velocity.
            to = ProjectToSphere(
                fromOn * c + math.cross(axis, fromOn) * s + axis * math.dot(axis, fromOn) * (1f - c),
                r);
            float3 transported = tangentVel * c
                + math.cross(axis, tangentVel) * s
                + axis * math.dot(axis, tangentVel) * (1f - c);
            float3 newTangent = FlattenToTangent(transported, to);
            if (math.lengthsq(newTangent) < 1e-10f)
                velocityOnShell = float3.zero;
            else
                velocityOnShell = math.normalize(newTangent) * speed;
        }

        /// <summary>
        /// Mario Galaxy pose walk: one rotation moves the shell point, parallel-transports
        /// velocity, and carries the ship's quaternion. Local +Y is then snapped to the new
        /// radial. Poles are not a special case — there is no world-Y heading rebuild.
        /// </summary>
        public static void TransportPose(
            float3 fromPos,
            quaternion fromRot,
            float3 velocity,
            float dt,
            float radius,
            out float3 toPos,
            out quaternion toRot,
            out float3 velocityOnShell)
        {
            float r = math.max(1e-3f, radius);
            float3 p0 = ProjectToSphere(fromPos, r);
            float3 up0 = LocalUp(p0);
            float3 tan = FlattenToTangent(velocity, p0);
            float speed = math.length(tan);
            float arc = speed * dt;

            quaternion transport = quaternion.identity;
            if (arc >= 1e-8f)
            {
                float3 dir = tan * (1f / speed);
                float3 axis = math.cross(up0, dir);
                if (math.lengthsq(axis) >= 1e-12f)
                    transport = quaternion.AxisAngle(math.normalize(axis), arc / r);
            }

            toPos = ProjectToSphere(math.mul(transport, p0), r);
            float3 up1 = LocalUp(toPos);
            toRot = AlignLocalUp(math.mul(transport, fromRot), up1);

            float3 transportedVel = math.mul(transport, tan);
            float3 newTangent = FlattenToTangent(transportedVel, toPos);
            if (math.lengthsq(newTangent) < 1e-10f)
                velocityOnShell = float3.zero;
            else
                velocityOnShell = math.normalize(newTangent) * speed;
        }

        /// <summary>
        /// Shortest rotation that takes <paramref name="from"/> onto <paramref name="to"/>.
        /// </summary>
        public static quaternion FromToRotation(float3 from, float3 to)
        {
            float3 a = math.normalizesafe(from, new float3(0f, 1f, 0f));
            float3 b = math.normalizesafe(to, a);
            float d = math.dot(a, b);
            if (d > 0.999999f)
                return quaternion.identity;
            if (d < -0.999999f)
                return quaternion.AxisAngle(OrthonormalTangent(a), math.PI);
            return quaternion.AxisAngle(
                math.normalize(math.cross(a, b)),
                math.acos(math.clamp(d, -1f, 1f)));
        }

        /// <summary>Rotates <paramref name="rotation"/> so local +Y matches <paramref name="up"/>.</summary>
        public static quaternion AlignLocalUp(quaternion rotation, float3 up)
        {
            float3 localUp = math.mul(rotation, new float3(0f, 1f, 0f));
            return math.mul(FromToRotation(localUp, up), rotation);
        }

        /// <summary>
        /// Yaw around local (radial) up toward a tangent aim. Does not rebuild a world basis.
        /// </summary>
        public static quaternion YawTowardOnSurface(
            float3 position,
            quaternion rotation,
            float3 desiredTangent,
            float maxRadians)
        {
            float3 up = LocalUp(position);
            quaternion aligned = AlignLocalUp(rotation, up);
            float3 current = FlattenToTangent(math.mul(aligned, new float3(0f, 0f, 1f)), position);
            float3 desired = FlattenToTangent(desiredTangent, position);
            if (math.lengthsq(current) < 1e-10f || math.lengthsq(desired) < 1e-10f)
                return aligned;

            current = math.normalize(current);
            desired = math.normalize(desired);
            float yaw = math.atan2(math.dot(math.cross(current, desired), up), math.dot(current, desired));
            float maxRad = math.max(0f, maxRadians);
            yaw = math.clamp(yaw, -maxRad, maxRad);
            if (math.abs(yaw) < 1e-8f)
                return aligned;
            return math.mul(quaternion.AxisAngle(up, yaw), aligned);
        }

        /// <summary>
        /// Great-circle interpolation between two shell points. t=0 is <paramref name="from"/>.
        /// </summary>
        public static float3 SphericalLerp(float3 from, float3 to, float t, float radius)
        {
            float r = math.max(1e-3f, radius);
            float3 a = math.normalizesafe(from, new float3(0f, 1f, 0f));
            float3 b = math.normalizesafe(to, a);
            float dt = math.clamp(math.dot(a, b), -1f, 1f);
            if (dt > 0.9995f)
                return ProjectToSphere(math.lerp(from, to, t), r);

            float omega = math.acos(dt);
            float so = math.sin(omega);
            if (math.abs(so) < 1e-6f)
                return ProjectToSphere(math.lerp(from, to, t), r);

            float3 dir = (math.sin((1f - t) * omega) * a + math.sin(t * omega) * b) / so;
            return dir * r;
        }

        /// <summary>
        /// Great-circle interpolation along the <b>long</b> way from <paramref name="from"/> to
        /// <paramref name="to"/> (the complement of <see cref="SphericalLerp"/>).
        /// </summary>
        public static float3 SphericalLerpLong(float3 from, float3 to, float t, float radius)
        {
            float r = math.max(1e-3f, radius);
            float3 a = math.normalizesafe(from, new float3(0f, 1f, 0f));
            float3 b = math.normalizesafe(to, a);
            float dt = math.clamp(math.dot(a, b), -1f, 1f);
            float omega = math.acos(dt);
            float longOmega = (math.PI * 2f) - omega;
            if (longOmega < 1e-4f)
                return ProjectToSphere(from, r);

            float3 axis = math.cross(a, b);
            if (math.lengthsq(axis) < 1e-10f)
                axis = OrthonormalTangent(a);
            else
                axis = math.normalize(axis);

            return math.mul(quaternion.AxisAngle(-axis, t * longOmega), a) * r;
        }

        /// <summary>
        /// Orthonormal surface frame at <paramref name="position"/>.
        /// <paramref name="preferredForward"/> is flattened onto the tangent plane.
        /// </summary>
        public static void LocalFrame(
            float3 position,
            float3 preferredForward,
            out float3 up,
            out float3 forward,
            out float3 right)
        {
            LocalFrame(position, preferredForward, preferredForward, out up, out forward, out right);
        }

        /// <summary>
        /// Same as <see cref="LocalFrame(float3,float3,out float3,out float3,out float3)"/> but
        /// uses <paramref name="fallbackForward"/> (typically transported velocity) when the
        /// preferred heading is radial — that is what happens at the poles.
        /// </summary>
        public static void LocalFrame(
            float3 position,
            float3 preferredForward,
            float3 fallbackForward,
            out float3 up,
            out float3 forward,
            out float3 right)
        {
            up = LocalUp(position);
            float3 f = FlattenToTangent(preferredForward, position);
            if (math.lengthsq(f) < 1e-8f)
                f = FlattenToTangent(fallbackForward, position);
            if (math.lengthsq(f) < 1e-8f)
                f = OrthonormalTangent(up);
            else
                f = math.normalize(f);

            right = math.normalize(math.cross(up, f));
            forward = math.normalize(math.cross(right, up));
        }

        /// <summary>
        /// Yaw-only rotation on the shell: look along flattened forward, up = radial.
        /// </summary>
        public static quaternion LookRotationOnSurface(float3 position, float3 preferredForward)
        {
            LocalFrame(position, preferredForward, out float3 up, out float3 forward, out _);
            return quaternion.LookRotationSafe(forward, up);
        }

        /// <summary>
        /// Yaw on the shell, carrying heading across the poles via <paramref name="fallbackForward"/>.
        /// </summary>
        public static quaternion LookRotationOnSurface(
            float3 position,
            float3 preferredForward,
            float3 fallbackForward)
        {
            LocalFrame(position, preferredForward, fallbackForward, out float3 up, out float3 forward, out _);
            return quaternion.LookRotationSafe(forward, up);
        }

        /// <summary>
        /// Rotate <paramref name="offset"/> from the planet's local tangent frame into world.
        /// Planet local +Y is radial; local XZ is the old planar orbit plane.
        /// </summary>
        public static float3 OrbitOffsetWorld(float3 planetPos, float localX, float localZ)
        {
            float3 up = LocalUp(planetPos);
            float3 tangent = OrthonormalTangent(up);
            float3 bitangent = math.normalize(math.cross(up, tangent));
            return tangent * localX + bitangent * localZ;
        }

        /// <summary>
        /// Stable unit tangent perpendicular to <paramref name="up"/>.
        /// Hughes–Moeller: no latitude threshold, so the basis does not jump 90° at the poles.
        /// </summary>
        public static float3 OrthonormalTangent(float3 up)
        {
            float3 n = math.normalizesafe(up, new float3(0f, 1f, 0f));
            // Prefer the larger of |x| and |z| so the construction is never parallel to n.
            if (n.x * n.x > n.z * n.z)
                return math.normalize(new float3(-n.y, n.x, 0f));
            return math.normalize(new float3(0f, -n.z, n.y));
        }

        /// <summary>
        /// Even spherical directions (Fibonacci / Vogel). Index in <c>[0, count)</c>.
        /// </summary>
        public static float3 FibonacciDirection(int index, int count)
        {
            int n = math.max(1, count);
            int i = math.clamp(index, 0, n - 1);
            float golden = (1f + math.sqrt(5f)) * 0.5f;
            float ga = 2f * math.PI * (1f - 1f / golden);
            float y = 1f - 2f * (i + 0.5f) / n;
            float r = math.sqrt(math.max(0f, 1f - y * y));
            float theta = ga * i;
            return math.normalize(new float3(math.cos(theta) * r, y, math.sin(theta) * r));
        }

        /// <summary>
        /// Random unit direction (uniform on the sphere) from a Burst-safe RNG.
        /// </summary>
        public static float3 RandomUnitDirection(ref Unity.Mathematics.Random rng)
        {
            float z = rng.NextFloat(-1f, 1f);
            float t = rng.NextFloat(0f, 2f * math.PI);
            float r = math.sqrt(math.max(0f, 1f - z * z));
            return new float3(r * math.cos(t), z, r * math.sin(t));
        }

        /// <summary>
        /// von Mises–Fisher sample around <paramref name="mean"/> (already a unit vector).
        /// <paramref name="concentration"/> κ: 0 is uniform, large values hug the mean.
        /// </summary>
        public static float3 VonMisesFisher(float3 mean, float concentration, ref Unity.Mathematics.Random rng)
        {
            float3 mu = math.normalizesafe(mean, new float3(0f, 1f, 0f));
            float kappa = math.max(0f, concentration);
            if (kappa < 1e-4f)
                return RandomUnitDirection(ref rng);

            float xi = rng.NextFloat();
            float w = 1f + (math.log(xi + (1f - xi) * math.exp(-2f * kappa)) / kappa);
            w = math.clamp(w, -1f, 1f);
            float t = rng.NextFloat(0f, 2f * math.PI);
            float r = math.sqrt(math.max(0f, 1f - w * w));
            float3 local = new float3(r * math.cos(t), w, r * math.sin(t));

            // Rotate +Y onto mu.
            float3 up = new float3(0f, 1f, 0f);
            float3 axis = math.cross(up, mu);
            float axisLenSq = math.lengthsq(axis);
            if (axisLenSq < 1e-10f)
                return math.dot(up, mu) > 0f ? local : -local;

            float3 n = axis * math.rsqrt(axisLenSq);
            float c = math.dot(up, mu);
            float s = math.sqrt(math.max(0f, 1f - c * c));
            // Rodrigues
            return local * c + math.cross(n, local) * s + n * math.dot(n, local) * (1f - c);
        }

        /// <summary>
        /// Radius from mapW/mapH pair (square). Used by callers that still pass both axes.
        /// </summary>
        public static float RadiusFromMapAxes(float mapW, float mapH) =>
            RadiusFromMapSize(math.max(mapW, mapH));

        /// <summary>
        /// Burst-safe shell radius. Never reads the static cache (BC1040).
        /// Prefers designer axes; else |position| when the mover is already on the shell.
        /// </summary>
        public static float BurstSafeRadius(float mapW, float mapH, float3 position)
        {
            float fromAxes = RadiusFromMapAxes(mapW, mapH);
            if (IsValidRadius(fromAxes))
                return fromAxes;
            return math.max(MinValidRadius, math.length(position));
        }

        /// <summary>Burst-safe shell radius from a point already on the sphere.</summary>
        public static float BurstSafeRadius(float3 position) =>
            math.max(MinValidRadius, math.length(position));

        /// <summary>
        /// Local tangent-chart coordinates of <paramref name="point"/> with origin at
        /// <paramref name="origin"/>. X = orthonormal tangent, Y = bitangent.
        /// Used for territory PIT and minimap radar.
        /// </summary>
        public static float2 TangentChartXY(float3 origin, float3 point, float radius)
        {
            float3 up = LocalUp(origin);
            float3 t = OrthonormalTangent(up);
            float3 b = math.normalize(math.cross(up, t));
            float3 off = GeodesicOffset(origin, point, radius);
            return new float2(math.dot(off, t), math.dot(off, b));
        }

        /// <summary>
        /// Tangent chart aligned to a screen frame: X = along <paramref name="screenRight"/>,
        /// Y = along <paramref name="screenUp"/>. Use the gameplay camera so minimap motion
        /// matches what the player sees.
        /// </summary>
        public static float2 TangentChartAligned(
            float3 origin,
            float3 point,
            float radius,
            float3 screenRight,
            float3 screenUp)
        {
            float3 r = FlattenToTangent(screenRight, origin);
            float3 u = FlattenToTangent(screenUp, origin);
            if (math.lengthsq(r) < 1e-8f)
                r = OrthonormalTangent(LocalUp(origin));
            else
                r = math.normalize(r);
            if (math.lengthsq(u) < 1e-8f)
                u = math.normalize(math.cross(LocalUp(origin), r));
            else
                u = math.normalize(u);

            float3 off = GeodesicOffset(origin, point, radius);
            return new float2(math.dot(off, r), math.dot(off, u));
        }

        /// <summary>
        /// Nearest intersection of a world ray with the playable shell.
        /// Never returns the far-side hit (through the planet).
        /// </summary>
        /// <summary>
        /// Encodes a world direction as a 2D tangent-chart vector at <paramref name="origin"/>
        /// (same basis as <see cref="TangentChartXY"/>). Used for ghosted <c>AimPlanarDir</c>.
        /// </summary>
        public static float2 EncodeTangentDir(float3 origin, float3 worldDir) =>
            EncodeTangentDir(origin, OrthonormalTangent(LocalUp(origin)), worldDir);

        /// <summary>
        /// Encodes <paramref name="worldDir"/> in the ship's own tangent frame (forward × radial).
        /// Use this for ghosted aim so poles cannot flip the chart.
        /// </summary>
        public static float2 EncodeTangentDir(float3 origin, quaternion rotation, float3 worldDir) =>
            EncodeTangentDir(origin, math.mul(rotation, new float3(0f, 0f, 1f)), worldDir);

        public static float2 EncodeTangentDir(float3 origin, float3 chartForward, float3 worldDir)
        {
            float3 up = LocalUp(origin);
            float3 t = FlattenToTangent(chartForward, origin);
            if (math.lengthsq(t) < 1e-10f)
                t = OrthonormalTangent(up);
            else
                t = math.normalize(t);
            float3 b = math.normalize(math.cross(up, t));
            float3 d = FlattenToTangent(worldDir, origin);
            if (math.lengthsq(d) < 1e-10f)
                return float2.zero;
            d = math.normalize(d);
            return new float2(math.dot(d, t), math.dot(d, b));
        }

        /// <summary>
        /// Reconstructs a unit tangent direction from <see cref="EncodeTangentDir"/>.
        /// </summary>
        public static float3 DecodeTangentDir(float3 origin, float2 encoded) =>
            DecodeTangentDir(origin, OrthonormalTangent(LocalUp(origin)), encoded);

        /// <summary>
        /// Reconstructs aim using the same ship-forward chart as
        /// <see cref="EncodeTangentDir(float3, quaternion, float3)"/>.
        /// </summary>
        public static float3 DecodeTangentDir(float3 origin, quaternion rotation, float2 encoded) =>
            DecodeTangentDir(origin, math.mul(rotation, new float3(0f, 0f, 1f)), encoded);

        public static float3 DecodeTangentDir(float3 origin, float3 chartForward, float2 encoded)
        {
            float3 up = LocalUp(origin);
            float3 t = FlattenToTangent(chartForward, origin);
            if (math.lengthsq(t) < 1e-10f)
                t = OrthonormalTangent(up);
            else
                t = math.normalize(t);
            float3 b = math.normalize(math.cross(up, t));
            float3 d = t * encoded.x + b * encoded.y;
            if (math.lengthsq(d) < 1e-10f)
                return t;
            return math.normalize(d);
        }

        /// <summary>
        /// Sit-on-surface rotation: local +Y is radial (toward the camera in top-down-on-sphere).
        /// </summary>
        public static quaternion SurfaceSitRotation(float3 position)
        {
            float3 up = LocalUp(position);
            return quaternion.LookRotationSafe(OrthonormalTangent(up), up);
        }

        public static bool TryRaycastNear(
            float3 rayOrigin,
            float3 rayDirection,
            float radius,
            out float3 hit)
        {
            hit = default;
            float3 d = math.normalizesafe(rayDirection, new float3(0f, 0f, 1f));
            float r = math.max(1e-3f, radius);
            float3 oc = rayOrigin;
            float b = math.dot(oc, d);
            float c = math.lengthsq(oc) - r * r;
            float disc = b * b - c;
            if (disc < 0f)
                return false;

            float s = math.sqrt(disc);
            float t0 = -b - s;
            float t1 = -b + s;
            float t = t0 >= 0f ? t0 : t1;
            if (t < 0f)
                return false;

            hit = rayOrigin + d * t;
            return true;
        }
    }
}
