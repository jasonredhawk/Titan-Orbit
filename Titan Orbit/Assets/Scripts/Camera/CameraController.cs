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
    [DefaultExecutionOrder(32500)] // After ToroidalRenderer (32000) and PlanetGemMoon (32100)
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
        [Tooltip("Smooth follow on XZ only while coasting (not moving) inside a friendly gem-moon orbit shell. 0 = hard lock everywhere.")]
        [Min(0f)]
        [SerializeField] private float gemMoonOrbitFollowSmoothTime = 0.1f;
        [Tooltip("Planar speed (m/s) at or below this starts gem-moon coast smoothing (hysteresis enter).")]
        [Min(0.05f)]
        [SerializeField] private float gemMoonOrbitSmoothingEnterSpeed = 0.35f;
        [Tooltip("Planar speed (m/s) at or above this ends gem-moon coast smoothing (hysteresis exit).")]
        [Min(0.05f)]
        [SerializeField] private float gemMoonOrbitSmoothingExitSpeed = 0.6f;
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
        [Tooltip("Shake strength when an asteroid is destroyed while ramming/grinding (0–1), scaled by gem size.")]
        [SerializeField] private float ramDestroyShakeMin = 0.25f;
        [SerializeField] private float ramDestroyShakeMax = 0.85f;
        [Tooltip("How long destroy shake runs (seconds).")]
        [SerializeField, Min(0.05f)] private float ramDestroyShakeDurationSeconds = 0.5f;
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

        [Header("Theatrical Idle Camera")]
        [Tooltip("When the local player stops moving and firing for the idle duration, orbit the ship with a cinematic camera.")]
        [SerializeField] private bool theatricalModeEnabled = true;
        [Tooltip("Seconds without move/fire input (and low drift speed) before theatrical mode begins.")]
        [Min(0.5f)]
        [SerializeField] private float theatricalIdleDurationSeconds = 3f;
        [Tooltip("Planar speed (m/s) at or below this counts as not moving while inputs are released.")]
        [Min(0f)]
        [SerializeField] private float theatricalIdleMaxPlanarSpeed = 0.35f;
        [Tooltip("Seconds to gently travel one full spline pass (6× slower epic pacing).")]
        [Min(8f)]
        [SerializeField] private float theatricalPathDurationMinSeconds = 720f;
        [Min(8f)]
        [SerializeField] private float theatricalPathDurationMaxSeconds = 1080f;
        [Tooltip("Seconds to blend from gameplay top-down into the theatrical orbit pose.")]
        [Min(0f)]
        [SerializeField] private float theatricalEnterBlendDuration = 3.5f;
        [Tooltip("Look-at focus smoothing while orbiting (higher = slower, more cinematic).")]
        [Min(0.05f)]
        [SerializeField] private float theatricalLookSmoothTime = 1.8f;
        [Tooltip("Rotation smoothing while orbiting (higher = slower).")]
        [Min(0.05f)]
        [SerializeField] private float theatricalRotationSmoothTime = 1.3f;
        [Tooltip("FOV zoom smoothing while orbiting (higher = slower).")]
        [Min(0.05f)]
        [SerializeField] private float theatricalFovSmoothTime = 4f;
        [Tooltip("Random points on each closed spline loop (plus the live camera anchor).")]
        [Range(4, 14)]
        [SerializeField] private int theatricalWaypointCount = 8;
        [Tooltip("Min elevation in degrees relative to the ship focus (− = below horizon).")]
        [SerializeField] private float theatricalMinElevationDeg = -32f;
        [Tooltip("Max elevation in degrees relative to the ship focus.")]
        [SerializeField] private float theatricalMaxElevationDeg = 52f;
        [Tooltip("Standoff radius multipliers on ship visual size.")]
        [SerializeField] private float theatricalRadiusMinMultiplier = 2.4f;
        [SerializeField] private float theatricalRadiusMaxMultiplier = 5.2f;
        [Tooltip("Extra scale on orbit standoff (1.5 = default range; was 3× before halving max zoom-out).")]
        [Min(0.5f)]
        [SerializeField] private float theatricalOrbitStandoffMultiplier = 1.5f;
        [Tooltip("Perspective FOV at closest orbit (zoomed in).")]
        [Range(18f, 55f)]
        [SerializeField] private float theatricalFovMin = 28f;
        [Tooltip("Perspective FOV at farthest orbit (zoomed out).")]
        [Range(18f, 70f)]
        [SerializeField] private float theatricalFovMax = 48f;
        [Tooltip("Gameplay perspective FOV restored after theatrical mode.")]
        [Range(30f, 70f)]
        [SerializeField] private float gameplayFieldOfView = 45f;

        private UnityEngine.Camera cam;
        private Starship targetShip;
        private Vector3 smoothedFollowXZ;
        private bool hasSmoothedFollowXZ;
        private Vector3 followVelocity;
        private bool gemMoonOrbitSmoothingActive;
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
        /// <summary>Fixed-duration shake (e.g. asteroid destroy while ramming).</summary>
        private float timedShakeIntensity;
        private float timedShakeEndTime;
        private float collisionShakeSeed;

        private readonly CameraTheatricalOrbit theatricalOrbit = new CameraTheatricalOrbit();
        private bool theatricalModeActive;
        private float theatricalIdleTimer;
        private float theatricalSavedGameplayZoomDistance;
        private float theatricalFovVelocity;
        private Vector3 theatricalLookVelocity;
        private float theatricalSmoothedFov;
        private bool hasTheatricalSmoothedFov;
        private Vector3 theatricalSmoothedLookTarget;
        private bool hasTheatricalSmoothedLookTarget;
        private Quaternion theatricalFrozenShipRotation = Quaternion.identity;
        private bool theatricalOrbitInitialized;
        private float theatricalEnterBlendElapsed;
        private bool theatricalCapturingEnterBlendStart;
        private Vector3 theatricalBlendStartPosition;
        private Quaternion theatricalBlendStartRotation = Quaternion.identity;
        private float theatricalBlendStartFov;
        private Quaternion theatricalSmoothedRotation = Quaternion.identity;
        private bool hasTheatricalSmoothedRotation;
        private bool wasTargetShipDead;
        private CameraClearFlags gameplayClearFlags = CameraClearFlags.SolidColor;

        /// <summary>True while the cinematic orbit is active.</summary>
        public bool IsTheatricalCameraEngaged => theatricalModeActive;

        /// <summary>True only during the orbit itself — rotation unlocks immediately when the player takes control.</summary>
        public bool IsTheatricalShipRotationLocked => theatricalModeActive;

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
            cam.fieldOfView = gameplayFieldOfView;
            gameplayClearFlags = cam.clearFlags;

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

            bool playerActivelyPlaying = IsPlayerActivelyMovingOrFiring();

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

            if (!galacticZoomActive && !theatricalModeActive)
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
            else if (galacticZoomActive)
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
            else
            {
                // Theatrical orbit only — preserve gameplay zoom distance (height) for a correct return.
                activeZoomDistance = theatricalSavedGameplayZoomDistance > 0.0001f
                    ? theatricalSavedGameplayZoomDistance
                    : GetManualZoomedDistance(defaultDistance);
            }

            bool wasTheatricalEngaged = theatricalModeActive;
            UpdateTheatricalModeState(playerActivelyPlaying);
            if (!wasTheatricalEngaged && theatricalModeActive)
                theatricalSavedGameplayZoomDistance = activeZoomDistance;

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

            Vector3 followWorld = targetShip != null
                ? targetShip.GetCameraFollowWorldPosition()
                : target.position;

            Vector3 followXZ = new Vector3(followWorld.x, 0f, followWorld.z);
            bool useGemMoonOrbitSmoothing = gemMoonOrbitFollowSmoothTime > 0.0001f
                && UpdateGemMoonOrbitSmoothingActive();

            if (!useGemMoonOrbitSmoothing)
            {
                smoothedFollowXZ = followXZ;
                hasSmoothedFollowXZ = true;
                followVelocity = Vector3.zero;
            }
            else if (!hasSmoothedFollowXZ)
            {
                smoothedFollowXZ = followXZ;
                hasSmoothedFollowXZ = true;
            }
            else
            {
                smoothedFollowXZ = Vector3.SmoothDamp(
                    smoothedFollowXZ,
                    followXZ,
                    ref followVelocity,
                    gemMoonOrbitFollowSmoothTime,
                    Mathf.Infinity,
                    Time.deltaTime);
            }

            Vector3 targetPosition = new Vector3(
                smoothedFollowXZ.x,
                followWorld.y,
                smoothedFollowXZ.z) + offset;

            float impulsePow = collisionShakeIntensity > 0.0001f
                ? Mathf.Pow(collisionShakeIntensity, collisionShakeSmoothingExponent)
                : 0f;
            float timedShake = 0f;
            if (Time.time < timedShakeEndTime)
                timedShake = timedShakeIntensity;
            else if (timedShakeIntensity > 0f)
            {
                timedShakeIntensity = 0f;
                timedShakeEndTime = 0f;
            }

            float combinedShake = Mathf.Max(impulsePow, timedShake);
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

            Quaternion finalRotation = Quaternion.Euler(90f, 0f, 0f);
            float finalFov = gameplayFieldOfView;

            if (theatricalModeActive)
            {
                ApplyTheatricalCamera(
                    targetPosition,
                    followWorld,
                    out Vector3 finalPosition,
                    out finalRotation,
                    out finalFov,
                    playerActivelyPlaying);
                targetPosition = finalPosition;
            }

            transform.rotation = finalRotation;
            transform.position = targetPosition;
            if (cam != null && !cam.orthographic)
                cam.fieldOfView = finalFov;

            UpdateTheatricalSpaceBackgroundVisibility();
        }

        private void UpdateTheatricalSpaceBackgroundVisibility()
        {
            if (theatricalModeActive)
            {
                // Perspective orbit cannot use the flat scrolling quad; show RenderSettings skybox instead.
                bool useSkybox = RenderSettings.skybox != null;
                if (spaceBackground != null)
                    spaceBackground.SetTemporarilyHidden(useSkybox);

                if (cam != null)
                    cam.clearFlags = useSkybox ? CameraClearFlags.Skybox : gameplayClearFlags;
                return;
            }

            if (spaceBackground != null && !galacticZoomActive)
                spaceBackground.SetTemporarilyHidden(false);

            if (cam != null)
                cam.clearFlags = gameplayClearFlags;
        }

        private bool IsPlayerActivelyMovingOrFiring()
        {
            if (targetShip == null)
                return false;

            if (targetShip.IsDead)
                return false;

            if (targetShip.IsInteractingWithOrbitStationMenu)
                return false;

            if (targetShip.IsMoveForwardPressedForGemMoonLanding
                || targetShip.IsShootPressedForGemMoonLanding)
                return true;

            return targetShip.GetPlanarSpeedWorld() > theatricalIdleMaxPlanarSpeed;
        }

        private void UpdateTheatricalModeState(bool playerActivelyPlaying)
        {
            bool targetDead = targetShip != null && targetShip.IsDead;
            if (wasTargetShipDead && !targetDead && theatricalModeActive)
                EndTheatricalMode();
            wasTargetShipDead = targetDead;

            if (targetDead)
            {
                if (galacticZoomActive)
                {
                    galacticZoomActive = false;
                    galacticZoomReturning = false;
                    if (spaceBackground != null)
                        spaceBackground.SetTemporarilyHidden(false);
                }

                theatricalIdleTimer = 0f;
                if (theatricalModeEnabled && !theatricalModeActive)
                    BeginTheatricalMode();
                return;
            }

            if (!theatricalModeEnabled || galacticZoomActive)
            {
                theatricalIdleTimer = 0f;
                if (theatricalModeActive)
                    EndTheatricalMode();
                return;
            }

            if (playerActivelyPlaying)
            {
                theatricalIdleTimer = 0f;
                if (theatricalModeActive)
                    EndTheatricalMode();
                return;
            }

            if (theatricalModeActive)
                return;

            theatricalIdleTimer += Time.deltaTime;
            if (theatricalIdleTimer >= theatricalIdleDurationSeconds)
                BeginTheatricalMode();
        }

        private void BeginTheatricalMode()
        {
            theatricalModeActive = true;
            theatricalIdleTimer = 0f;
            theatricalOrbitInitialized = false;
            hasTheatricalSmoothedLookTarget = false;
            hasTheatricalSmoothedFov = false;
            hasTheatricalSmoothedRotation = false;
            theatricalEnterBlendElapsed = 0f;
            theatricalCapturingEnterBlendStart = true;
            theatricalFrozenShipRotation = target.rotation;

            TryGetShipVisualFocus(target, out _, out float radius);
            theatricalOrbit.SetCharacteristicRadius(radius);
        }

        private void EnsureTheatricalOrbitInitialized(Vector3 cameraWorldPosition, Vector3 focus, float radius)
        {
            if (theatricalOrbitInitialized)
                return;

            theatricalOrbit.SetCharacteristicRadius(radius);
            float standoff = Mathf.Max(0.5f, theatricalOrbitStandoffMultiplier);
            theatricalOrbit.ConfigurePathGeneration(
                theatricalWaypointCount,
                theatricalMinElevationDeg,
                theatricalMaxElevationDeg,
                theatricalRadiusMinMultiplier * standoff,
                theatricalRadiusMaxMultiplier * standoff,
                theatricalPathDurationMinSeconds,
                theatricalPathDurationMaxSeconds);

            theatricalOrbit.BeginPathFromCamera(
                cameraWorldPosition,
                focus,
                theatricalFrozenShipRotation);
            theatricalOrbitInitialized = true;
        }

        private void EndTheatricalMode()
        {
            if (!theatricalModeActive)
                return;

            theatricalModeActive = false;
            theatricalOrbitInitialized = false;
            theatricalIdleTimer = 0f;
            hasTheatricalSmoothedLookTarget = false;
            hasTheatricalSmoothedFov = false;
            hasTheatricalSmoothedRotation = false;
            theatricalEnterBlendElapsed = 0f;
            theatricalCapturingEnterBlendStart = false;
        }

        private void ApplyTheatricalCamera(
            Vector3 gameplayPosition,
            Vector3 followWorld,
            out Vector3 finalPosition,
            out Quaternion finalRotation,
            out float finalFov,
            bool playerActivelyPlaying)
        {
            finalPosition = gameplayPosition;
            finalRotation = Quaternion.Euler(90f, 0f, 0f);
            finalFov = gameplayFieldOfView;

            TryGetShipVisualFocus(target, out Vector3 boundsFocus, out float radius);
            float focusYOffset = boundsFocus.y - target.position.y;
            Vector3 focus = new Vector3(followWorld.x, followWorld.y + focusYOffset, followWorld.z);
            theatricalOrbit.SetCharacteristicRadius(radius);

            if (theatricalCapturingEnterBlendStart)
            {
                theatricalBlendStartPosition = gameplayPosition;
                theatricalBlendStartRotation = Quaternion.Euler(90f, 0f, 0f);
                theatricalBlendStartFov = cam != null ? cam.fieldOfView : gameplayFieldOfView;
                theatricalCapturingEnterBlendStart = false;
            }

            if (!theatricalOrbitInitialized)
            {
                EnsureTheatricalOrbitInitialized(gameplayPosition, focus, radius);
            }

            bool enterBlendActive = theatricalEnterBlendDuration > 0.0001f
                && theatricalEnterBlendElapsed < theatricalEnterBlendDuration;
            if (enterBlendActive)
                theatricalEnterBlendElapsed += Time.deltaTime;
            else
                theatricalOrbit.Advance(Time.deltaTime, focus, theatricalFrozenShipRotation);

            theatricalOrbit.Sample(
                focus,
                theatricalFrozenShipRotation,
                out Vector3 orbitPosition,
                out Vector3 lookTarget,
                out float zoomT);

            if (!hasTheatricalSmoothedLookTarget)
            {
                theatricalSmoothedLookTarget = lookTarget;
                hasTheatricalSmoothedLookTarget = true;
            }

            theatricalSmoothedLookTarget = Vector3.SmoothDamp(
                theatricalSmoothedLookTarget,
                lookTarget,
                ref theatricalLookVelocity,
                theatricalLookSmoothTime,
                Mathf.Infinity,
                Time.deltaTime);

            Quaternion targetOrbitRotation = TryLookAtRotation(
                orbitPosition,
                theatricalSmoothedLookTarget,
                theatricalBlendStartRotation);

            if (!hasTheatricalSmoothedRotation)
            {
                theatricalSmoothedRotation = theatricalBlendStartRotation;
                hasTheatricalSmoothedRotation = true;
            }

            float rotationBlend = 1f - Mathf.Exp(
                -Time.deltaTime / Mathf.Max(0.05f, theatricalRotationSmoothTime));
            theatricalSmoothedRotation = Quaternion.Slerp(
                theatricalSmoothedRotation,
                targetOrbitRotation,
                rotationBlend);

            float targetOrbitFov = Mathf.Lerp(theatricalFovMax, theatricalFovMin, zoomT);
            if (!hasTheatricalSmoothedFov)
            {
                theatricalSmoothedFov = theatricalBlendStartFov;
                hasTheatricalSmoothedFov = true;
            }

            theatricalSmoothedFov = Mathf.SmoothDamp(
                theatricalSmoothedFov,
                targetOrbitFov,
                ref theatricalFovVelocity,
                theatricalFovSmoothTime);

            if (enterBlendActive)
            {
                float blendT = Mathf.SmoothStep(
                    0f,
                    1f,
                    Mathf.Clamp01(theatricalEnterBlendElapsed / theatricalEnterBlendDuration));

                float standoff = Mathf.Max(0.5f, theatricalOrbitStandoffMultiplier);
                Quaternion invShipRot = Quaternion.Inverse(theatricalFrozenShipRotation);
                Vector3 anchorLocal = invShipRot * (theatricalBlendStartPosition - focus);
                Vector3 pullbackLocal = CameraTheatricalOrbit.ComputePullbackLocal(
                    anchorLocal,
                    radius,
                    theatricalRadiusMaxMultiplier * standoff);
                Vector3 pullbackPosition = focus + theatricalFrozenShipRotation * pullbackLocal;

                const float pullbackPhaseEnd = 0.55f;
                if (blendT < pullbackPhaseEnd)
                {
                    float phaseT = Mathf.SmoothStep(0f, 1f, blendT / pullbackPhaseEnd);
                    finalPosition = Vector3.Lerp(theatricalBlendStartPosition, pullbackPosition, phaseT);
                    finalRotation = theatricalBlendStartRotation;
                    finalFov = Mathf.Lerp(theatricalBlendStartFov, theatricalFovMax, phaseT);
                }
                else
                {
                    float phaseT = Mathf.SmoothStep(
                        0f,
                        1f,
                        (blendT - pullbackPhaseEnd) / (1f - pullbackPhaseEnd));
                    finalPosition = orbitPosition;
                    finalRotation = Quaternion.Slerp(
                        theatricalBlendStartRotation,
                        theatricalSmoothedRotation,
                        phaseT);
                    finalFov = Mathf.Lerp(theatricalFovMax, theatricalSmoothedFov, phaseT);
                }
            }
            else
            {
                finalPosition = orbitPosition;
                finalRotation = theatricalSmoothedRotation;
                finalFov = theatricalSmoothedFov;
            }
        }

        /// <summary>
        /// Stable look-at when the camera is nearly straight above/below the focus
        /// (LookRotation with forward parallel to world up spins unpredictably).
        /// </summary>
        private static Quaternion TryLookAtRotation(
            Vector3 from,
            Vector3 to,
            Quaternion fallbackRotation)
        {
            Vector3 forward = to - from;
            if (forward.sqrMagnitude < 0.0001f)
                return fallbackRotation;

            forward.Normalize();
            if (Mathf.Abs(Vector3.Dot(forward, Vector3.up)) > 0.98f)
                return Quaternion.Euler(forward.y > 0f ? -90f : 90f, 0f, 0f);

            return Quaternion.LookRotation(forward, Vector3.up);
        }

        private static bool TryGetShipVisualFocus(Transform shipRoot, out Vector3 lookTarget, out float characteristicRadius)
        {
            lookTarget = shipRoot.position;
            characteristicRadius = 4f;

            if (shipRoot == null)
                return false;

            var renderers = shipRoot.GetComponentsInChildren<Renderer>();
            Bounds? bounds = null;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer r = renderers[i];
                if (r == null || !r.enabled || r is ParticleSystemRenderer)
                    continue;

                if (!bounds.HasValue)
                    bounds = r.bounds;
                else
                {
                    Bounds b = bounds.Value;
                    b.Encapsulate(r.bounds);
                    bounds = b;
                }
            }

            if (!bounds.HasValue)
                return false;

            Bounds wb = bounds.Value;
            lookTarget = wb.center + Vector3.up * (wb.extents.y * 0.1f);
            characteristicRadius = Mathf.Max(wb.extents.x, wb.extents.y, wb.extents.z) * 2.8f;
            characteristicRadius = Mathf.Max(3f, characteristicRadius);
            return true;
        }

        /// <summary>Impulse shake for the local player (decays over time).</summary>
        public void ApplyCollisionShake(float amount01)
        {
            if (!collisionCameraShakeEnabled) return;
            collisionShakeIntensity = Mathf.Max(collisionShakeIntensity, Mathf.Clamp01(amount01));
        }

        /// <summary>Constant-intensity shake for a fixed duration (used for asteroid destroy while ramming/grinding).</summary>
        public void ApplyTimedCollisionShake(float amount01, float durationSeconds)
        {
            if (!collisionCameraShakeEnabled || durationSeconds <= 0f) return;
            float a = Mathf.Clamp01(amount01);
            float end = Time.time + durationSeconds;
            if (Time.time >= timedShakeEndTime)
            {
                timedShakeIntensity = a;
                timedShakeEndTime = end;
            }
            else
            {
                timedShakeIntensity = Mathf.Max(timedShakeIntensity, a);
                timedShakeEndTime = Mathf.Max(timedShakeEndTime, end);
            }
        }

        /// <summary>Maps asteroid gem size (typically 1–70) to destroy shake strength (0–1).</summary>
        public float EvaluateRamDestroyShake(float asteroidGemSize)
        {
            float sizeMin = Mathf.Min(ramDestroyShakeSizeMin, ramDestroyShakeSizeMax);
            float sizeMax = Mathf.Max(ramDestroyShakeSizeMin, ramDestroyShakeSizeMax);
            float sizeT = Mathf.InverseLerp(sizeMin, sizeMax, asteroidGemSize);
            return Mathf.Lerp(ramDestroyShakeMin, ramDestroyShakeMax, sizeT);
        }

        public float RamDestroyShakeDurationSeconds => ramDestroyShakeDurationSeconds;

        /// <summary>
        /// Hides the starfield while the map loads / team menu uses the zoomed-out camera (avoids a tiny quad at extreme zoom distance).
        /// Call with <c>false</c> when releasing to normal ship follow.
        /// </summary>
        public void SetSpaceBackgroundHiddenForLoadingState(bool hidden)
        {
            if (spaceBackground != null)
                spaceBackground.SetTemporarilyHidden(hidden);
        }

        private bool UpdateGemMoonOrbitSmoothingActive()
        {
            if (targetShip == null
                || targetShip.GemMoonDocked
                || !targetShip.IsInsideFriendlyGemMoonOrbitZone())
            {
                gemMoonOrbitSmoothingActive = false;
                return false;
            }

            if (targetShip.IsMoveForwardPressedForGemMoonLanding
                || targetShip.IsShootPressedForGemMoonLanding)
            {
                gemMoonOrbitSmoothingActive = false;
                return false;
            }

            float speed = targetShip.GetPlanarSpeedWorld();
            float enterSpeed = Mathf.Max(0.05f, gemMoonOrbitSmoothingEnterSpeed);
            float exitSpeed = Mathf.Max(enterSpeed + 0.05f, gemMoonOrbitSmoothingExitSpeed);

            if (!gemMoonOrbitSmoothingActive)
            {
                if (speed <= enterSpeed)
                    gemMoonOrbitSmoothingActive = true;
            }
            else if (speed >= exitSpeed)
                gemMoonOrbitSmoothingActive = false;

            return gemMoonOrbitSmoothingActive;
        }

        private bool hasEverSetFollowTarget;

        public void SetTarget(Transform newTarget)
        {
            target = newTarget;
            targetShip = newTarget != null ? newTarget.GetComponent<Starship>() : null;
            hasSmoothedFollowXZ = false;
            followVelocity = Vector3.zero;
            gemMoonOrbitSmoothingActive = false;
            manualZoomT = 0f;
            galacticZoomActive = false;
            galacticZoomReturning = false;
            galacticZoomElapsed = 0f;
            theatricalModeActive = false;
            theatricalIdleTimer = 0f;
            theatricalSavedGameplayZoomDistance = 0f;
            theatricalOrbitInitialized = false;
            hasTheatricalSmoothedLookTarget = false;
            hasTheatricalSmoothedFov = false;
            hasTheatricalSmoothedRotation = false;
            theatricalEnterBlendElapsed = 0f;
            theatricalCapturingEnterBlendStart = false;
            wasTargetShipDead = false;

            int level = targetShip != null ? targetShip.ShipLevel : minShipLevelForZoom;
            float levelScale = GetZoomScaleForShipLevel(level);

            bool playSpawnIntro = newTarget != null && spawnZoomScale > 1f && !hasEverSetFollowTarget;
            if (newTarget != null)
                hasEverSetFollowTarget = true;

            if (playSpawnIntro)
            {
                currentScale = spawnZoomScale;
            }
            else
            {
                currentScale = levelScale;
            }
            scaleVelocity = 0f;
        }

        /// <summary>Begin galactic zoom out toward a large camera distance.</summary>
        public void StartGalacticZoomOut()
        {
            if (!galacticZoomEnabled)
                return;

            if (cam == null) return;

            if (theatricalModeActive)
                EndTheatricalMode();

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

            if (theatricalModeActive)
                EndTheatricalMode();

            galacticZoomReturning = true;
            galacticZoomElapsed = 0f;
            galacticZoomStartSize = GetActiveZoomDistanceForGalacticTransition();
        }

        /// <summary>Ends theatrical orbit instantly and restores gameplay follow.</summary>
        public void TriggerTheatricalReturn() => EndTheatricalMode();

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
