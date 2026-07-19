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
    /// surface (scale shrink, spin with moon), and reverses when thrusting away. Reads ShipMoonDockState
    /// from the visualization ECS world. When active, EcsWorldVisualizer skips transform sync
    /// (ShouldSkipTransformSync). Provides local camera follow override via TryGetLocalFollowPosition.
    /// Cosmetic cinematic — authoritative dock state lives in ShipMoonDockSystem on the server.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public class ShipMoonDockVisualApplier : MonoBehaviour
    {
        const float SpinSpeedDegPerSec = 9f;
        const float DockScaleAtSurface = 0.24f;
        const float LandingDurationSeconds = 1f;

        Entity _shipEntity;
        float _baselineScale = 1f;
        bool _wasControllingTransform;
        bool _wasLandingVisualActive;
        bool _wasMoonDockEngaged;

        // Landing animation start pose (captured when each landing sequence begins).
        Vector3 _landingStartPosition;
        Quaternion _landingStartRotation;
        float _landingStartScale;

        // Surface contact direction on the moon spin plane (rotates with moon during dock).
        Vector3 _landingSurfaceDir;

        // Takeoff animation.
        bool _isTakeoffAnimating;
        float _takeoffProgress;
        Vector3 _takeoffStartPosition;
        Quaternion _takeoffStartRotation;
        float _takeoffStartScale;

        /// <summary>When true, EcsWorldVisualizer should not overwrite this proxy's transform.</summary>
        public bool ShouldSkipTransformSync => _isTakeoffAnimating || _wasControllingTransform;

        static ShipMoonDockVisualApplier s_localInstance;

        /// <summary>Visual follow point for the local player while landing/docked/taking off.</summary>
        public static bool TryGetLocalFollowPosition(out Vector3 position)
        {
            if (s_localInstance != null && s_localInstance.ShouldSkipTransformSync)
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
            bool approachReady = moonDock.LandingApproachDelay + 0.0001f >= GemEconomyConstants.MoonLandingApproachDelaySeconds;
            bool landingVisualActive = moonDockEngaged && approachReady && moonDock.LandingProgress > 0.001f;
            UpdateLocalInstanceRegistration(em);

            // --- Takeoff reverse animation (when moon dock disengages) ---
            if (_isTakeoffAnimating)
            {
                UpdateTakeoffAnimation(em);
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
                    _wasControllingTransform = true;
                    _wasLandingVisualActive = false;
                    _wasMoonDockEngaged = moonDockEngaged;
                    return;
                }

                _wasControllingTransform = false;
                _wasLandingVisualActive = false;
                _wasMoonDockEngaged = moonDockEngaged;
                return;
            }

            if (!TryResolveMoonPose(moonDock.MoonPlanetId, out Vector3 moonPos, out Vector3 spinAxis, out float moonBodyRadius))
            {
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

        void ApplyLandingAnimation(ShipMoonDockState moonDock, Vector3 moonPos, Vector3 spinAxis, float moonBodyRadius)
        {
            float shipRadius = BodyCollisionMath.GetShipHullRadiusWorld(_baselineScale / BodyCollisionMath.ShipPresentationScale);
            float contactRadius = moonBodyRadius + shipRadius;

            float eased = GemMoonDockEaseInOut(Mathf.Clamp01(moonDock.LandingProgress));
            float spinStep = SpinSpeedDegPerSec * Time.deltaTime * eased;
            if (Mathf.Abs(spinStep) > 0.0001f)
                _landingSurfaceDir = Quaternion.AngleAxis(spinStep, spinAxis) * _landingSurfaceDir;

            _landingSurfaceDir = _landingSurfaceDir.normalized;
            Vector3 endPosition = moonPos + _landingSurfaceDir * contactRadius;
            Quaternion endRotation = ComputeDockedRotation(_landingSurfaceDir, spinAxis);
            float dockedScale = _baselineScale * DockScaleAtSurface;

            transform.position = Vector3.Lerp(_landingStartPosition, endPosition, eased);
            transform.rotation = Quaternion.Slerp(_landingStartRotation, endRotation, eased);
            transform.localScale = Vector3.one * Mathf.Lerp(_landingStartScale, dockedScale, eased);
        }

        void OnDisable()
        {
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
