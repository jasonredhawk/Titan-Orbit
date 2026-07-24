using TitanOrbit.ECS;
using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Client-side moon landing presentation: animates the ship proxy from flight pose onto the
    /// closest moon-surface point (any latitude), then spins that contact with the moon mesh, and
    /// reverses when thrusting away. Reads <see cref="ShipMoonDockState"/> from the visualization
    /// ECS world. When active, <see cref="EcsWorldVisualizer"/> skips transform sync
    /// (<see cref="ShouldSkipTransformSync"/>).
    /// Provides a local camera follow override via <see cref="TryGetLocalFollowPosition"/> —
    /// the gameplay camera stays on the ship hull through approach, surface spin, and takeoff so the
    /// view rides with the docked ship instead of locking to the moon center. Cosmetic only; server
    /// dock state is owned by <see cref="ShipMoonDockSystem"/>.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public class ShipMoonDockVisualApplier : MonoBehaviour
    {
        /// <summary>
        /// Cosmetic hull spin around the moon (deg/sec). Must match
        /// <see cref="PlanetGemMoonVisualProxy"/> mesh spin so the ship looks glued to the surface.
        /// </summary>
        const float SpinSpeedDegPerSec = 9f;

        /// <summary>Proxy scale multiplier when parked on the moon surface.</summary>
        const float DockScaleAtSurface = 0.24f;

        /// <summary>Landing and takeoff cinematic duration (seconds).</summary>
        const float LandingDurationSeconds = 1f;

        /// <summary>
        /// Soft follow rate when easing the camera onto a new dock target (1/s). [TITAN-ORBIT]
        /// Presentation only — not a second flight motor. Used if a soft catch-up is requested;
        /// ship follow during dock normally hard-locks so surface spin stays visible.
        /// </summary>
        const float DockCameraFollowCatchUp = 14f;

        Entity _shipEntity;
        float _baselineScale = 1f;
        bool _wasControllingTransform;
        bool _wasLandingVisualActive;
        bool _wasMoonDockEngaged;

        // Landing animation start pose (captured when each landing sequence begins).
        Vector3 _landingStartPosition;
        Quaternion _landingStartRotation;
        float _landingStartScale;

        // Unit vector moon-center → hull contact (keeps latitude; spins around moon axis while docked).
        Vector3 _landingSurfaceDir;

        // Takeoff animation.
        bool _isTakeoffAnimating;
        float _takeoffProgress;
        Vector3 _takeoffStartPosition;
        Quaternion _takeoffStartRotation;
        float _takeoffStartScale;

        // --- Camera follow override (ship hull while dock cinematic owns the proxy) ---
        bool _dockCameraFollowValid;
        Vector3 _dockCameraFollowPosition;

        /// <summary>When true, EcsWorldVisualizer should not overwrite this proxy's transform.</summary>
        public bool ShouldSkipTransformSync => _isTakeoffAnimating || _wasControllingTransform;

        static ShipMoonDockVisualApplier s_localInstance;

        /// <summary>
        /// Visual follow point for the local player while landing/docked/taking off.
        /// Returns the ship proxy position so <see cref="CameraFollowEcs"/> rides with the hull
        /// (including moon surface spin) instead of locking to the moon center or flickering onto
        /// the planar ECS attach pose beside the moon.
        /// </summary>
        public static bool TryGetLocalFollowPosition(out Vector3 position)
        {
            if (s_localInstance == null)
            {
                position = default;
                return false;
            }

            // [HYBRID] Published ship-hull anchor while dock cinematic is driving presentation.
            if (s_localInstance._dockCameraFollowValid)
            {
                position = s_localInstance._dockCameraFollowPosition;
                return true;
            }

            // Pre-anchor frames: follow the proxy hull itself.
            if (s_localInstance.ShouldSkipTransformSync)
            {
                position = s_localInstance.transform.position;
                return true;
            }

            position = default;
            return false;
        }

        /// <summary>Links to ship entity; optional presentationScale seeds baseline flight scale.</summary>
        public void Bind(Entity shipEntity, float presentationScale = -1f)
        {
            _shipEntity = shipEntity;
            if (presentationScale > 0f)
                _baselineScale = presentationScale;
            else
                RefreshBaselineScale();
        }

        /// <summary>
        /// True while this applier is driving the proxy onto / around the moon surface.
        /// Used by <see cref="EcsWorldVisualizer"/> before a chassis rebuild to decide whether to
        /// copy <see cref="LandingSurfaceDir"/> onto the replacement hull.
        /// </summary>
        public bool IsDrivingMoonDockPresentation =>
            _wasControllingTransform && !_isTakeoffAnimating && _landingSurfaceDir.sqrMagnitude > 0.0001f;

        /// <summary>
        /// Unit vector from moon center toward the hull contact (any latitude, not equator-only).
        /// Valid while <see cref="IsDrivingMoonDockPresentation"/> is true.
        /// </summary>
        public Vector3 LandingSurfaceDir => _landingSurfaceDir;

        /// <summary>
        /// After a hybrid hull rebuild while still fully moon-docked, snap presentation to the
        /// landed pose immediately so we do not replay the landing lerp from a physics-ejected
        /// ECS position (felt like the upgrade "booted" the ship out of orbit).
        /// </summary>
        /// <param name="em">Visualization-world EntityManager for this ship ghost.</param>
        public void SeedFullyLandedPresentation(EntityManager em)
        {
            SeedFullyLandedPresentation(em, preserveSurfaceDir: false, preservedSurfaceDir: default);
        }

        /// <summary>
        /// Same as <see cref="SeedFullyLandedPresentation(EntityManager)"/>, but keeps the previous
        /// hull's moon-surface contact direction so a purchased chassis appears at the same spun
        /// pose (same side of the moon, same docked rotation) instead of snapping to the planar
        /// ECS attach point.
        /// </summary>
        /// <param name="em">Visualization-world EntityManager for this ship ghost.</param>
        /// <param name="preservedSurfaceDir">
        /// Unit contact direction copied from the destroyed proxy's
        /// <see cref="LandingSurfaceDir"/> before rebuild.
        /// </param>
        public void SeedFullyLandedPresentation(EntityManager em, Vector3 preservedSurfaceDir)
        {
            SeedFullyLandedPresentation(em, preserveSurfaceDir: true, preservedSurfaceDir);
        }

        /// <summary>
        /// Shared seed path for chassis swaps while fully moon-docked.
        /// </summary>
        /// <param name="em">Visualization-world EntityManager.</param>
        /// <param name="preserveSurfaceDir">
        /// When true, use <paramref name="preservedSurfaceDir"/> instead of deriving contact from
        /// the new proxy's (often planar ECS) transform.
        /// </param>
        /// <param name="preservedSurfaceDir">
        /// Prior hull contact direction (full 3D — latitude + longitude on the moon sphere).
        /// </param>
        void SeedFullyLandedPresentation(EntityManager em, bool preserveSurfaceDir, Vector3 preservedSurfaceDir)
        {
            if (_shipEntity == Entity.Null || !em.Exists(_shipEntity) ||
                !em.HasComponent<ShipMoonDockState>(_shipEntity))
                return;

            var moonDock = em.GetComponentData<ShipMoonDockState>(_shipEntity);
            if (moonDock.MoonPlanetId == 0 ||
                moonDock.LandingProgress + 0.0001f < GemEconomyConstants.MoonLandingCompleteThreshold)
                return;

            if (!TryResolveMoonPose(moonDock.MoonPlanetId, out Vector3 moonPos, out Vector3 spinAxis,
                    out float moonBodyRadius))
                return;

            // --- Instant landed state (skip approach lerp / takeoff flags) ---
            RefreshBaselineScale();

            // Prefer the old hull's spinning contact dir so purchase keeps the same surface pose
            // (same latitude + longitude). Fallback: closest surface point from current transform.
            if (preserveSurfaceDir && preservedSurfaceDir.sqrMagnitude > 0.0001f)
            {
                // [TITAN-ORBIT] Keep the full 3D contact. Do NOT ProjectOnPlane(spinAxis) — that
                // used to force the equator and wipe the parked latitude after a chassis rebuild.
                _landingSurfaceDir = preservedSurfaceDir.normalized;
            }
            else
            {
                _landingSurfaceDir = ComputeClosestSurfaceDirection(transform.position, moonPos, spinAxis);
            }

            _landingStartPosition = moonPos + _landingSurfaceDir *
                (moonBodyRadius + BodyCollisionMath.GetShipHullRadiusWorld(
                    _baselineScale / BodyCollisionMath.ShipPresentationScale));
            _landingStartRotation = ComputeDockedRotation(_landingSurfaceDir, spinAxis);
            _landingStartScale = _baselineScale * DockScaleAtSurface;
            _isTakeoffAnimating = false;
            _wasLandingVisualActive = true;
            _wasMoonDockEngaged = true;
            _wasControllingTransform = true;

            ApplyLandingAnimation(moonDock, moonPos, spinAxis, moonBodyRadius);
        }

        void RefreshBaselineScale()
        {
            _baselineScale = Mathf.Max(0.01f, transform.localScale.x);
        }

        void LateUpdate()
        {
            if (_shipEntity == Entity.Null)
            {
                ResetDockPresentationState();
                return;
            }

            var world = EcsGameBridge.GetVisualizationWorld();
            if (world == null || !world.IsCreated)
            {
                ResetDockPresentationState();
                return;
            }

            var em = world.EntityManager;
            if (!em.Exists(_shipEntity) ||
                !em.HasComponent<ShipMoonDockState>(_shipEntity) ||
                !em.HasComponent<LocalTransform>(_shipEntity))
            {
                if (s_localInstance == this)
                    s_localInstance = null;
                ResetDockPresentationState();
                return;
            }

            var moonDock = em.GetComponentData<ShipMoonDockState>(_shipEntity);
            bool moonDockEngaged = moonDock.MoonPlanetId != 0;
            // [TITAN-ORBIT] Brief approach delay before landing animation starts (matches server timing).
            // Fully landed must stay cinematic even if LandingApproachDelay flickers to 0 — that used
            // to drop ShouldSkipTransformSync and show the planar ECS pose at full flight scale.
            bool approachReady = moonDock.LandingApproachDelay + 0.0001f >= GemEconomyConstants.MoonLandingApproachDelaySeconds;
            bool fullyLanded = moonDock.LandingProgress + 0.0001f >= GemEconomyConstants.MoonLandingCompleteThreshold;
            bool landingVisualActive = moonDockEngaged
                && moonDock.LandingProgress > 0.001f
                && (approachReady || fullyLanded);
            UpdateLocalInstanceRegistration(em);

            // --- Takeoff reverse animation (when moon dock disengages) ---
            if (_isTakeoffAnimating)
            {
                UpdateTakeoffAnimation(em);
                // Follow the lerping hull during takeoff.
                SetDockCameraFollow(transform.position, softCatchUp: false);
                _wasControllingTransform = true;
                _wasLandingVisualActive = false;
                _wasMoonDockEngaged = moonDockEngaged;
                return;
            }

            if (!landingVisualActive)
            {
                if (_wasMoonDockEngaged && !moonDockEngaged)
                    BeginTakeoffAnimation();

                if (_isTakeoffAnimating)
                {
                    UpdateTakeoffAnimation(em);
                    SetDockCameraFollow(transform.position, softCatchUp: false);
                    _wasControllingTransform = true;
                    _wasLandingVisualActive = false;
                    _wasMoonDockEngaged = moonDockEngaged;
                    return;
                }

                ClearDockCameraFollow();
                _wasControllingTransform = false;
                _wasLandingVisualActive = false;
                _wasMoonDockEngaged = moonDockEngaged;
                return;
            }

            if (!TryResolveMoonPose(moonDock.MoonPlanetId, out Vector3 moonPos, out Vector3 spinAxis, out float moonBodyRadius))
            {
                // Keep last good ship follow anchor if the moon proxy blips for a frame.
                _wasControllingTransform = false;
                _wasLandingVisualActive = false;
                _wasMoonDockEngaged = moonDockEngaged;
                return;
            }

            if (!_wasLandingVisualActive)
                CaptureLandingStartPose(moonPos, spinAxis);

            ApplyLandingAnimation(moonDock, moonPos, spinAxis, moonBodyRadius);
            _wasControllingTransform = true;
            _wasLandingVisualActive = true;
            _wasMoonDockEngaged = moonDockEngaged;
        }

        void ResetDockPresentationState()
        {
            ClearDockCameraFollow();
            _wasControllingTransform = false;
            _wasLandingVisualActive = false;
            _wasMoonDockEngaged = false;
        }

        /// <summary>
        /// Captures the flight pose at the start of a landing cinematic and picks the closest
        /// moon-surface contact direction from the ship's current world position.
        /// </summary>
        /// <param name="moonPos">Moon center in world space.</param>
        /// <param name="spinAxis">Moon mesh spin axis (unit); used only if contact is degenerate.</param>
        void CaptureLandingStartPose(Vector3 moonPos, Vector3 spinAxis)
        {
            RefreshBaselineScale();
            _landingStartPosition = transform.position;
            _landingStartRotation = transform.rotation;
            _landingStartScale = transform.localScale.x;
            // [TITAN-ORBIT] Closest point on the sphere — keeps the ship's approach latitude.
            _landingSurfaceDir = ComputeClosestSurfaceDirection(transform.position, moonPos, spinAxis);
        }

        /// <summary>
        /// Drives proxy pose onto the moon surface (at the captured latitude) and publishes a
        /// ship-hull camera follow anchor.
        /// </summary>
        void ApplyLandingAnimation(ShipMoonDockState moonDock, Vector3 moonPos, Vector3 spinAxis, float moonBodyRadius)
        {
            float shipRadius = BodyCollisionMath.GetShipHullRadiusWorld(_baselineScale / BodyCollisionMath.ShipPresentationScale);
            float contactRadius = moonBodyRadius + shipRadius;

            float progress = Mathf.Clamp01(moonDock.LandingProgress);
            float eased = GemMoonDockEaseInOut(progress);
            bool fullyLanded = progress + 0.0001f >= GemEconomyConstants.MoonLandingCompleteThreshold;

            // --- Cosmetic surface spin (approach + fully landed) ---
            // [TITAN-ORBIT] Same 9°/s as PlanetGemMoonVisualProxy so the hull rides the spinning
            // mesh. Rotating the contact dir around spinAxis preserves latitude (polar angle) while
            // advancing longitude — a mid-latitude land stays on that parallel, not the equator.
            // Camera hard-locks to this hull (below). Approach ramps spin with eased progress;
            // fully landed uses full rate.
            float spinWeight = fullyLanded ? 1f : eased;
            float spinStep = SpinSpeedDegPerSec * Time.deltaTime * spinWeight;
            if (Mathf.Abs(spinStep) > 0.0001f)
                _landingSurfaceDir = Quaternion.AngleAxis(spinStep, spinAxis) * _landingSurfaceDir;

            _landingSurfaceDir = _landingSurfaceDir.normalized;
            Vector3 endPosition = moonPos + _landingSurfaceDir * contactRadius;
            Quaternion endRotation = ComputeDockedRotation(_landingSurfaceDir, spinAxis);
            float dockedScale = _baselineScale * DockScaleAtSurface;

            // Once fully landed, pin to the live surface point (no stale start-pose lerp).
            // During approach, lerp from capture pose into the spinning surface contact.
            if (fullyLanded)
            {
                transform.position = endPosition;
                transform.rotation = endRotation;
                transform.localScale = Vector3.one * dockedScale;
            }
            else
            {
                transform.position = Vector3.Lerp(_landingStartPosition, endPosition, eased);
                transform.rotation = Quaternion.Slerp(_landingStartRotation, endRotation, eased);
                transform.localScale = Vector3.one * Mathf.Lerp(_landingStartScale, dockedScale, eased);
            }

            // --- Camera: always follow the ship hull (approach + parked surface spin) ---
            // [TITAN-ORBIT] Intentional: we used to soft-lock to moon center once landed so spin
            // would not move the camera; players prefer riding the hull with the moon instead.
            SetDockCameraFollow(transform.position, softCatchUp: false);
        }

        /// <summary>
        /// Updates the local-player camera anchor used by <see cref="TryGetLocalFollowPosition"/>.
        /// </summary>
        /// <param name="target">World point the gameplay camera should hard-lock to (plus its offset).</param>
        /// <param name="softCatchUp">
        /// When true, exponentially ease toward <paramref name="target"/> to hide moon pose jitter.
        /// </param>
        void SetDockCameraFollow(Vector3 target, bool softCatchUp)
        {
            if (!_dockCameraFollowValid || !softCatchUp)
            {
                _dockCameraFollowPosition = target;
                _dockCameraFollowValid = true;
                return;
            }

            // [STANDARD] Frame-rate independent exp lerp: 1 - e^(-k dt).
            float t = 1f - Mathf.Exp(-DockCameraFollowCatchUp * Time.deltaTime);
            _dockCameraFollowPosition = Vector3.Lerp(_dockCameraFollowPosition, target, t);
            _dockCameraFollowValid = true;
        }

        /// <summary>Clears the dock camera override so <see cref="CameraFollowEcs"/> returns to presentation pose.</summary>
        void ClearDockCameraFollow()
        {
            _dockCameraFollowValid = false;
            _dockCameraFollowPosition = default;
        }

        void OnDisable()
        {
            ClearDockCameraFollow();
            if (s_localInstance == this)
                s_localInstance = null;
        }

        void UpdateLocalInstanceRegistration(EntityManager em)
        {
            if (em.HasComponent<LocalPlayerShipTag>(_shipEntity) ||
                em.HasComponent<GhostOwnerIsLocal>(_shipEntity))
                s_localInstance = this;
            else if (s_localInstance == this)
                s_localInstance = null;
        }

        void BeginTakeoffAnimation()
        {
            _takeoffStartPosition = transform.position;
            _takeoffStartRotation = transform.rotation;
            _takeoffStartScale = transform.localScale.x;
            _takeoffProgress = 0f;
            _isTakeoffAnimating = true;
        }

        /// <summary>Lerps proxy back to ECS LocalTransform flight pose over LandingDurationSeconds.</summary>
        void UpdateTakeoffAnimation(EntityManager em)
        {
            var lt = em.GetComponentData<LocalTransform>(_shipEntity);
            float flightScale = Mathf.Max(0.25f, lt.Scale) * BodyCollisionMath.ShipPresentationScale;

            Vector3 endPosition = GetShipVisualPosition(em, lt.Position);
            Quaternion endRotation = lt.Rotation;
            float endScale = flightScale;

            _takeoffProgress = Mathf.Min(1f, _takeoffProgress + Time.deltaTime / LandingDurationSeconds);
            float eased = GemMoonDockEaseInOut(_takeoffProgress);

            transform.position = Vector3.Lerp(_takeoffStartPosition, endPosition, eased);
            transform.rotation = Quaternion.Slerp(_takeoffStartRotation, endRotation, eased);
            transform.localScale = Vector3.one * Mathf.Lerp(_takeoffStartScale, endScale, eased);

            if (_takeoffProgress >= 1f)
            {
                _isTakeoffAnimating = false;
                RefreshBaselineScale();
            }
        }

        /// <summary>
        /// Closest unit direction from moon center to the ship — the sphere's nearest surface point.
        /// Keeps latitude (angle from the spin axis); spin later rotates this vector around
        /// <paramref name="spinAxis"/> so the hull rides a parallel, not only the equator.
        /// </summary>
        /// <param name="shipPosition">Ship proxy world position at capture / seed time.</param>
        /// <param name="moonPos">Moon center world position.</param>
        /// <param name="spinAxis">
        /// Moon spin axis (unit). Used only for the rare degenerate case when the ship sits nearly
        /// on the moon center (no reliable radial direction).
        /// </param>
        /// <returns>Unit vector moon-center → closest surface contact.</returns>
        static Vector3 ComputeClosestSurfaceDirection(Vector3 shipPosition, Vector3 moonPos, Vector3 spinAxis)
        {
            // [STANDARD] Closest point on a sphere = moon center + R * normalize(ship - moon).
            // We return only the unit radial; callers multiply by (moon radius + ship radius).
            Vector3 dir = shipPosition - moonPos;
            if (dir.sqrMagnitude > 0.0001f)
                return dir.normalized;

            // Degenerate: ship almost at moon center — pick an equatorial fallback so spin has a ring.
            Vector3 equatorial = Vector3.ProjectOnPlane(Vector3.forward, spinAxis);
            if (equatorial.sqrMagnitude < 0.0001f)
                equatorial = Vector3.ProjectOnPlane(Vector3.right, spinAxis);
            if (equatorial.sqrMagnitude < 0.0001f)
                equatorial = Vector3.right;
            return equatorial.normalized;
        }

        /// <summary>
        /// Docked hull orientation: up = outward surface normal, forward = circumferential spin
        /// tangent so the ship faces along the parallel it rides (works at any latitude).
        /// </summary>
        /// <param name="surfaceNormal">Unit moon-center → contact (any latitude).</param>
        /// <param name="spinAxis">Moon mesh spin axis (unit).</param>
        static Quaternion ComputeDockedRotation(Vector3 surfaceNormal, Vector3 spinAxis)
        {
            // Circumferential tangent = direction the contact point moves under moon spin.
            // Near the poles Cross(spinAxis, normal) shrinks — fall back to a stable look axis.
            Vector3 tangent = Vector3.Cross(spinAxis, surfaceNormal);
            if (tangent.sqrMagnitude < 0.0001f)
                tangent = Vector3.Cross(surfaceNormal, Vector3.forward);
            if (tangent.sqrMagnitude < 0.0001f)
                tangent = Vector3.forward;
            tangent.Normalize();
            return Quaternion.LookRotation(tangent, surfaceNormal);
        }

        /// <summary>Local docked ship = logical; remotes use hysteresis near local ship.</summary>
        Vector3 GetShipVisualPosition(EntityManager em, float3 logicalPos)
        {
            if (ToroidalDisplay.IsLocalPlayerShip(em, _shipEntity))
                return logicalPos;

            if (!ToroidalDisplay.TryGetReferencePosition(out var reference))
                return logicalPos;

            return ToroidalDisplay.ToDisplayPositionWithHysteresis(_shipEntity, logicalPos, reference);
        }

        /// <summary>Resolves moon world pose from visual registry or ECS planet + orbit math fallback.</summary>
        static bool TryResolveMoonPose(int planetId, out Vector3 moonPos, out Vector3 spinAxis, out float moonBodyRadius)
        {
            moonPos = default;
            spinAxis = Vector3.up;
            moonBodyRadius = 0.5f;

            if (PlanetGemMoonVisualRegistry.TryGetMoon(planetId, out var moonProxy))
            {
                moonPos = moonProxy.MoonWorldPosition;
                spinAxis = moonProxy.SpinAxisWorld.normalized;
                moonBodyRadius = moonProxy.MoonBodyRadiusWorld;
                return true;
            }

            if (!EcsGameBridge.TryGetPlanetPoseByPlanetId(planetId, out float3 planetPos, out float planetSize, out var planetState))
                return false;

            // --- Classic tile unwrap toward logical local ship ---
            if (ToroidalDisplay.TryGetReferencePosition(out var reference))
            {
                Vector3 displayPlanet = ToroidalDisplay.ToDisplayPositionWithHysteresis(planetId, planetPos, reference);
                planetPos = new float3(displayPlanet.x, displayPlanet.y, displayPlanet.z);
            }

            // [TITAN-ORBIT] Same ServerTick orbit clock as PlanetGemMoonVisualProxy / colliders.
            double elapsed = PlanetGemMoonOrbitClock.TryGetElapsedSeconds(out double orbitElapsed, includeTickFraction: true)
                ? orbitElapsed
                : Time.timeAsDouble;
            float3 moonPosF3 = PlanetOrbitMath.GetMoonWorldPosition(
                planetPos,
                planetSize,
                planetState.PlanetLevel,
                planetId,
                elapsed,
                planetState.IsHomePlanet);
            moonPos = new Vector3(moonPosF3.x, moonPosF3.y, moonPosF3.z);

            float3 spinAxisLocal = PlanetOrbitMath.GetLevelBandsSpinAxisLocal();
            if (EcsGameBridge.TryGetPlanetRotationByPlanetId(planetId, out quaternion planetRot))
            {
                float3 spinAxisF3 = math.normalizesafe(math.mul(planetRot, spinAxisLocal), new float3(0f, 1f, 0f));
                spinAxis = new Vector3(spinAxisF3.x, spinAxisF3.y, spinAxisF3.z);
            }
            else
                spinAxis = new Vector3(spinAxisLocal.x, spinAxisLocal.y, spinAxisLocal.z).normalized;

            moonBodyRadius = PlanetGemMoonMath.GetMoonBodyRadiusWorld(planetSize, planetState.IsHomePlanet);
            return true;
        }

        static float GemMoonDockEaseInOut(float t)
        {
            t = Mathf.Clamp01(t);
            return t * t * (3f - 2f * t);
        }
    }
}
