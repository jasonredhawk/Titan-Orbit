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
        [Tooltip("Orthographic size (view distance). Larger = more zoomed out. 12 = closer view, 24 = zoomed out.")]
        [SerializeField] private float orthographicSizeAtReferenceLevel = 12f;
        [Tooltip("Zoom scale at level 1 (e.g. 0.7 = slightly closer). Reaches 1.0 (100%) at level 6. 1 = no level-based zoom.")]
        [Range(0.3f, 1f)]
        [SerializeField] private float zoomScaleAtLevel1 = 1f;
        [Tooltip("Time in seconds to smoothly transition to new distance when level changes (0 = instant).")]
        [Min(0f)]
        [SerializeField] private float distanceChangeSmoothTime = 1.5f;
        [Tooltip("When you first spawn, camera starts this many times more zoomed out (bird's eye), then zooms in. 1 = no spawn zoom.")]
        [Min(1f)]
        [SerializeField] private float spawnZoomScale = 1f;

        [Header("Galactic Zoom")]
        [Tooltip("Master toggle for the galactic zoom-out effect. Disable to keep gameplay zoom only.")]
        [SerializeField] private bool galacticZoomEnabled = true;
        [Tooltip("Orthographic size used for far-map view. Actual galactic zoom stops halfway between default zoom and this value.")]
        [SerializeField] private float galacticZoomOrthoSize = 180f;
        [Tooltip("Seconds to smoothly zoom out to galactic view after depositing all gems.")]
        [Min(0.1f)]
        [SerializeField] private float galacticZoomOutDuration = 8f;
        [Tooltip("Seconds to zoom back in to default gameplay zoom when the player moves.")]
        [Min(0.05f)]
        [SerializeField] private float galacticZoomInDuration = 2f;
        [Tooltip("Optional: space background that can be hidden while zoomed out.")]
        [SerializeField] private ScrollingSpaceBackground spaceBackground;

        private UnityEngine.Camera cam;
        private float currentScale = 1f;
        private float scaleVelocity;

        // Galactic zoom state
        private bool galacticZoomActive;
        private bool galacticZoomReturning;
        private float galacticZoomElapsed;
        private float galacticZoomStartSize;

        private void Awake()
        {
            cam = GetComponent<UnityEngine.Camera>();
            if (cam == null)
            {
                cam = gameObject.AddComponent<UnityEngine.Camera>();
            }

            if (spaceBackground == null)
            {
                spaceBackground = FindFirstObjectByType<ScrollingSpaceBackground>();
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

            float defaultOrthoSize = orthographicSizeAtReferenceLevel * currentScale;

            if (cam.orthographic)
            {
                if (!galacticZoomActive)
                {
                    cam.orthographicSize = defaultOrthoSize;
                }
                else
                {
                    galacticZoomElapsed += Time.deltaTime;

                    // Target zoomed-out size is halfway between current default zoom and the far-map size.
                    float halfwayOutSize = Mathf.Lerp(defaultOrthoSize, galacticZoomOrthoSize, 0.5f);

                    if (!galacticZoomReturning)
                    {
                        float tOut = galacticZoomOutDuration > 0.0001f
                            ? Mathf.Clamp01(galacticZoomElapsed / galacticZoomOutDuration)
                            : 1f;
                        float size = Mathf.Lerp(galacticZoomStartSize, halfwayOutSize, tOut);
                        cam.orthographicSize = size;
                    }
                    else
                    {
                        float tIn = galacticZoomInDuration > 0.0001f
                            ? Mathf.Clamp01(galacticZoomElapsed / galacticZoomInDuration)
                            : 1f;
                        float size = Mathf.Lerp(galacticZoomStartSize, defaultOrthoSize, tIn);
                        cam.orthographicSize = size;

                        if (tIn >= 1f - 0.0001f)
                        {
                            galacticZoomActive = false;
                            galacticZoomReturning = false;
                            if (spaceBackground != null)
                                spaceBackground.SetTemporarilyHidden(false);
                        }
                    }
                }
            }

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

        /// <summary>Begin galactic zoom out toward a large orthographic size.</summary>
        public void StartGalacticZoomOut()
        {
            if (!galacticZoomEnabled)
                return;

            if (cam == null) return;

            galacticZoomActive = true;
            galacticZoomReturning = false;
            galacticZoomElapsed = 0f;
            galacticZoomStartSize = cam.orthographic ? cam.orthographicSize : orthographicSizeAtReferenceLevel * currentScale;

            if (spaceBackground != null)
            {
                spaceBackground.SetTemporarilyHidden(true);
            }
        }

        /// <summary>Trigger fast zoom back in to the default gameplay zoom.</summary>
        public void TriggerGalacticZoomReturn()
        {
            if (!galacticZoomEnabled || !galacticZoomActive || galacticZoomReturning || cam == null)
                return;

            galacticZoomReturning = true;
            galacticZoomElapsed = 0f;
            galacticZoomStartSize = cam.orthographic ? cam.orthographicSize : orthographicSizeAtReferenceLevel * currentScale;
        }
    }
}
