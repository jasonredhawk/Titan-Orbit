using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>Deterministic compound hull collision against solid world bodies. People transports pass through.</summary>
    public static class ShipCollisionLogic
    {
        const float DepenetrationSkin = 0.002f;
        const float CullingPadding = 1.5f;
        const float MaxHullHalfExtentWorld = 0.35f;
        const float MinSweptHitT = 1e-4f;

        public static void ResolveMovement(
            EntityManager em,
            Entity selfEntity,
            float3 prevPos,
            quaternion prevRot,
            ref ShipMotorState motorState,
            float shipTransformScale,
            float mapW,
            float mapH,
            double elapsedSeconds)
        {
            using var selfHull = GatherHullColliders(em, selfEntity, shipTransformScale, Allocator.Temp);

            float3 from = prevPos;
            from.y = 0f;
            float3 delta = ToroidalMapEcs.ShortestOffsetXZ(from, motorState.Position, mapW, mapH);
            float3 to = from + delta;
            to.y = 0f;
            float moveDistance = math.length(new float2(delta.x, delta.z));

            float bestT = 1f;
            float3 bestNormal = float3.zero;
            bool foundHit = false;

            if (moveDistance > 1e-5f)
            {
                TryCollectHits(
                    from,
                    to,
                    prevRot,
                    motorState.Rotation,
                    moveDistance,
                    from,
                    selfHull,
                    mapW,
                    mapH,
                    ref bestT,
                    ref bestNormal,
                    ref foundHit,
                    em,
                    selfEntity,
                    elapsedSeconds);

                if (foundHit && bestT > MinSweptHitT && bestT < 1f)
                {
                    float3 hitPos = from + delta * bestT;
                    hitPos.y = 0f;
                    motorState.Position = hitPos;
                    RemoveInwardVelocity(ref motorState, bestNormal);
                }
            }

            Depenetrate(
                ref motorState,
                selfHull,
                mapW,
                mapH,
                em,
                selfEntity,
                elapsedSeconds);
        }

        static NativeList<ShipHullColliderElement> GatherHullColliders(
            EntityManager em,
            Entity entity,
            float shipTransformScale,
            Allocator allocator)
        {
            var list = new NativeList<ShipHullColliderElement>(allocator);
            if (em.HasBuffer<ShipHullColliderElement>(entity))
            {
                var buffer = em.GetBuffer<ShipHullColliderElement>(entity);
                for (int i = 0; i < buffer.Length; i++)
                {
                    var element = buffer[i];
                    element.HalfExtents = ClampHalfExtents(element.HalfExtents);
                    list.Add(element);
                }
            }

            if (list.Length == 0)
                list.Add(CreateFallbackCollider(shipTransformScale));

            return list;
        }

        static float3 ClampHalfExtents(float3 halfExtents)
        {
            return new float3(
                math.min(halfExtents.x, MaxHullHalfExtentWorld),
                math.min(halfExtents.y, MaxHullHalfExtentWorld),
                math.min(halfExtents.z, MaxHullHalfExtentWorld));
        }

        static ShipHullColliderElement CreateFallbackCollider(float shipTransformScale)
        {
            float radius = BodyCollisionMath.GetShipHullRadiusWorld(shipTransformScale);
            return new ShipHullColliderElement
            {
                LocalCenter = float3.zero,
                LocalRotation = quaternion.identity,
                HalfExtents = new float3(radius, 0.08f, radius * 0.85f),
            };
        }

        static float GetBoxReachXZ(in ShipCompoundCollisionMath.Obb2D obb) =>
            math.length(obb.HalfExtents);

        static void TryCollectHits(
            float3 shipFrom,
            float3 shipTo,
            quaternion rotFrom,
            quaternion rotTo,
            float moveDistance,
            float3 unwrapOrigin,
            NativeList<ShipHullColliderElement> selfHull,
            float mapW,
            float mapH,
            ref float bestT,
            ref float3 bestNormal,
            ref bool foundHit,
            EntityManager em,
            Entity selfEntity,
            double elapsedSeconds)
        {
            for (int h = 0; h < selfHull.Length; h++)
            {
                var hull = selfHull[h];
                var obbFrom = BuildWorldObb(shipFrom, rotFrom, hull);
                var obbTo = BuildWorldObb(shipTo, rotTo, hull);
                float boxReach = GetBoxReachXZ(obbTo);

                CollectSphereObstacleHits(
                    obbFrom.Center, obbTo.Center, boxReach, unwrapOrigin, moveDistance, mapW, mapH,
                    ref bestT, ref bestNormal, ref foundHit, em, elapsedSeconds);

                CollectShipObstacleHits(
                    obbFrom, obbTo, boxReach, unwrapOrigin, moveDistance, mapW, mapH,
                    ref bestT, ref bestNormal, ref foundHit, em, selfEntity, elapsedSeconds);
            }
        }

        static void CollectSphereObstacleHits(
            float2 boxFrom,
            float2 boxTo,
            float boxReach,
            float3 unwrapOrigin,
            float moveDistance,
            float mapW,
            float mapH,
            ref float bestT,
            ref float3 bestNormal,
            ref bool foundHit,
            EntityManager em,
            double elapsedSeconds)
        {
            float2 shipMid = (boxFrom + boxTo) * 0.5f;

            using var planetQuery = em.CreateEntityQuery(
                ComponentType.ReadOnly<PlanetTag>(),
                ComponentType.ReadOnly<PlanetState>(),
                ComponentType.ReadOnly<LocalTransform>());
            using var planetStates = planetQuery.ToComponentDataArray<PlanetState>(Allocator.Temp);
            using var planetTransforms = planetQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);

            for (int i = 0; i < planetStates.Length; i++)
            {
                var planetState = planetStates[i];
                var planetTransform = planetTransforms[i];
                float planetSize = math.max(0.25f, planetTransform.Scale);

                float bodyRadius = BodyCollisionMath.GetPlanetBodyRadiusWorld(planetSize);
                float3 planetCenter = UnwrapCenter(unwrapOrigin, planetTransform.Position, mapW, mapH);
                var planetCenter2 = new float2(planetCenter.x, planetCenter.z);
                if (IsWithinRange(shipMid, planetCenter2, bodyRadius, boxReach, moveDistance, mapW, mapH))
                {
                    CollectBoxCenterVsSphere(
                        boxFrom, boxTo, boxReach, planetCenter2, bodyRadius,
                        ref bestT, ref bestNormal, ref foundHit);
                }

                float moonRadius = PlanetGemMoonMath.GetMoonBodyRadiusWorld(planetSize, planetState.IsHomePlanet);
                float3 moonCenter = PlanetOrbitMath.GetMoonWorldPositionNear(
                    unwrapOrigin,
                    planetTransform.Position,
                    planetSize,
                    planetState.PlanetLevel,
                    planetState.PlanetId,
                    elapsedSeconds,
                    mapW,
                    mapH);
                var moonCenter2 = new float2(moonCenter.x, moonCenter.z);
                if (IsWithinRange(shipMid, moonCenter2, moonRadius, boxReach, moveDistance, mapW, mapH))
                {
                    CollectBoxCenterVsSphere(
                        boxFrom, boxTo, boxReach, moonCenter2, moonRadius,
                        ref bestT, ref bestNormal, ref foundHit);
                }
            }

            using var asteroidQuery = em.CreateEntityQuery(
                ComponentType.ReadOnly<AsteroidTag>(),
                ComponentType.ReadOnly<AsteroidState>(),
                ComponentType.ReadOnly<LocalTransform>());
            using var asteroidStates = asteroidQuery.ToComponentDataArray<AsteroidState>(Allocator.Temp);
            using var asteroidTransforms = asteroidQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);

            for (int i = 0; i < asteroidStates.Length; i++)
            {
                if (asteroidStates[i].IsDestroyed)
                    continue;

                float asteroidRadius = BodyCollisionMath.GetAsteroidBodyRadiusWorld(asteroidTransforms[i].Scale);
                float3 asteroidCenter = UnwrapCenter(unwrapOrigin, asteroidTransforms[i].Position, mapW, mapH);
                var asteroidCenter2 = new float2(asteroidCenter.x, asteroidCenter.z);
                if (!IsWithinRange(shipMid, asteroidCenter2, asteroidRadius, boxReach, moveDistance, mapW, mapH))
                    continue;

                CollectBoxCenterVsSphere(
                    boxFrom, boxTo, boxReach, asteroidCenter2, asteroidRadius,
                    ref bestT, ref bestNormal, ref foundHit);
            }
        }

        static void CollectShipObstacleHits(
            ShipCompoundCollisionMath.Obb2D obbFrom,
            ShipCompoundCollisionMath.Obb2D obbTo,
            float boxReach,
            float3 unwrapOrigin,
            float moveDistance,
            float mapW,
            float mapH,
            ref float bestT,
            ref float3 bestNormal,
            ref bool foundHit,
            EntityManager em,
            Entity selfEntity,
            double elapsedSeconds)
        {
            _ = elapsedSeconds;
            float2 shipMid = (obbFrom.Center + obbTo.Center) * 0.5f;

            using var shipQuery = em.CreateEntityQuery(
                ComponentType.ReadOnly<ShipTag>(),
                ComponentType.ReadOnly<ShipState>(),
                ComponentType.ReadOnly<LocalTransform>());
            using var shipStates = shipQuery.ToComponentDataArray<ShipState>(Allocator.Temp);
            using var shipTransforms = shipQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
            using var shipEntities = shipQuery.ToEntityArray(Allocator.Temp);

            for (int i = 0; i < shipEntities.Length; i++)
            {
                if (shipEntities[i] == selfEntity || shipStates[i].IsDead)
                    continue;

                var otherTransform = shipTransforms[i];
                float3 otherPos = UnwrapCenter(unwrapOrigin, otherTransform.Position, mapW, mapH);
                float2 otherCenter2 = new float2(otherPos.x, otherPos.z);
                if (!IsWithinRange(shipMid, otherCenter2, boxReach * 2f, boxReach, moveDistance, mapW, mapH))
                    continue;

                using var otherHull = GatherHullColliders(em, shipEntities[i], otherTransform.Scale, Allocator.Temp);
                for (int h = 0; h < otherHull.Length; h++)
                {
                    var otherObb = BuildWorldObb(otherPos, otherTransform.Rotation, otherHull[h]);
                    CollectObbVsObbHit(obbFrom.Center, obbTo.Center, obbTo, otherObb, ref bestT, ref bestNormal, ref foundHit);
                }
            }
        }

        static void CollectBoxCenterVsSphere(
            float2 boxFrom,
            float2 boxTo,
            float boxReach,
            float2 sphereCenter,
            float sphereRadius,
            ref float bestT,
            ref float3 bestNormal,
            ref bool foundHit)
        {
            float combinedRadius = sphereRadius + boxReach;
            float3 from = new float3(boxFrom.x, 0f, boxFrom.y);
            float3 to = new float3(boxTo.x, 0f, boxTo.y);
            float3 center = new float3(sphereCenter.x, 0f, sphereCenter.y);

            if (!BulletCollision.SegmentHitsSphere(from, to, center, combinedRadius, out float3 hitPoint))
                return;

            float3 seg = to - from;
            float segLenSq = math.lengthsq(seg);
            float t = segLenSq > 1e-8f ? math.dot(hitPoint - from, seg) / segLenSq : 0f;
            t = math.clamp(t, 0f, 1f);
            if (t <= MinSweptHitT || t >= bestT)
                return;

            float3 normal = hitPoint - center;
            normal.y = 0f;
            if (math.lengthsq(normal) < 1e-8f)
            {
                normal = from - center;
                normal.y = 0f;
            }

            if (math.lengthsq(normal) < 1e-8f)
                normal = new float3(0f, 0f, 1f);

            bestT = t;
            bestNormal = math.normalize(normal);
            foundHit = true;
        }

        static void CollectObbVsObbHit(
            float2 boxFrom,
            float2 boxTo,
            ShipCompoundCollisionMath.Obb2D selfObb,
            ShipCompoundCollisionMath.Obb2D otherObb,
            ref float bestT,
            ref float3 bestNormal,
            ref bool foundHit)
        {
            const int samples = 4;
            for (int s = 1; s <= samples; s++)
            {
                float t = s / (float)samples;
                float2 center = math.lerp(boxFrom, boxTo, t);
                var movingObb = selfObb;
                movingObb.Center = center;

                if (!ShipCompoundCollisionMath.TryDepenetrateObbFromObb(
                        movingObb, otherObb, out float2 pushNormal, out float _))
                    continue;

                if (t >= bestT)
                    continue;

                bestT = t;
                bestNormal = new float3(pushNormal.x, 0f, pushNormal.y);
                foundHit = true;
            }
        }

        static void Depenetrate(
            ref ShipMotorState motorState,
            NativeList<ShipHullColliderElement> selfHull,
            float mapW,
            float mapH,
            EntityManager em,
            Entity selfEntity,
            double elapsedSeconds)
        {
            const int maxIterations = 4;
            float3 pos = motorState.Position;
            pos.y = 0f;
            quaternion rot = motorState.Rotation;

            for (int iteration = 0; iteration < maxIterations; iteration++)
            {
                float deepestPenetration = 0f;
                float3 bestPush = float3.zero;
                float3 unwrapOrigin = pos;

                for (int h = 0; h < selfHull.Length; h++)
                {
                    var selfObb = BuildWorldObb(pos, rot, selfHull[h]);
                    float boxReach = GetBoxReachXZ(selfObb);
                    float2 shipPos2 = new float2(pos.x, pos.z);

                    using var planetQuery = em.CreateEntityQuery(
                        ComponentType.ReadOnly<PlanetTag>(),
                        ComponentType.ReadOnly<PlanetState>(),
                        ComponentType.ReadOnly<LocalTransform>());
                    using var planetStates = planetQuery.ToComponentDataArray<PlanetState>(Allocator.Temp);
                    using var planetTransforms = planetQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);

                    for (int i = 0; i < planetStates.Length; i++)
                    {
                        var planetState = planetStates[i];
                        var planetTransform = planetTransforms[i];
                        float planetSize = math.max(0.25f, planetTransform.Scale);
                        float bodyRadius = BodyCollisionMath.GetPlanetBodyRadiusWorld(planetSize);
                        float3 planetCenter = UnwrapCenter(unwrapOrigin, planetTransform.Position, mapW, mapH);
                        var planetCenter2 = new float2(planetCenter.x, planetCenter.z);
                        if (IsWithinRange(shipPos2, planetCenter2, bodyRadius, boxReach, 0f, mapW, mapH))
                            TryCollectDepenetrationPush(selfObb, planetCenter2, bodyRadius, ref deepestPenetration, ref bestPush);

                        float moonRadius = PlanetGemMoonMath.GetMoonBodyRadiusWorld(planetSize, planetState.IsHomePlanet);
                        float3 moonCenter = PlanetOrbitMath.GetMoonWorldPositionNear(
                            unwrapOrigin,
                            planetTransform.Position,
                            planetSize,
                            planetState.PlanetLevel,
                            planetState.PlanetId,
                            elapsedSeconds,
                            mapW,
                            mapH);
                        var moonCenter2 = new float2(moonCenter.x, moonCenter.z);
                        if (IsWithinRange(shipPos2, moonCenter2, moonRadius, boxReach, 0f, mapW, mapH))
                            TryCollectDepenetrationPush(selfObb, moonCenter2, moonRadius, ref deepestPenetration, ref bestPush);
                    }

                    using var asteroidQuery = em.CreateEntityQuery(
                        ComponentType.ReadOnly<AsteroidTag>(),
                        ComponentType.ReadOnly<AsteroidState>(),
                        ComponentType.ReadOnly<LocalTransform>());
                    using var asteroidStates = asteroidQuery.ToComponentDataArray<AsteroidState>(Allocator.Temp);
                    using var asteroidTransforms = asteroidQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);

                    for (int i = 0; i < asteroidStates.Length; i++)
                    {
                        if (asteroidStates[i].IsDestroyed)
                            continue;

                        float asteroidRadius = BodyCollisionMath.GetAsteroidBodyRadiusWorld(asteroidTransforms[i].Scale);
                        float3 asteroidCenter = UnwrapCenter(unwrapOrigin, asteroidTransforms[i].Position, mapW, mapH);
                        var asteroidCenter2 = new float2(asteroidCenter.x, asteroidCenter.z);
                        if (!IsWithinRange(shipPos2, asteroidCenter2, asteroidRadius, boxReach, 0f, mapW, mapH))
                            continue;

                        TryCollectDepenetrationPush(selfObb, asteroidCenter2, asteroidRadius, ref deepestPenetration, ref bestPush);
                    }

                    using var shipQuery = em.CreateEntityQuery(
                        ComponentType.ReadOnly<ShipTag>(),
                        ComponentType.ReadOnly<ShipState>(),
                        ComponentType.ReadOnly<LocalTransform>());
                    using var shipStates = shipQuery.ToComponentDataArray<ShipState>(Allocator.Temp);
                    using var shipTransforms = shipQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
                    using var shipEntities = shipQuery.ToEntityArray(Allocator.Temp);

                    for (int i = 0; i < shipEntities.Length; i++)
                    {
                        if (shipEntities[i] == selfEntity || shipStates[i].IsDead)
                            continue;

                        var otherTransform = shipTransforms[i];
                        float3 otherPos = UnwrapCenter(unwrapOrigin, otherTransform.Position, mapW, mapH);
                        float2 otherCenter2 = new float2(otherPos.x, otherPos.z);
                        if (!IsWithinRange(shipPos2, otherCenter2, boxReach * 2f, boxReach, 0f, mapW, mapH))
                            continue;

                        using var otherHull = GatherHullColliders(em, shipEntities[i], otherTransform.Scale, Allocator.Temp);
                        for (int o = 0; o < otherHull.Length; o++)
                        {
                            var otherObb = BuildWorldObb(otherPos, otherTransform.Rotation, otherHull[o]);
                            selfObb.Center = shipPos2;
                            if (!ShipCompoundCollisionMath.TryDepenetrateObbFromObb(
                                    selfObb, otherObb, out float2 pushNormal, out float penetration))
                                continue;

                            float3 push = new float3(pushNormal.x, 0f, pushNormal.y) * (penetration + DepenetrationSkin);
                            if (penetration > deepestPenetration)
                            {
                                deepestPenetration = penetration;
                                bestPush = push;
                            }
                        }
                    }
                }

                if (deepestPenetration <= 0f)
                    break;

                pos += bestPush;
                pos.y = 0f;
            }

            motorState.Position = pos;
            motorState.Position.y = 0f;
        }

        static void TryCollectDepenetrationPush(
            ShipCompoundCollisionMath.Obb2D obb,
            float2 circleCenter,
            float circleRadius,
            ref float deepestPenetration,
            ref float3 bestPush)
        {
            if (!ShipCompoundCollisionMath.TryDepenetrateObbFromCircle(
                    obb, circleCenter, circleRadius, out float2 pushNormal, out float penetration))
                return;

            float3 push = new float3(pushNormal.x, 0f, pushNormal.y) * (penetration + DepenetrationSkin);
            if (penetration > deepestPenetration)
            {
                deepestPenetration = penetration;
                bestPush = push;
            }
        }

        static bool IsWithinRange(
            float2 shipPos,
            float2 obstaclePos,
            float obstacleRadius,
            float boxReach,
            float moveDistance,
            float mapW,
            float mapH)
        {
            float3 ship = new float3(shipPos.x, 0f, shipPos.y);
            float3 obstacle = new float3(obstaclePos.x, 0f, obstaclePos.y);
            float dist = ToroidalMapEcs.ToroidalDistance(ship, obstacle, mapW, mapH);
            float range = obstacleRadius + boxReach + moveDistance + CullingPadding;
            return dist <= range;
        }

        static ShipCompoundCollisionMath.Obb2D BuildWorldObb(float3 shipPos, quaternion shipRot, in ShipHullColliderElement hull)
        {
            float3 worldCenter = shipPos + math.rotate(shipRot, hull.LocalCenter);
            quaternion worldRot = math.mul(shipRot, hull.LocalRotation);
            return ShipCompoundCollisionMath.BuildObb2D(worldCenter, worldRot, hull.HalfExtents);
        }

        static float3 UnwrapCenter(float3 unwrapOrigin, float3 centerWorld, float mapW, float mapH)
        {
            float3 center = unwrapOrigin + ToroidalMapEcs.ShortestOffsetXZ(unwrapOrigin, centerWorld, mapW, mapH);
            center.y = 0f;
            return center;
        }

        static void RemoveInwardVelocity(ref ShipMotorState motorState, float3 surfaceNormal)
        {
            float3 vel = motorState.Velocity;
            vel.y = 0f;
            float vn = math.dot(vel, surfaceNormal);
            if (vn < 0f)
                vel -= surfaceNormal * vn;
            motorState.Velocity = vel;
        }
    }
}
