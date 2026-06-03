using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.Serialization;
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
        [Tooltip("Base camera distance (view distance). Larger = more zoomed out. 12 = closer view, 24 = zoomed out.")]
        [FormerlySerializedAs("orthographicSizeAtReferenceLevel")]
        [SerializeField] private float zoomDistanceAtReferenceLevel = 12f;
        [Tooltip("Ship level that uses Zoom Scale At Level 1.")]
        [Min(1)]
        [SerializeField] private int minShipLevelForZoom = 1;
        [Tooltip("Ship level that uses Zoom Scale At Max Level (e.g. 6). Levels above this use the max scale.")]
        [Min(2)]
        [SerializeField] private int maxShipLevelForZoom = 6;
        [Tooltip("Multiplier on base zoom distance at min ship level. 1 = reference distance.")]
        [Min(0.1f)]
        [SerializeField] private float zoomScaleAtLevel1 = 1f;
        [Tooltip("Multiplier at max ship level. 1.5 = 50% more zoomed out than level 1. Linear steps per level in between.")]
        [Min(0.1f)]
        [SerializeField] private float zoomScaleAtMaxShipLevel = 1.5f;
        [Tooltip("Time in seconds to smoothly transition to new distance when level changes (0 = instant).")]
        [Min(0f)]
        [SerializeField] private float distanceChangeSmoothTime = 1.5f;
        [Tooltip("When you first spawn, camera starts this many times more zoomed out (bird's eye), then zooms in. 1 = no spawn zoom.")]
        [Min(1f)]
        [SerializeField] private float spawnZoomScale = 1f;

        [Header("Galactic Zoom")]
        [Tooltip("Master toggle for the galactic zoom-out effect. Disable to keep gameplay zoom only.")]
        [SerializeField] private bool galacticZoomEnabled = true;
        [Tooltip("Far-map camera distance target. Actual galactic zoom stops halfway between default zoom and this value.")]
        [FormerlySerializedAs("galacticZoomOrthoSize")]
        [SerializeField] private float galacticZoomDistance = 180f;
        [Tooltip("Seconds to smoothly zoom out to galactic view after depositing all gems.")]
        [Min(0.1f)]
        [SerializeField] private float galacticZoomOutDuration = 8f;
        [Tooltip("Seconds to zoom back in to default gameplay zoom when the player moves.")]
        [Min(0.05f)]
        [SerializeField] private float galacticZoomInDuration = 2f;
        [Tooltip("Optional: space background that can be hidden while zoomed out.")]
        [SerializeField] private ScrollingSpaceBackground spaceBackground;

        [Header("Impact Feedback")]
        [Tooltip("When enabled, asteroid and ship collisions shake the camera. Impact sounds are unchanged.")]
        [SerializeField] private bool collisionCameraShakeEnabled = true;
        [Tooltip("Max position jitter in camera-local space (applied after follow, so it is visible).")]
        [SerializeField] private Vector3 collisionShakeMaxTranslation = new Vector3(0.45f, 0.45f, 0.45f);
        [SerializeField] private float collisionShakeFrequency = 25f;
        [Tooltip("How fast shake intensity decays per second.")]
        [SerializeField] private float collisionShakeRecoverPerSecond = 1.2f;
        [SerializeField] private float collisionShakeSmoothingExponent = 1f;
        [Tooltip("Impulse shake at lightest vs hardest asteroid ram impacts (0–1).")]
        [SerializeField] private float ramImpactShakeMin = 0.05f;
        [SerializeField] private float ramImpactShakeMax = 0.35f;
        [Tooltip("Sustained shake while grinding an asteroid at min vs max grind intensity (0–1).")]
        [SerializeField] private float ramGrindShakeMin = 0.02f;
        [SerializeField] private float ramGrindShakeMax = 0.18f;
        [Tooltip("Extra impulse when grinding through an asteroid breakup (ram + grind at destruction), scaled by gem size.")]
        [SerializeField] private float ramDestroyShakeMin = 0.25f;
        [SerializeField] private float ramDestroyShakeMax = 0.85f;
        [Tooltip("Gem value range used to scale destroy shake (matches asteroid size 1–70).")]
        [SerializeField] private float ramDestroyShakeSizeMin = 1f;
        [SerializeField] private float ramDestroyShakeSizeMax = 70f;

        [Header("Mouse Zoom")]
        [Tooltip("Allow mouse wheel to zoom out from the default ship zoom up to max zoom out size.")]
        [SerializeField] private bool mouseZoomEnabled = true;
        [Tooltip("Largest camera distance when fully zoomed out with the wheel (larger = see more of the map).")]
        [FormerlySerializedAs("maxManualZoomOutOrthoSize")]
        [SerializeField] private float maxManualZoomOutDistance = 80f;
        [Tooltip("How much the zoom slider moves per scroll wheel unit (Unity uses ~±120 per notch on Windows).")]
        [SerializeField] private float mouseWheelZoomSensitivity = 0.12f;
        [Tooltip("If true, wheel does not zoom while the pointer is over UI.")]
        [SerializeField] private bool ignoreMouseZoomOverUi = true;

        private UnityEngine.Camera cam;
        private float currentScale = 1f;
        private float scaleVelocity;

        // Galactic zoom state
        private bool galacticZoomActive;
        private bool galacticZoomReturning;
        private float galacticZoomElapsed;
        private float galacticZoomStartSize;

        /// <summary>0 = default ship zoom; 1 = max manual zoom out.</summary>
        private float manualZoomT;

        /// <summary>Gameplay zoom distance after manual wheel and galactic animation (ortho size or perspective equivalent).</summary>
        private float lastActiveZoomDistance;

        /// <summary>Decaying hit impulse (single collisions).</summary>
        private float collisionShakeIntensity;
        /// <summary>Sustained level while grinding (0..1); cleared by Starship when contacts end.</summary>
        private float rammingShakeDrive;
        private float collisionShakeSeed;

        private float GetManualZoomedDistance(float defaultDistance)
        {
            float maxDistance = Mathf.Max(maxManualZoomOutDistance, defaultDistance);
            return Mathf.Lerp(defaultDistance, maxDistance, manualZoomT);
        }

        /// <summary>Linear zoom multiplier from <see cref="zoomScaleAtLevel1"/> at min level to <see cref="zoomScaleAtMaxShipLevel"/> at max level.</summary>
        private float GetZoomScaleForShipLevel(int shipLevel)
        {
            int minLevel = Mathf.Max(1, minShipLevelForZoom);
            int maxLevel = Mathf.Max(minLevel + 1, maxShipLevelForZoom);
            shipLevel = Mathf.Clamp(shipLevel, minLevel, maxLevel);
            float t = (shipLevel - minLevel) / (float)(maxLevel - minLevel);
            return Mathf.Lerp(zoomScaleAtLevel1, zoomScaleAtMaxShipLevel, t);
        }

        /// <summary>Inspector toggle: whether collision feedback may apply camera shake.</summary>
        public bool IsCollisionCameraShakeEnabled => collisionCameraShakeEnabled;

        private void Awake()
        {
            cam = GetComponent<UnityEngine.Camera>();
            if (cam == null)
            {
                cam = gameObject.AddComponent<UnityEngine.Camera>();
            }

            cam.orthographic = false;
            cam.fieldOfView = 45f;

            if (spaceBackground == null)
            {
                spaceBackground = FindFirstObjectByType<ScrollingSpaceBackground>();
            }

            collisionShakeSeed = Random.value * 100f;

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

            float targetScale = GetZoomScaleForShipLevel(level);

            if (distanceChangeSmoothTime > 0f)
                currentScale = Mathf.SmoothDamp(currentScale, targetScale, ref scaleVelocity, distanceChangeSmoothTime);
            else
                currentScale = targetScale;

            float defaultDistance = zoomDistanceAtReferenceLevel * currentScale;
            float activeZoomDistance = defaultDistance;

            if (!galacticZoomActive)
            {
                if (mouseZoomEnabled && target != null)
                {
                    bool allowWheel = !ignoreMouseZoomOverUi
                        || EventSystem.current == null
                        || !EventSystem.current.IsPointerOverGameObject();
                    if (allowWheel)
                    {
                        float scroll;
#if ENABLE_INPUT_SYSTEM
                        scroll = Mouse.current != null ? Mouse.current.scroll.ReadValue().y : 0f;
#else
                        scroll = UnityEngine.Input.mouseScrollDelta.y;
#endif
                        if (Mathf.Abs(scroll) > 0.0001f)
                        {
                            // Scroll up (positive) = zoom in toward default; scroll down = zoom out toward max.
                            manualZoomT = Mathf.Clamp01(
                                manualZoomT - (scroll / 120f) * mouseWheelZoomSensitivity);
                        }
                    }
                }

                activeZoomDistance = GetManualZoomedDistance(defaultDistance);
            }
            else
            {
                galacticZoomElapsed += Time.deltaTime;

                float gameplayDistance = GetManualZoomedDistance(defaultDistance);
                // Target zoomed-out size is halfway between gameplay zoom (including manual wheel) and the far-map size.
                float halfwayOutSize = Mathf.Lerp(gameplayDistance, galacticZoomDistance, 0.5f);

                if (!galacticZoomReturning)
                {
                    float tOut = galacticZoomOutDuration > 0.0001f
                        ? Mathf.Clamp01(galacticZoomElapsed / galacticZoomOutDuration)
                        : 1f;
                    activeZoomDistance = Mathf.Lerp(galacticZoomStartSize, halfwayOutSize, tOut);
                }
                else
                {
                    float tIn = galacticZoomInDuration > 0.0001f
                        ? Mathf.Clamp01(galacticZoomElapsed / galacticZoomInDuration)
                        : 1f;
                    activeZoomDistance = Mathf.Lerp(galacticZoomStartSize, gameplayDistance, tIn);

                    if (tIn >= 1f - 0.0001f)
                    {
                        galacticZoomActive = false;
                        galacticZoomReturning = false;
                        if (spaceBackground != null)
                            spaceBackground.SetTemporarilyHidden(false);
                    }
                }
            }

            lastActiveZoomDistance = activeZoomDistance;

            if (cam.orthographic)
                cam.orthographicSize = activeZoomDistance;

            // Perspective zoom is driven by camera height; orthographic zoom uses orthographicSize instead.
            float zoomRatio = defaultDistance > 0.0001f ? activeZoomDistance / defaultDistance : 1f;
            float offsetY = offsetAtReferenceLevel.y * currentScale;
            if (!cam.orthographic)
                offsetY *= zoomRatio;

            Vector3 offset = new Vector3(
                offsetAtReferenceLevel.x,
                offsetY,
                offsetAtReferenceLevel.z);

            // Lock camera to ship - ship is always in wrapped coordinates, so just follow directly
            Vector3 targetPosition = target.position + offset;

            float impulsePow = collisionShakeIntensity > 0.0001f
                ? Mathf.Pow(collisionShakeIntensity, collisionShakeSmoothingExponent)
                : 0f;
            float combinedShake = Mathf.Max(impulsePow, rammingShakeDrive);
            if (combinedShake > 0.0001f)
            {
                float ft = Time.time * collisionShakeFrequency;
                Vector3 localJitter = new Vector3(
                    collisionShakeMaxTranslation.x * (Mathf.PerlinNoise(collisionShakeSeed, ft) * 2f - 1f),
                    collisionShakeMaxTranslation.y * (Mathf.PerlinNoise(collisionShakeSeed + 1f, ft) * 2f - 1f),
                    collisionShakeMaxTranslation.z * (Mathf.PerlinNoise(collisionShakeSeed + 2f, ft) * 2f - 1f)
                ) * combinedShake;
                targetPosition += transform.rotation * localJitter;
                if (collisionShakeIntensity > 0.0001f)
                {
                    collisionShakeIntensity = Mathf.Clamp01(
                        collisionShakeIntensity - collisionShakeRecoverPerSecond * Time.deltaTime);
                }
            }

            transform.position = targetPosition;
        }

        /// <summary>Impulse shake for the local player (typ. 0.05–0.35). Stacks with sustained ramming via max.</summary>
        public void ApplyCollisionShake(float amount01)
        {
            if (!collisionCameraShakeEnabled) return;
            collisionShakeIntensity = Mathf.Max(collisionShakeIntensity, Mathf.Clamp01(amount01));
        }

        /// <summary>Sustained shake while grinding; set every physics step from owner client, or 0 when ramming stops.</summary>
        public void SetRammingShakeDrive(float amount01)
        {
            if (!collisionCameraShakeEnabled) return;
            rammingShakeDrive = Mathf.Clamp01(amount01);
        }

        /// <summary>Maps asteroid impact severity (0–1, same curve as collision VFX) to impulse shake strength.</summary>
        public float EvaluateRamImpactShake(float impactSeverity01) =>
            Mathf.Lerp(ramImpactShakeMin, ramImpactShakeMax, Mathf.Clamp01(impactSeverity01));

        /// <summary>Maps grind severity (0–1) to sustained shake drive while pushing into an asteroid.</summary>
        public float EvaluateRamGrindShake(float grindSeverity01) =>
            Mathf.Lerp(ramGrindShakeMin, ramGrindShakeMax, Mathf.Clamp01(grindSeverity01));

        /// <summary>Maps asteroid gem size (typically 1–70) to breakup impulse when grinding through destruction.</summary>
        public float EvaluateRamDestroyShake(float asteroidGemSize)
        {
            float sizeMin = Mathf.Min(ramDestroyShakeSizeMin, ramDestroyShakeSizeMax);
            float sizeMax = Mathf.Max(ramDestroyShakeSizeMin, ramDestroyShakeSizeMax);
            float sizeT = Mathf.InverseLerp(sizeMin, sizeMax, asteroidGemSize);
            return Mathf.Lerp(ramDestroyShakeMin, ramDestroyShakeMax, sizeT);
        }

        /// <summary>
        /// Hides the starfield while the map loads / team menu uses the zoomed-out camera (avoids a tiny quad at extreme zoom distance).
        /// Call with <c>false</c> when releasing to normal ship follow.
        /// </summary>
        public void SetSpaceBackgroundHiddenForLoadingState(bool hidden)
        {
            if (spaceBackground != null)
                spaceBackground.SetTemporarilyHidden(hidden);
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

        /// <summary>Begin galactic zoom out toward a large camera distance.</summary>
        public void StartGalacticZoomOut()
        {
            if (!galacticZoomEnabled)
                return;

            if (cam == null) return;

            galacticZoomActive = true;
            galacticZoomReturning = false;
            galacticZoomElapsed = 0f;
            galacticZoomStartSize = GetActiveZoomDistanceForGalacticTransition();

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
            galacticZoomStartSize = GetActiveZoomDistanceForGalacticTransition();
        }

        private float GetActiveZoomDistanceForGalacticTransition()
        {
            if (cam == null)
                return zoomDistanceAtReferenceLevel * currentScale;

            if (cam.orthographic)
                return cam.orthographicSize;

            if (lastActiveZoomDistance > 0.0001f)
                return lastActiveZoomDistance;

            return GetManualZoomedDistance(zoomDistanceAtReferenceLevel * currentScale);
        }
    }
}
