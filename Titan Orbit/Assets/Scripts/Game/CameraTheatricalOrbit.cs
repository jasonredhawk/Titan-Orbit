using System.Collections.Generic;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Closed Catmull-Rom camera path orbiting the local ship for idle theatrical shots.
    /// Waypoints live in ship-local space so the orbit stays locked to the hull when the
    /// ship wraps or drifts. Paired with <see cref="CameraFollowEcs"/>, which owns idle
    /// detection and enter/exit blends. Client presentation only — never drives sim.
    /// Same assembly as the gameplay camera — do not use namespace TitanOrbit.Camera
    /// (that shadows UnityEngine.Camera for every TitanOrbit.Game script).
    /// <para>
    /// [TITAN-ORBIT] The first path can start at a pullback point farther than the live
    /// gameplay camera so the intro zooms away instead of diving in. Later loops start from
    /// the current camera pose so the next random orbit continues without a hitch.
    /// </para>
    /// </summary>
    internal sealed class CameraTheatricalOrbit
    {
        /// <summary>Ship-local waypoints for the current closed loop (waypoint 0 is the start).</summary>
        private readonly List<Vector3> localWaypoints = new List<Vector3>(14);

        /// <summary>0 at the start of the loop, 1 when the path should rebuild.</summary>
        private float pathProgress;

        /// <summary>Seconds to travel the current loop (random in the configured range).</summary>
        private float pathDurationSeconds = 90f;

        /// <summary>Near-orbit radius as a multiple of ship characteristic radius.</summary>
        private float pathRadiusMinMultiplier = 2.8f;

        /// <summary>Far-orbit radius as a multiple of ship characteristic radius.</summary>
        private float pathRadiusMaxMultiplier = 9.5f;

        /// <summary>Ship visual size used to scale orbit radii (world units).</summary>
        private float characteristicRadiusCached = 4f;

        /// <summary>Lowest random waypoint elevation in degrees (− = below the hull).</summary>
        private float minElevationDeg = -32f;

        /// <summary>Highest random waypoint elevation in degrees.</summary>
        private float maxElevationDeg = 52f;

        /// <summary>Closed-loop waypoint count including the start point.</summary>
        private int waypointCount = 8;

        /// <summary>Minimum seconds for one full random loop.</summary>
        private float pathDurationMinSeconds = 22f;

        /// <summary>Maximum seconds for one full random loop.</summary>
        private float pathDurationMaxSeconds = 36f;

        /// <summary>
        /// Catmull-Rom p0 for segment 0. On a pullback intro this is the live camera
        /// (nearer than waypoint 0) so the spline leaves the pullback toward the orbit.
        /// </summary>
        private Vector3 entryControlLocalOffset;

        /// <summary>[STANDARD] Deterministic RNG for this session's waypoint rolls.</summary>
        private System.Random rng;

        /// <summary>True after <see cref="BeginPathFromCamera"/> built a usable loop.</summary>
        private bool hasPath;

        /// <summary>[TITAN-ORBIT] Sets ship characteristic radius used for path radius multipliers.</summary>
        /// <param name="radius">Approximate hull size in world units (at least 1).</param>
        public void SetCharacteristicRadius(float radius) =>
            characteristicRadiusCached = Mathf.Max(1f, radius);

        /// <summary>
        /// Stores designer ranges for random waypoint generation and path duration.
        /// Called when the cinematic rig initializes or Inspector knobs change.
        /// </summary>
        public void ConfigurePathGeneration(
            int randomWaypointCount,
            float minElevationDeg,
            float maxElevationDeg,
            float radiusMinMultiplier,
            float radiusMaxMultiplier,
            float pathDurationMinSeconds,
            float pathDurationMaxSeconds)
        {
            waypointCount = Mathf.Clamp(randomWaypointCount, 5, 15);
            this.minElevationDeg = minElevationDeg;
            this.maxElevationDeg = maxElevationDeg;
            pathRadiusMinMultiplier = radiusMinMultiplier;
            pathRadiusMaxMultiplier = radiusMaxMultiplier;
            this.pathDurationMinSeconds = pathDurationMinSeconds;
            this.pathDurationMaxSeconds = pathDurationMaxSeconds;
        }

        /// <summary>
        /// Shared pullback distance used by the enter blend and the first orbit waypoint.
        /// Farther than the live camera along the same local offset, so the intro backs away.
        /// </summary>
        /// <param name="anchorLocal">Current camera offset from focus, in ship-local space.</param>
        /// <param name="characteristicRadius">Ship visual size in world units.</param>
        /// <param name="radiusMaxMultiplier">Far-orbit multiplier (same as path generation).</param>
        /// <returns>Ship-local offset farther from the hull than <paramref name="anchorLocal"/>.</returns>
        public static Vector3 ComputePullbackLocal(
            Vector3 anchorLocal,
            float characteristicRadius,
            float radiusMaxMultiplier)
        {
            // --- Pullback along the current view axis ---
            // [TITAN-ORBIT] The gameplay camera sits high above the ship. We keep that
            // direction and push farther out so the first move is "zoom away", not "dive in".
            float anchorDist = Mathf.Max(0.01f, anchorLocal.magnitude);
            float farRadius = Mathf.Max(2f, characteristicRadius * radiusMaxMultiplier);
            float pullbackDist = Mathf.Max(
                anchorDist + characteristicRadius * 0.75f,
                farRadius * 0.92f);

            if (anchorLocal.sqrMagnitude < 0.0001f)
                return Vector3.up * pullbackDist;

            return anchorLocal.normalized * pullbackDist;
        }

        /// <summary>
        /// Builds a new closed loop. When <paramref name="pullBackFirstWaypoint"/> is true,
        /// waypoint 0 is farther than the live camera (theatrical intro). When false
        /// (loop rebuild), waypoint 0 is the current camera so the next orbit continues smoothly.
        /// </summary>
        /// <param name="cameraWorldPosition">World pose to start from (gameplay camera or last sample).</param>
        /// <param name="focusWorld">Ship focus the path orbits.</param>
        /// <param name="shipRotation">Ship orientation for local-space waypoints.</param>
        /// <param name="pullBackFirstWaypoint">True for the idle-enter intro; false when looping.</param>
        public void BeginPathFromCamera(
            Vector3 cameraWorldPosition,
            Vector3 focusWorld,
            Quaternion shipRotation,
            bool pullBackFirstWaypoint = false)
        {
            if (rng == null)
                rng = new System.Random(Random.Range(int.MinValue, int.MaxValue));

            Quaternion invRot = Quaternion.Inverse(shipRotation);
            Vector3 anchorLocal = invRot * (cameraWorldPosition - focusWorld);

            localWaypoints.Clear();
            Vector3 startLocal = anchorLocal;
            if (pullBackFirstWaypoint)
            {
                // --- Intro: start farther than the live camera ---
                startLocal = ComputePullbackLocal(
                    anchorLocal,
                    characteristicRadiusCached,
                    pathRadiusMaxMultiplier);
                localWaypoints.Add(startLocal);
            }
            else
            {
                // --- Loop: start at the current camera so the next path does not hitch ---
                localWaypoints.Add(anchorLocal);
            }

            // Gameplay sit is almost +Y. Circling while still looking down spins the
            // view around world Y. Tilt off that pole first, then a small yaw, then
            // the surround — one orbit direction the whole loop.
            float startRadius = Mathf.Max(0.01f, startLocal.magnitude);
            Vector3 startDir = startLocal / startRadius;
            float startElevRad = Mathf.Asin(Mathf.Clamp(startDir.y, -1f, 1f));
            float startAzimuth = startDir.x * startDir.x + startDir.z * startDir.z > 0.0004f
                ? Mathf.Atan2(startDir.x, startDir.z)
                : 0f;
            float orbitSign = rng.NextDouble() < 0.5d ? -1f : 1f;

            float tiltElevRad = Mathf.Lerp(startElevRad, 42f * Mathf.Deg2Rad, 0.88f);
            tiltElevRad = Mathf.Clamp(tiltElevRad, -20f * Mathf.Deg2Rad, 55f * Mathf.Deg2Rad);
            float tiltRadius = Mathf.Max(
                startRadius * 1.2f,
                characteristicRadiusCached * 3.4f);
            localWaypoints.Add(SphericalLocal(startAzimuth, tiltElevRad, tiltRadius));

            float introYaw = (22f + (float)rng.NextDouble() * 10f) * Mathf.Deg2Rad;
            float afterIntroAzimuth = startAzimuth + orbitSign * introYaw;
            localWaypoints.Add(SphericalLocal(afterIntroAzimuth, tiltElevRad, tiltRadius * 1.08f));

            int surroundCount = Mathf.Max(3, waypointCount - 3);
            for (int i = 0; i < surroundCount; i++)
            {
                localWaypoints.Add(GenerateSurroundLocalOffset(
                    i,
                    surroundCount,
                    afterIntroAzimuth,
                    tiltElevRad,
                    tiltRadius,
                    orbitSign));
            }

            // Incoming control behind the first step so Catmull-Rom does not overshoot
            // the start and reverse (p0 == p1 did that on the first segment).
            if (localWaypoints.Count >= 2)
            {
                Vector3 firstStep = localWaypoints[1] - localWaypoints[0];
                entryControlLocalOffset = localWaypoints[0] - firstStep * 0.35f;
            }
            else
                entryControlLocalOffset = startLocal;

            pathProgress = 0f;
            float durationMin = Mathf.Max(8f, pathDurationMinSeconds);
            float durationMax = Mathf.Max(durationMin, pathDurationMaxSeconds);
            pathDurationSeconds = Mathf.Lerp(durationMin, durationMax, (float)rng.NextDouble());
            hasPath = localWaypoints.Count >= 4;
        }

        /// <summary>
        /// Advances path progress. When the loop completes, rebuilds a new random loop from
        /// the end pose (no pullback) so the camera keeps moving.
        /// </summary>
        /// <param name="deltaTime">Frame delta in seconds.</param>
        /// <param name="focusWorld">Ship/world focus point the path orbits.</param>
        /// <param name="shipRotation">Ship orientation for local-space waypoint conversion.</param>
        public void Advance(float deltaTime, Vector3 focusWorld, Quaternion shipRotation)
        {
            // --- Advance ---
            if (!hasPath || pathDurationSeconds <= 0.0001f)
                return;

            pathProgress += deltaTime / pathDurationSeconds;
            if (pathProgress < 1f)
                return;

            Sample(focusWorld, shipRotation, out Vector3 endPosition, out _, out _);
            BeginPathFromCamera(endPosition, focusWorld, shipRotation, pullBackFirstWaypoint: false);
        }

        /// <summary>
        /// Samples camera position along the current path segment. Outputs look target (focus)
        /// and a 0–1 zoom blend based on distance from focus (1 = closest / tightest FOV).
        /// </summary>
        public void Sample(
            Vector3 focusWorld,
            Quaternion shipRotation,
            out Vector3 cameraPosition,
            out Vector3 lookTarget,
            out float zoomT)
        {
            lookTarget = focusWorld;
            zoomT = 0f;

            if (!hasPath || localWaypoints.Count < 4)
            {
                cameraPosition = focusWorld + Vector3.up * 8f;
                return;
            }

            int count = localWaypoints.Count;
            float scaledT = pathProgress * count;
            int segment = Mathf.FloorToInt(scaledT) % count;
            float segmentT = scaledT - Mathf.Floor(scaledT);

            Vector3 localPosition;
            if (segment == 0)
            {
                localPosition = CatmullRom(
                    entryControlLocalOffset,
                    localWaypoints[0],
                    localWaypoints[1],
                    localWaypoints[Mathf.Min(2, count - 1)],
                    segmentT);
            }
            else
            {
                localPosition = CatmullRom(
                    localWaypoints[(segment - 1 + count) % count],
                    localWaypoints[segment],
                    localWaypoints[(segment + 1) % count],
                    localWaypoints[(segment + 2) % count],
                    segmentT);
            }

            cameraPosition = focusWorld + shipRotation * localPosition;

            float dist = Vector3.Distance(cameraPosition, focusWorld);
            float nearRadius = Mathf.Max(2f, characteristicRadiusCached * pathRadiusMinMultiplier);
            float farRadius = Mathf.Max(nearRadius + 1f, characteristicRadiusCached * pathRadiusMaxMultiplier);
            zoomT = Mathf.InverseLerp(farRadius, nearRadius, dist);
        }

        /// <summary>
        /// One surround waypoint after the intro tilt / small-yaw pair. Walks
        /// monotonically around <paramref name="startAzimuth"/> so the path does not
        /// reverse on the first legs.
        /// </summary>
        /// <param name="index">0-based waypoint among the random surround points.</param>
        /// <param name="count">How many surround points this loop uses.</param>
        /// <param name="startAzimuth">Ship-local azimuth of the current camera (0 = +Z).</param>
        /// <param name="startElevRad">Ship-local elevation of the current camera.</param>
        /// <param name="startRadius">Distance from focus to the current camera.</param>
        /// <param name="orbitSign">+1 or −1 — one consistent yaw direction for this loop.</param>
        private Vector3 GenerateSurroundLocalOffset(
            int index,
            int count,
            float startAzimuth,
            float startElevRad,
            float startRadius,
            float orbitSign)
        {
            // --- Monotonic surround from the live heading ---
            float slice = (Mathf.PI * 2f) / Mathf.Max(1, count);
            float jitter = (float)((rng.NextDouble() - 0.5d) * slice * 0.35d);
            float azimuth = startAzimuth + orbitSign * ((index + 1) * slice + jitter);

            float targetElevRad = Mathf.Lerp(minElevationDeg, maxElevationDeg, (float)rng.NextDouble())
                * Mathf.Deg2Rad;
            float baseRadius = Mathf.Max(2f, characteristicRadiusCached);
            float radiusMul;
            if (index >= 3 && rng.NextDouble() < 0.38d)
                radiusMul = pathRadiusMaxMultiplier * Mathf.Lerp(0.92f, 1.28f, (float)rng.NextDouble());
            else
                radiusMul = Mathf.Lerp(pathRadiusMinMultiplier, pathRadiusMaxMultiplier, (float)rng.NextDouble());
            float targetRadius = baseRadius * radiusMul;

            // First surround point still eases off the intro tilt; later points are full random.
            float poseEase = index <= 0 ? 0.45f : (index == 1 ? 0.75f : 1f);
            float elevRad = Mathf.Lerp(startElevRad, targetElevRad, poseEase);
            float radius = Mathf.Lerp(startRadius, targetRadius, poseEase);
            return SphericalLocal(azimuth, elevRad, radius);
        }

        /// <summary>Ship-local offset from azimuth (0 = +Z), elevation, and radius.</summary>
        static Vector3 SphericalLocal(float azimuth, float elevRad, float radius)
        {
            float cosElev = Mathf.Cos(elevRad);
            float sinElev = Mathf.Sin(elevRad);
            return new Vector3(
                cosElev * Mathf.Sin(azimuth),
                sinElev,
                cosElev * Mathf.Cos(azimuth)) * radius;
        }

        /// <summary>
        /// [STANDARD] Cubic Catmull-Rom interpolation. t=0 returns p1, t=1 returns p2.
        /// </summary>
        private static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            // --- CatmullRom ---
            float t2 = t * t;
            float t3 = t2 * t;
            return 0.5f * (
                (2f * p1)
                + (-p0 + p2) * t
                + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2
                + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
        }
    }
}
