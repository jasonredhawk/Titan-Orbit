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
        [Tooltip("Offset from ship. Y scales with zoom so camera height matches view.")]
        [SerializeField] private Vector3 offsetAtReferenceLevel = new Vector3(0, 40, 0);

        [Header("Distance by Ship Level")]
        [Tooltip("Orthographic size at level 6 (100% zoom out). Level 1 uses zoomScaleAtLevel1 of this; reaches this at level 6. Larger = more zoomed out.")]
        [SerializeField] private float orthographicSizeAtReferenceLevel = 24f;
        [Tooltip("Zoom scale at level 1 (e.g. 0.7 = slightly closer). Reaches 1.0 (100%) at level 6.")]
        [Range(0.3f, 0.95f)]
        [SerializeField] private float zoomScaleAtLevel1 = 0.7f;
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

            int level = 1;
            var ship = target.GetComponent<Starship>();
            if (ship != null)
                level = ship.ShipLevel;

            // Level 1 = much closer (zoomScaleAtLevel1), level 6 = 100% zoom out (1.0). Linear interpolation.
            float zoomT = level <= 1 ? 0f : Mathf.Clamp01((level - 1) / 5f);
            float targetScale = Mathf.Lerp(zoomScaleAtLevel1, 1f, zoomT);

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
