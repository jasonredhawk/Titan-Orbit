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
    /// Client-side moon landing presentation: animates the ship proxy from flight pose onto the moon
    /// surface (scale shrink, optional spin during approach), and reverses when thrusting away. Reads
    /// <see cref="ShipMoonDockState"/> from the visualization ECS world. When active,
    /// <see cref="EcsWorldVisualizer"/> skips transform sync (<see cref="ShouldSkipTransformSync"/>).
    /// Provides a stable local camera follow override via <see cref="TryGetLocalFollowPosition"/> —
    /// once landed the camera tracks the moon center (not the spinning surface point) so co-orbit
    /// attach + surface spin cannot jerk the hard-lock follow. Cosmetic only; server dock state is
    /// owned by <see cref="ShipMoonDockSystem"/>.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public class ShipMoonDockVisualApplier : MonoBehaviour
    {
        /// <summary>Cosmetic hull spin during the landing lerp only (deg/sec). Frozen once fully docked.</summary>
        const float SpinSpeedDegPerSec = 9f;

        /// <summary>Proxy scale multiplier when parked on the moon surface.</summary>
        const float DockScaleAtSurface = 0.24f;

        /// <summary>Landing and takeoff cinematic duration (seconds).</summary>
        const float LandingDurationSeconds = 1f;

        /// <summary>
        /// Soft follow rate toward the moon anchor while docked (1/s). [TITAN-ORBIT] Presentation only —
        /// not a second flight motor; hides tick-fraction / registry jitter from the hard-lock camera.
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

        // Surface contact direction on the moon spin plane (rotates with moon during approach only).
        Vector3 _landingSurfaceDir;

        // Takeoff animation.
        bool _isTakeoffAnimating;
        float _takeoffProgress;
        Vector3 _takeoffStartPosition;
        Quaternion _takeoffStartRotation;
        float _takeoffStartScale;

        // --- Stable camera anchor (moon-centered while docked) ---
        bool _dockCameraFollowValid;
        Vector3 _dockCameraFollowPosition;

        /// <summary>When true, EcsWorldVisualizer should not overwrite this proxy's transform.</summary>
        public bool ShouldSkipTransformSync => _isTakeoffAnimating || _wasControllingTransform;

        static ShipMoonDockVisualApplier s_localInstance;

        /// <summary>
        /// Visual follow point for the local player while landing/docked/taking off.
        /// Prefers the moon-stable anchor so <see cref="CameraFollowEcs"/> does not hard-lock to
        /// surface spin or flicker onto planar ECS attach beside the moon.
        /// </summary>
        public static bool TryGetLocalFollowPosition(out Vector3 position)
        {
            if (s_localInstance == null)
            {
                position = default;
                return false;
            }

            // [HYBRID] Moon-stable anchor while dock cinematic is driving presentation.
            if (s_localInstance._dockCameraFollowValid)
            {
                position = s_localInstance._dockCameraFollowPosition;
                return true;
            }

            // Takeoff (or pre-anchor frames): follow the proxy hull itself.
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
        /// After a hybrid hull rebuild while still fully moon-docked, snap presentation to the
        /// landed pose immediately so we do not replay the landing lerp from a physics-ejected
        /// ECS position (felt like the upgrade "booted" the ship out of orbit).
        /// </summary>
        public void SeedFullyLandedPresentation(EntityManager em)
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
            _landingSurfaceDir = ComputeSurfaceDirection(transform.position, moonPos, spinAxis);
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
                // Follow the lerping hull during takeoff (not the parked moon anchor).
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
                // Keep last good camera anchor if the moon proxy blips for a frame.
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

        void CaptureLandingStartPose(Vector3 moonPos, Vector3 spinAxis)
        {
            RefreshBaselineScale();
            _landingStartPosition = transform.position;
            _landingStartRotation = transform.rotation;
            _landingStartScale = transform.localScale.x;
            _landingSurfaceDir = ComputeSurfaceDirection(transform.position, moonPos, spinAxis);
        }

        /// <summary>
        /// Drives proxy pose onto the moon surface and publishes a stable camera follow anchor.
        /// </summary>
        void ApplyLandingAnimation(ShipMoonDockState moonDock, Vector3 moonPos, Vector3 spinAxis, float moonBodyRadius)
        {
            float shipRadius = BodyCollisionMath.GetShipHullRadiusWorld(_baselineScale / BodyCollisionMath.ShipPresentationScale);
            float contactRadius = moonBodyRadius + shipRadius;

            float progress = Mathf.Clamp01(moonDock.LandingProgress);
            float eased = GemMoonDockEaseInOut(progress);
            bool fullyLanded = progress + 0.0001f >= GemEconomyConstants.MoonLandingCompleteThreshold;

            // --- Cosmetic surface spin during approach only ---
            // [TITAN-ORBIT] Once landed, freeze the contact direction. Spinning the hull under a
            // hard-lock camera made the view orbit the moon in a jerky compound motion with co-orbit.
            if (!fullyLanded)
            {
                float spinStep = SpinSpeedDegPerSec * Time.deltaTime * eased;
                if (Mathf.Abs(spinStep) > 0.0001f)
                    _landingSurfaceDir = Quaternion.AngleAxis(spinStep, spinAxis) * _landingSurfaceDir;
            }

            _landingSurfaceDir = _landingSurfaceDir.normalized;
            Vector3 endPosition = moonPos + _landingSurfaceDir * contactRadius;
            Quaternion endRotation = ComputeDockedRotation(_landingSurfaceDir, spinAxis);
            float dockedScale = _baselineScale * DockScaleAtSurface;

            transform.position = Vector3.Lerp(_landingStartPosition, endPosition, eased);
            transform.rotation = Quaternion.Slerp(_landingStartRotation, endRotation, eased);
            transform.localScale = Vector3.one * Mathf.Lerp(_landingStartScale, dockedScale, eased);

            // --- Camera: ship during approach, moon center once parked ---
            // Moon center rides the smooth tick-fraction orbit without surface-spin radius.
            if (fullyLanded)
                SetDockCameraFollow(moonPos, softCatchUp: true);
            else
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

        static Vector3 ComputeSurfaceDirection(Vector3 shipPosition, Vector3 moonPos, Vector3 spinAxis)
        {
            Vector3 dir = shipPosition - moonPos;
            dir = Vector3.ProjectOnPlane(dir, spinAxis);
            if (dir.sqrMagnitude < 0.0001f)
                dir = Vector3.Cross(spinAxis, Vector3.forward);
            if (dir.sqrMagnitude < 0.0001f)
                dir = Vector3.Cross(spinAxis, Vector3.right);
            return dir.normalized;
        }

        static Quaternion ComputeDockedRotation(Vector3 surfaceNormal, Vector3 spinAxis)
        {
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
