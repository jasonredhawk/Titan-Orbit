using UnityEngine;
using TitanOrbit.Entities;

namespace TitanOrbit.Camera
{
    /// <summary>
    /// Camera controller that follows the player ship with smooth movement.
    /// Camera distance (zoom) and height scale with ship level: level 1 = closer, higher levels = further (more view).
    /// </summary>
    public class CameraController : MonoBehaviour
    {
        [Header("Camera Settings")]
        [SerializeField] private Transform target;
        [Tooltip("Offset at reference level (level 7). Actual offset Y scales with ship level.")]
        [SerializeField] private Vector3 offsetAtReferenceLevel = new Vector3(0, 20, 0);

        [Header("Distance by Ship Level")]
        [Tooltip("Ship level that uses the reference distance (current 'normal' zoom).")]
        [SerializeField] private int referenceLevel = 7;
        [Tooltip("Orthographic size at reference level (larger = more of the world visible).")]
        [SerializeField] private float orthographicSizeAtReferenceLevel = 12f;
        [Tooltip("At level 1, camera distance is this fraction of reference (e.g. 0.7 = 70%, closer view).")]
        [Range(0.5f, 1f)]
        [SerializeField] private float level1DistanceScale = 0.7f;
        [Tooltip("Time in seconds to smoothly transition to new distance when level changes (0 = instant).")]
        [Min(0f)]
        [SerializeField] private float distanceChangeSmoothTime = 1.5f;
        [Tooltip("When you first spawn, camera starts this many times more zoomed out (bird's eye), then zooms in. 1 = no spawn zoom.")]
        [Min(1f)]
        [SerializeField] private float spawnZoomScale = 2.5f;

        private UnityEngine.Camera cam;
        private float currentScale = 1f;
        private float scaleVelocity;

        private void Awake()
        {
            cam = GetComponent<UnityEngine.Camera>();
            if (cam == null)
            {
                cam = gameObject.AddComponent<UnityEngine.Camera>();
            }

            // Set up camera for top-down view
            transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        }

        private void LateUpdate()
        {
            if (target == null) return;

            int level = referenceLevel;
            var ship = target.GetComponent<Starship>();
            if (ship != null)
                level = ship.ShipLevel;

            float t = referenceLevel <= 1
                ? 1f
                : Mathf.Clamp01((float)(level - 1) / (referenceLevel - 1));
            float targetScale = Mathf.Lerp(level1DistanceScale, 1f, t);

            if (distanceChangeSmoothTime > 0f)
                currentScale = Mathf.SmoothDamp(currentScale, targetScale, ref scaleVelocity, distanceChangeSmoothTime);
            else
                currentScale = targetScale;

            if (cam.orthographic)
                cam.orthographicSize = orthographicSizeAtReferenceLevel * currentScale;

            Vector3 offset = new Vector3(
                offsetAtReferenceLevel.x,
                offsetAtReferenceLevel.y * currentScale,
                offsetAtReferenceLevel.z);

            // Lock camera to ship - ship is always in wrapped coordinates, so just follow directly
            Vector3 targetPosition = target.position + offset;
            transform.position = targetPosition;
        }

        public void SetTarget(Transform newTarget)
        {
            target = newTarget;
            if (newTarget != null && spawnZoomScale > 1f)
            {
                currentScale = spawnZoomScale;
                scaleVelocity = 0f;
            }
        }
    }
}
