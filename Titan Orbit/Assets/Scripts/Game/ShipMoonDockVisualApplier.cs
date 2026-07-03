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
    /// Client-side moon landing presentation: ship snaps to moon surface and rotates with moon spin.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public class ShipMoonDockVisualApplier : MonoBehaviour
    {
        const float SpinSpeedDegPerSec = 9f;
        const float DockScaleAtSurface = 0.24f;
        const float DockScaleAtOrbitEdge = 1f / 3f;

        Entity _shipEntity;
        Vector3 _landingOffset;
        int _cachedPlanetId;
        float _baselineScale = 1f;
        float _dockStartScale = 1f;
        Quaternion _dockStartRotation = Quaternion.identity;
        bool _wasDocked;

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
                return;

            var world = EcsGameBridge.GetVisualizationWorld();
            if (world == null || !world.IsCreated)
                return;

            var em = world.EntityManager;
            if (!em.Exists(_shipEntity) ||
                !em.HasComponent<ShipMoonDockState>(_shipEntity) ||
                !em.HasComponent<LocalTransform>(_shipEntity))
                return;

            var moonDock = em.GetComponentData<ShipMoonDockState>(_shipEntity);
            if (moonDock.MoonPlanetId == 0 || moonDock.LandingProgress <= 0.001f)
            {
                if (_wasDocked)
                {
                    transform.localScale = Vector3.one * _baselineScale;
                    RefreshBaselineScale();
                }
                _wasDocked = false;
                _cachedPlanetId = 0;
                return;
            }

            if (!TryResolveMoonPose(moonDock.MoonPlanetId, out Vector3 moonPos, out Vector3 spinAxis, out float moonBodyRadius))
                return;

            float eased = GemMoonDockEaseInOut(Mathf.Clamp01(moonDock.LandingProgress));
            float shipRadius = BodyCollisionMath.GetShipHullRadiusWorld(_baselineScale / BodyCollisionMath.ShipPresentationScale);
            float contactRadius = moonBodyRadius + shipRadius;

            if (!_wasDocked || _cachedPlanetId != moonDock.MoonPlanetId)
            {
                RefreshBaselineScale();
                _dockStartScale = transform.localScale.x;
                _dockStartRotation = transform.rotation;

                Vector3 initialDir = transform.position - moonPos;
                initialDir = Vector3.ProjectOnPlane(initialDir, spinAxis);
                if (initialDir.sqrMagnitude < 0.0001f)
                    initialDir = Vector3.Cross(spinAxis, Vector3.forward);
                if (initialDir.sqrMagnitude < 0.0001f)
                    initialDir = Vector3.Cross(spinAxis, Vector3.right);
                initialDir.Normalize();
                _landingOffset = initialDir * contactRadius;
                _cachedPlanetId = moonDock.MoonPlanetId;
            }

            float spinStep = SpinSpeedDegPerSec * Time.deltaTime * eased;
            if (Mathf.Abs(spinStep) > 0.0001f)
                _landingOffset = Quaternion.AngleAxis(spinStep, spinAxis) * _landingOffset;

            _landingOffset = _landingOffset.normalized * contactRadius;
            transform.position = moonPos + _landingOffset;

            Vector3 surfaceNormal = _landingOffset.normalized;
            Vector3 tangent = Vector3.Cross(spinAxis, surfaceNormal);
            if (tangent.sqrMagnitude < 0.0001f)
                tangent = Vector3.ProjectOnPlane(transform.forward, surfaceNormal);
            if (tangent.sqrMagnitude < 0.0001f)
                tangent = Vector3.forward;
            tangent.Normalize();

            Quaternion targetRot = Quaternion.LookRotation(tangent, surfaceNormal);
            transform.rotation = Quaternion.Slerp(_dockStartRotation, targetRot, eased);

            float targetScaleMul = Mathf.Lerp(DockScaleAtOrbitEdge, DockScaleAtSurface, eased);
            float targetScale = _baselineScale * targetScaleMul;
            transform.localScale = Vector3.one * Mathf.Lerp(_dockStartScale, targetScale, eased);
            _wasDocked = true;
        }

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

            double elapsed = TryGetSimulationElapsedTime(out double simElapsed) ? simElapsed : Time.timeAsDouble;
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

        static bool TryGetSimulationElapsedTime(out double elapsedSeconds)
        {
            elapsedSeconds = 0d;
            World world = null;
            if (ClientServerBootstrap.ServerWorld != null && ClientServerBootstrap.ServerWorld.IsCreated)
                world = ClientServerBootstrap.ServerWorld;
            else if (ClientServerBootstrap.ClientWorld != null && ClientServerBootstrap.ClientWorld.IsCreated)
                world = ClientServerBootstrap.ClientWorld;

            if (world == null || !world.IsCreated)
                return false;

            elapsedSeconds = world.Time.ElapsedTime;
            return true;
        }

        static float GemMoonDockEaseInOut(float t)
        {
            t = Mathf.Clamp01(t);
            return t * t * (3f - 2f * t);
        }
    }
}
