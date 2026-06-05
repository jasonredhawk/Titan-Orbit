using System.Collections.Generic;
using UnityEngine;

namespace TitanOrbit.Camera
{
    /// <summary>
    /// Closed Catmull-Rom loop around the ship. Waypoint 0 is always the camera anchor
    /// (current pose when the path is built). Segment 0 departs straight toward waypoint 1.
    /// </summary>
    internal sealed class CameraTheatricalOrbit
    {
        private readonly List<Vector3> localWaypoints = new List<Vector3>(14);
        private float pathProgress;
        private float pathDurationSeconds = 90f;
        private float pathRadiusMinMultiplier = 2.4f;
        private float pathRadiusMaxMultiplier = 5.2f;
        private float characteristicRadiusCached = 4f;
        private float minElevationDeg = -32f;
        private float maxElevationDeg = 52f;
        private int waypointCount = 8;
        private float pathDurationMinSeconds = 720f;
        private float pathDurationMaxSeconds = 1080f;
        private Vector3 entryControlLocalOffset;
        private System.Random rng;
        private bool hasPath;

        public void SetCharacteristicRadius(float radius) =>
            characteristicRadiusCached = Mathf.Max(1f, radius);

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
        /// Waypoint 0 = camera anchor; remaining points are random. Closed loop.
        /// </summary>
        public void BeginPathFromCamera(
            Vector3 cameraWorldPosition,
            Vector3 focusWorld,
            Quaternion shipRotation)
        {
            if (rng == null)
                rng = new System.Random(Random.Range(int.MinValue, int.MaxValue));

            Quaternion invRot = Quaternion.Inverse(shipRotation);
            Vector3 anchorLocal = invRot * (cameraWorldPosition - focusWorld);

            localWaypoints.Clear();
            localWaypoints.Add(anchorLocal);

            int randomCount = Mathf.Max(4, waypointCount - 1);
            for (int i = 0; i < randomCount; i++)
                localWaypoints.Add(GenerateRandomLocalOffset());

            // Match p0 to the anchor so segment 0 departs forward toward waypoint 1 instead of pulling back.
            entryControlLocalOffset = anchorLocal;

            pathProgress = 0f;
            float durationMin = Mathf.Max(8f, pathDurationMinSeconds);
            float durationMax = Mathf.Max(durationMin, pathDurationMaxSeconds);
            pathDurationSeconds = Mathf.Lerp(durationMin, durationMax, (float)rng.NextDouble());
            hasPath = localWaypoints.Count >= 4;
        }

        public void Advance(float deltaTime, Vector3 focusWorld, Quaternion shipRotation)
        {
            if (!hasPath || pathDurationSeconds <= 0.0001f)
                return;

            pathProgress += deltaTime / pathDurationSeconds;
            if (pathProgress < 1f)
                return;

            Sample(focusWorld, shipRotation, out Vector3 endPosition, out _, out _);
            BeginPathFromCamera(endPosition, focusWorld, shipRotation);
        }

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

        private Vector3 GenerateRandomLocalOffset()
        {
            float baseRadius = Mathf.Max(2f, characteristicRadiusCached);
            float azimuth = (float)(rng.NextDouble() * Mathf.PI * 2d);
            float elevDeg = Mathf.Lerp(minElevationDeg, maxElevationDeg, (float)rng.NextDouble());
            float elevRad = elevDeg * Mathf.Deg2Rad;
            float radiusMul = Mathf.Lerp(pathRadiusMinMultiplier, pathRadiusMaxMultiplier, (float)rng.NextDouble());
            float radius = baseRadius * radiusMul;

            float cosElev = Mathf.Cos(elevRad);
            float sinElev = Mathf.Sin(elevRad);
            Vector3 localDir = new Vector3(
                cosElev * Mathf.Sin(azimuth),
                sinElev,
                cosElev * Mathf.Cos(azimuth));

            return localDir * radius;
        }

        private static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
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
