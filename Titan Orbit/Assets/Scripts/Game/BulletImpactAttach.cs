using SpaceGraphicsToolkit;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Entities;
using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Places impact VFX on the <b>drawn</b> body and parents them so they follow
    /// moving ships / moons / rocks. Cosmetic only — damage stays on HitRpc.
    /// </summary>
    public static class BulletImpactAttach
    {
        /// <summary>
        /// World radius of the asteroid mesh this observer sees (SgtPlanet radius +
        /// displacement × proxy scale). Larger than the gameplay sweep sphere when
        /// the rock is puffed — that gap was spawning impacts inside the mesh.
        /// </summary>
        public static float GetAsteroidVisualRadiusWorld(Transform proxy)
        {
            if (proxy == null)
                return BodyCollisionMath.GetAsteroidBodyRadiusWorld(1f);

            float scale = math.max(0.01f, math.abs(proxy.lossyScale.x));
            var sgt = proxy.GetComponentInChildren<SgtPlanet>(true);
            if (sgt != null)
            {
                float local = math.max(0.01f, sgt.Radius) + math.max(0f, sgt.Displacement);
                return scale * local;
            }

            return scale * (BodyCollisionMath.AsteroidMeshBaseRadius
                            + BodyCollisionMath.AsteroidVisualDisplacementLocal);
        }

        /// <summary>
        /// Resolves a follow parent + world surface point from a cosmetic obstacle.
        /// Uses the proxy's hysteresis tile so the flash sits on the mesh this client drew.
        /// </summary>
        public static bool TryResolve(
            in BulletCosmeticHitQuery.Obstacle obstacle,
            float3 logicalHit,
            out Transform parent,
            out Vector3 worldPos)
        {
            parent = null;
            worldPos = new Vector3(logicalHit.x, 0f, logicalHit.z);

            switch (obstacle.Kind)
            {
                case BulletCosmeticHitQuery.ObstacleKind.Moon:
                    return TryResolveMoon(in obstacle, logicalHit, out parent, out worldPos);
                case BulletCosmeticHitQuery.ObstacleKind.Asteroid:
                    return TryResolveProxySphere(
                        obstacle.SourceEntity, logicalHit, obstacle.LogicalCenter,
                        GetAsteroidRadius(obstacle), out parent, out worldPos);
                case BulletCosmeticHitQuery.ObstacleKind.Planet:
                    return TryResolveProxySphere(
                        obstacle.SourceEntity, logicalHit, obstacle.LogicalCenter,
                        math.max(0.05f, obstacle.Radius), out parent, out worldPos);
                case BulletCosmeticHitQuery.ObstacleKind.Ship:
                    return TryResolveShipContact(in obstacle, logicalHit, out parent, out worldPos);
                case BulletCosmeticHitQuery.ObstacleKind.PlanetaryDefense:
                    return TryResolveProxySphere(
                        obstacle.SourceEntity, logicalHit, obstacle.LogicalCenter,
                        math.max(0.05f, obstacle.Radius), out parent, out worldPos);
                case BulletCosmeticHitQuery.ObstacleKind.Drone:
                    return TryResolveProxySphere(
                        obstacle.SourceEntity, logicalHit, obstacle.LogicalCenter,
                        math.max(0.05f, obstacle.Radius), out parent, out worldPos);
                case BulletCosmeticHitQuery.ObstacleKind.Transport:
                    return TryResolveTransport(in obstacle, logicalHit, out parent, out worldPos);
                default:
                    return false;
            }
        }

        /// <summary>
        /// Sequence-0 / HitRpc path: pick the nearest cached obstacle and attach to it.
        /// </summary>
        public static bool TryResolveAtLogicalPoint(
            float3 logicalHit,
            out Transform parent,
            out Vector3 worldPos)
        {
            parent = null;
            worldPos = new Vector3(logicalHit.x, 0f, logicalHit.z);
            BulletCosmeticHitQuery.TryRefresh();
            if (!BulletCosmeticHitQuery.TryFindNearestObstacle(logicalHit, out var obstacle))
                return false;
            return TryResolve(in obstacle, logicalHit, out parent, out worldPos);
        }

        /// <summary>Spawns the bank impact on the resolved surface (parented when possible).</summary>
        public static void Play(
            float3 logicalHit,
            in BulletCosmeticHitQuery.Obstacle obstacle,
            BulletVfxBank bank,
            int bankIndex,
            TeamId team,
            float damage,
            float scaleMultiplier)
        {
            if (!TryResolve(in obstacle, logicalHit, out Transform parent, out Vector3 worldPos))
            {
                if (ToroidalDisplay.TryGetReferencePosition(out var reference))
                    worldPos = ToroidalDisplay.ToDisplayPosition(logicalHit, reference);
                parent = null;
            }

            BulletVisualFactory.SpawnBulletImpactVfx(
                worldPos, bank, bankIndex, team, damage, scaleMultiplier, parent);
        }

        /// <summary>HitRpc / ram / burn-tick: attach from the impact point alone.</summary>
        /// <param name="duration">
        /// Seconds the pooled flash stays alive. 0 or less uses
        /// <see cref="BulletVisualFactory.DefaultImpactDuration"/>.
        /// </param>
        public static void PlayAtLogicalPoint(
            float3 logicalHit,
            BulletVfxBank bank,
            int bankIndex,
            TeamId team,
            float damage,
            float scaleMultiplier,
            float duration = 0f)
        {
            TryResolveAtLogicalPoint(logicalHit, out Transform parent, out Vector3 worldPos);
            if (parent == null && ToroidalDisplay.TryGetReferencePosition(out var reference))
                worldPos = ToroidalDisplay.ToDisplayPosition(logicalHit, reference);

            BulletVisualFactory.SpawnBulletImpactVfx(
                worldPos, bank, bankIndex, team, damage, scaleMultiplier, parent, duration);
        }

        static float GetAsteroidRadius(in BulletCosmeticHitQuery.Obstacle obstacle)
        {
            var visualizer = EcsWorldVisualizer.Active;
            if (visualizer != null &&
                visualizer.TryGetProxy(obstacle.SourceEntity, out GameObject go) &&
                go != null)
                return GetAsteroidVisualRadiusWorld(go.transform);
            return math.max(0.05f, obstacle.Radius);
        }

        static bool TryResolveMoon(
            in BulletCosmeticHitQuery.Obstacle obstacle,
            float3 logicalHit,
            out Transform parent,
            out Vector3 worldPos)
        {
            parent = null;
            worldPos = default;
            if (!PlanetGemMoonVisualRegistry.TryGetMoon(obstacle.PlanetId, out var moon) ||
                moon == null)
                return TryResolveProxySphere(
                    obstacle.SourceEntity, logicalHit, obstacle.LogicalCenter,
                    math.max(0.05f, obstacle.Radius), out parent, out worldPos);

            Transform moonRoot = moon.transform.Find("GemMoonVisual");
            parent = moonRoot != null ? moonRoot : moon.transform;
            Vector3 moonCenter = moon.MoonWorldPosition;
            float radius = PlanetGemMoonMath.GetMoonBulletHitRadiusWorld(
                obstacle.Scale, obstacle.IsHomePlanet, obstacle.CurrentShield);
            if (radius < 0.05f)
                radius = math.max(obstacle.Radius, moon.MoonBodyRadiusWorld);

            // LogicalCenter is the planet — unwrap the hit onto the planet's drawn tile,
            // then project onto the moon sphere (do not add the orbit offset twice).
            Vector3 displayHit = moonCenter;
            var visualizer = EcsWorldVisualizer.Active;
            if (visualizer != null &&
                visualizer.TryGetProxy(obstacle.SourceEntity, out GameObject planetGo) &&
                planetGo != null)
            {
                displayHit = ProjectLogicalOntoDisplayCenter(
                    planetGo.transform.position, logicalHit, obstacle.LogicalCenter);
            }

            Vector3 dir = displayHit - moonCenter;
            dir.y = 0f;
            if (dir.sqrMagnitude < 1e-8f)
                dir = Vector3.forward;
            else
                dir.Normalize();
            worldPos = moonCenter + dir * radius;
            worldPos.y = moonCenter.y;
            return parent != null;
        }

        static bool TryResolveTransport(
            in BulletCosmeticHitQuery.Obstacle obstacle,
            float3 logicalHit,
            out Transform parent,
            out Vector3 worldPos)
        {
            parent = null;
            worldPos = default;
            if (!PeopleTransportVfxDriver.TryGetNearestFlightTransform(
                    obstacle.LogicalCenter, 4f, out parent) ||
                parent == null)
                return false;

            float radius = math.max(0.05f, obstacle.Radius);
            Vector3 displayHit = ProjectLogicalOntoDisplayCenter(
                parent.position, logicalHit, obstacle.LogicalCenter);
            Vector3 dir = displayHit - parent.position;
            dir.y = 0f;
            if (dir.sqrMagnitude < 1e-8f)
                dir = Vector3.forward;
            else
                dir.Normalize();
            worldPos = parent.position + dir * radius;
            worldPos.y = parent.position.y;
            return true;
        }

        /// <summary>
        /// Places the flash on the drawn hull at the sim contact. MEGA boxes must not be
        /// projected onto a covering sphere — that parks the VFX in empty space around
        /// a long hull while damage still applies.
        /// </summary>
        static bool TryResolveShipContact(
            in BulletCosmeticHitQuery.Obstacle obstacle,
            float3 logicalHit,
            out Transform parent,
            out Vector3 worldPos)
        {
            parent = null;
            worldPos = default;
            var visualizer = EcsWorldVisualizer.Active;
            if (visualizer == null ||
                !visualizer.TryGetProxy(obstacle.SourceEntity, out GameObject go) ||
                go == null)
                return false;

            parent = go.transform;
            float3 logicalPivot = obstacle.LogicalCenter;
            World world = EcsGameBridge.ClientWorld ?? EcsGameBridge.ServerWorld;
            if (world != null && world.IsCreated)
            {
                var em = world.EntityManager;
                if (em.Exists(obstacle.SourceEntity) &&
                    em.HasComponent<LocalTransform>(obstacle.SourceEntity))
                    logicalPivot = em.GetComponentData<LocalTransform>(obstacle.SourceEntity).Position;
            }

            worldPos = ProjectLogicalOntoDisplayCenter(parent.position, logicalHit, logicalPivot);
            worldPos.y = parent.position.y + (logicalHit.y - logicalPivot.y);
            return true;
        }

        static bool TryResolveProxySphere(
            Entity source,
            float3 logicalHit,
            float3 logicalCenter,
            float radius,
            out Transform parent,
            out Vector3 worldPos)
        {
            parent = null;
            worldPos = default;
            var visualizer = EcsWorldVisualizer.Active;
            if (visualizer == null ||
                !visualizer.TryGetProxy(source, out GameObject go) ||
                go == null)
                return false;

            parent = go.transform;
            Vector3 displayHit = ProjectLogicalOntoDisplayCenter(
                parent.position, logicalHit, logicalCenter);
            Vector3 dir = displayHit - parent.position;
            dir.y = 0f;
            if (dir.sqrMagnitude < 1e-8f)
                dir = Vector3.forward;
            else
                dir.Normalize();
            worldPos = parent.position + dir * math.max(0.05f, radius);
            worldPos.y = parent.position.y;
            return true;
        }

        static Vector3 ProjectLogicalOntoDisplayCenter(
            Vector3 displayCenter,
            float3 logicalHit,
            float3 logicalCenter)
        {
            float3 offset = logicalHit - logicalCenter;
            if (ToroidalMapEcs.TryGetMapSize(out float mapW, out float mapH) &&
                ToroidalMapEcs.IsValidMapSize(mapW, mapH))
                offset = ToroidalMapEcs.ShortestOffsetXZ(logicalCenter, logicalHit, mapW, mapH);
            offset.y = 0f;
            return new Vector3(
                displayCenter.x + offset.x,
                displayCenter.y,
                displayCenter.z + offset.z);
        }
    }
}
