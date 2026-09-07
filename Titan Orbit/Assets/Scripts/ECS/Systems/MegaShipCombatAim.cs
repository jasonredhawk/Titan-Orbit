using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// MEGA and regular ships use one Physics sphere. These helpers aim and test
    /// against that sphere (compound child walks still work if an old blob remains).
    /// </summary>
    public static class MegaShipCombatAim
    {
        /// <summary>
        /// World aim point for a ship: collider-box center when this is a MEGA, else the pivot.
        /// </summary>
        public static float3 GetAimPoint(EntityManager em, Entity ship, in LocalTransform xf)
        {
            if (em.HasComponent<MegaShipState>(ship)
                && em.GetComponentData<MegaShipState>(ship).IsMega
                && TryGetColliderBoxLocal(em, ship, out float3 localCenter, out _))
            {
                float3 world = xf.Position + math.rotate(xf.Rotation, localCenter * xf.Scale);
                world.y = xf.Position.y;
                return world;
            }

            return xf.Position;
        }

        /// <summary>
        /// Covering-sphere radius around <see cref="GetAimPoint"/>. Slack for nearest-obstacle
        /// search and camera only — combat traces use <see cref="TryHitBulletSegment"/> against
        /// each baked part, not this bubble or the covering box.
        /// </summary>
        public static float GetHitRadiusWorld(EntityManager em, Entity ship, in PhysicsCollider collider, float scale)
        {
            float fallback = ShipToroidalWorldCollisionLogic.GetShipCollisionRadiusWorld(collider, scale);
            if (!em.HasComponent<MegaShipState>(ship)
                || !em.GetComponentData<MegaShipState>(ship).IsMega
                || !collider.Value.IsCreated)
                return fallback;

            Aabb aabb = collider.Value.Value.CalculateAabb();
            float3 e = aabb.Extents;
            float r = math.length(new float3(e.x, 0f, e.z));
            r *= math.max(0.01f, scale);
            return math.max(fallback, r);
        }

        /// <summary>
        /// Tight yaw-aligned XZ box for gameplay overlap (moon dock, etc.).
        /// Unity.Physics <c>Aabb.Extents</c> is the full size (Max−Min); this returns half-extents
        /// so a part must actually reach the other volume. Same box as
        /// <see cref="TryGetHitBoxWorld"/>.
        /// </summary>
        public static bool TryGetOverlapBoxWorld(
            EntityManager em,
            Entity ship,
            in LocalTransform xf,
            out float3 worldCenter,
            out float2 halfExtents,
            out float yawRadians)
        {
            worldCenter = xf.Position;
            halfExtents = float2.zero;
            yawRadians = 0f;
            if (!em.HasComponent<MegaShipState>(ship)
                || !em.GetComponentData<MegaShipState>(ship).IsMega
                || !em.HasComponent<PhysicsCollider>(ship))
                return false;

            var collider = em.GetComponentData<PhysicsCollider>(ship);
            if (!collider.Value.IsCreated)
                return false;

            Aabb aabb = collider.Value.Value.CalculateAabb();
            float3 halfLocal = aabb.Extents * 0.5f;
            float scale = math.max(0.01f, xf.Scale);
            worldCenter = xf.Position + math.rotate(xf.Rotation, aabb.Center * scale);
            worldCenter.y = xf.Position.y;
            halfExtents = new float2(halfLocal.x, halfLocal.z) * scale;
            float3 fwd = math.mul(xf.Rotation, new float3(0f, 0f, 1f));
            yawRadians = math.atan2(fwd.x, fwd.z);
            return halfExtents.x > 0.01f && halfExtents.y > 0.01f;
        }

        /// <summary>
        /// Covering yaw-aligned XZ box (compound AABB). Camera, moon-dock, and a cheap
        /// broadphase reject. Bullets must use <see cref="TryHitBulletSegment"/> so they
        /// hit individual part colliders instead of this outer rectangle.
        /// <paramref name="hullMidY"/> is the 3D AABB center height for tracer lift.
        /// </summary>
        public static bool TryGetHitBoxWorld(
            EntityManager em,
            Entity ship,
            in LocalTransform xf,
            out float3 worldCenter,
            out float2 halfExtents,
            out float yawRadians)
        {
            return TryGetHitBoxWorld(em, ship, xf, out worldCenter, out halfExtents, out yawRadians, out _);
        }

        /// <inheritdoc cref="TryGetHitBoxWorld(EntityManager,Entity,LocalTransform,out float3,out float2,out float)"/>
        public static bool TryGetHitBoxWorld(
            EntityManager em,
            Entity ship,
            in LocalTransform xf,
            out float3 worldCenter,
            out float2 halfExtents,
            out float yawRadians,
            out float hullMidY)
        {
            worldCenter = xf.Position;
            halfExtents = float2.zero;
            yawRadians = 0f;
            hullMidY = xf.Position.y;
            if (!em.HasComponent<MegaShipState>(ship)
                || !em.GetComponentData<MegaShipState>(ship).IsMega
                || !TryGetColliderBoxLocal(em, ship, out float3 localCenter, out float3 localExtents))
                return false;

            float scale = math.max(0.01f, xf.Scale);
            float3 localScaled = localCenter * scale;
            worldCenter = xf.Position + math.rotate(xf.Rotation, localScaled);
            hullMidY = xf.Position.y + localScaled.y;
            worldCenter.y = xf.Position.y;
            halfExtents = new float2(localExtents.x, localExtents.z) * scale;
            float3 fwd = math.mul(xf.Rotation, new float3(0f, 0f, 1f));
            yawRadians = math.atan2(fwd.x, fwd.z);
            return halfExtents.x > 0.01f && halfExtents.y > 0.01f;
        }

        /// <summary>
        /// Local collider center / extents for camera framing and tracer lift.
        /// </summary>
        public static bool TryGetColliderBoxLocal(
            EntityManager em,
            Entity ship,
            out float3 localCenter,
            out float3 localExtents)
        {
            localCenter = float3.zero;
            localExtents = float3.zero;
            if (!em.HasComponent<PhysicsCollider>(ship))
                return false;

            var collider = em.GetComponentData<PhysicsCollider>(ship);
            if (!collider.Value.IsCreated)
                return false;

            Aabb aabb = collider.Value.Value.CalculateAabb();
            localCenter = aabb.Center;
            // Unity.Physics Extents is Max−Min (full size). Combat / framing want half-extents.
            localExtents = aabb.Extents * 0.5f;
            return true;
        }

        /// <summary>
        /// World-space XZ view radius and hull-top Y for a MEGA (or any ship with a box collider).
        /// </summary>
        public static bool TryGetHullView(
            EntityManager em,
            Entity ship,
            in LocalTransform xf,
            out float3 worldCenter,
            out float viewRadius,
            out float hullTopY)
        {
            worldCenter = xf.Position;
            viewRadius = 0f;
            hullTopY = xf.Position.y;
            if (!TryGetColliderBoxLocal(em, ship, out float3 localCenter, out float3 localExtents))
                return false;

            float scale = math.max(0.01f, xf.Scale);
            worldCenter = xf.Position + math.rotate(xf.Rotation, localCenter * scale);
            worldCenter.y = xf.Position.y;
            viewRadius = math.length(new float2(localExtents.x, localExtents.z)) * scale;
            hullTopY = xf.Position.y + math.abs(localCenter.y * scale) + localExtents.y * scale;
            return viewRadius > 0.01f;
        }

        /// <summary>
        /// One baked MEGA part in world XZ for Burst cosmetic sweeps.
        /// Sphere parts set <see cref="SphereRadius"/>; boxes set half-extents + yaw.
        /// </summary>
        public struct MegaPartSweepShape
        {
            public float3 WorldCenter;
            public float2 BoxHalfExtents;
            public float BoxYawRadians;
            public float SphereRadius;
        }

        /// <summary>
        /// Appends each compound child as a world box/sphere (no bullet pad).
        /// Used by client Burst tracers so they stop on the same parts as
        /// <see cref="TryHitBulletSegment"/> instead of the covering hull sphere.
        /// </summary>
        public static bool TryAppendPartSweepShapes(
            EntityManager em,
            Entity ship,
            in LocalTransform xf,
            List<MegaPartSweepShape> into)
        {
            if (into == null
                || !em.HasComponent<MegaShipState>(ship)
                || !em.GetComponentData<MegaShipState>(ship).IsMega
                || !em.HasComponent<PhysicsCollider>(ship))
                return false;

            var physics = em.GetComponentData<PhysicsCollider>(ship);
            if (!physics.Value.IsCreated)
                return false;

            int before = into.Count;
            AppendColliderPartShapes(physics, xf, into);
            return into.Count > before;
        }

        /// <summary>
        /// Swept bullet vs each baked MEGA part collider (compound children).
        /// The covering AABB is only a broadphase reject — a miss on every part
        /// means the shot passes through a gap in the hull.
        /// </summary>
        public static bool TryHitBulletSegment(
            EntityManager em,
            Entity ship,
            in LocalTransform xf,
            float3 from,
            float3 to,
            float pad,
            float mapW,
            float mapH,
            out float3 hitPoint,
            out float hullMidY)
        {
            hitPoint = to;
            hullMidY = xf.Position.y;
            if (!em.HasComponent<MegaShipState>(ship)
                || !em.GetComponentData<MegaShipState>(ship).IsMega
                || !em.HasComponent<PhysicsCollider>(ship))
                return false;

            var physics = em.GetComponentData<PhysicsCollider>(ship);
            if (!physics.Value.IsCreated)
                return false;

            if (!TryGetHitBoxWorld(em, ship, xf, out float3 boxCenter, out float2 boxHe, out float boxYaw, out _))
                return false;
            if (!BulletCollision.SegmentHitsOrientedBoxToroidal(
                    from, to, boxCenter, boxHe + pad, boxYaw, mapW, mapH, out _))
                return false;

            if (TryHitColliderParts(physics, xf, from, to, pad, mapW, mapH, out bool anyPart, out hitPoint, out hullMidY))
                return anyPart;

            return BulletCollision.SegmentHitsOrientedBoxToroidal(
                from, to, boxCenter, boxHe + pad, boxYaw, mapW, mapH, out hitPoint);
        }

        /// <summary>
        /// Walks the compound (or the single baked collider). Returns false when the
        /// blob cannot be read so the caller can fall back to the covering box.
        /// </summary>
        static unsafe bool TryHitColliderParts(
            in PhysicsCollider physics,
            in LocalTransform xf,
            float3 from,
            float3 to,
            float pad,
            float mapW,
            float mapH,
            out bool anyHit,
            out float3 hitPoint,
            out float hullMidY)
        {
            anyHit = false;
            hitPoint = to;
            hullMidY = xf.Position.y;

            Collider* root = (Collider*)physics.Value.GetUnsafePtr();
            if (root == null)
                return false;

            float bestT = float.MaxValue;
            float3 bestHit = to;
            float bestY = xf.Position.y;
            bool walked = false;

            if (root->Type == ColliderType.Compound)
            {
                var compound = (CompoundCollider*)root;
                int n = compound->NumChildren;
                if (n <= 0)
                    return false;

                walked = true;
                for (int i = 0; i < n; i++)
                {
                    ref CompoundCollider.Child child = ref compound->Children[i];
                    if (!TryHitOnePart(
                            child.Collider,
                            child.CompoundFromChild,
                            xf, from, to, pad, mapW, mapH,
                            out float3 partHit, out float partY))
                        continue;

                    float t = BulletCollision.GetSegmentHitParameter(from, to, partHit);
                    if (t >= bestT)
                        continue;
                    bestT = t;
                    bestHit = partHit;
                    bestY = partY;
                    anyHit = true;
                }
            }
            else
            {
                walked = true;
                if (TryHitOnePart(
                        root,
                        RigidTransform.identity,
                        xf, from, to, pad, mapW, mapH,
                        out bestHit, out bestY))
                    anyHit = true;
            }

            if (anyHit)
            {
                hitPoint = bestHit;
                hullMidY = bestY;
            }

            return walked;
        }

        static unsafe void AppendColliderPartShapes(
            in PhysicsCollider physics,
            in LocalTransform xf,
            List<MegaPartSweepShape> into)
        {
            Collider* root = (Collider*)physics.Value.GetUnsafePtr();
            if (root == null)
                return;

            if (root->Type == ColliderType.Compound)
            {
                var compound = (CompoundCollider*)root;
                int n = compound->NumChildren;
                for (int i = 0; i < n; i++)
                {
                    ref CompoundCollider.Child child = ref compound->Children[i];
                    if (!TryGetPartWorldShape(
                            child.Collider, child.CompoundFromChild, xf,
                            out MegaPartSweepShape shape))
                        continue;
                    into.Add(shape);
                }

                return;
            }

            if (TryGetPartWorldShape(root, RigidTransform.identity, xf, out MegaPartSweepShape single))
                into.Add(single);
        }

        static unsafe bool TryGetPartWorldShape(
            Collider* part,
            in RigidTransform compoundFromChild,
            in LocalTransform xf,
            out MegaPartSweepShape shape)
        {
            shape = default;
            if (part == null)
                return false;

            float scale = math.max(0.01f, xf.Scale);
            float3 localCenter;
            quaternion localRot;
            float2 halfXz;
            float radius;

            if (part->Type == ColliderType.Box)
            {
                var box = (Unity.Physics.BoxCollider*)part;
                BoxGeometry g = box->Geometry;
                localCenter = math.transform(compoundFromChild, g.Center);
                localRot = math.mul(compoundFromChild.rot, g.Orientation);
                float3 half = g.Size * 0.5f;
                halfXz = new float2(half.x, half.z) * scale;
                radius = 0f;
            }
            else if (part->Type == ColliderType.Sphere)
            {
                var sphere = (Unity.Physics.SphereCollider*)part;
                SphereGeometry g = sphere->Geometry;
                localCenter = math.transform(compoundFromChild, g.Center);
                localRot = compoundFromChild.rot;
                halfXz = float2.zero;
                radius = g.Radius * scale;
            }
            else
            {
                Aabb aabb = part->CalculateAabb();
                localCenter = math.transform(compoundFromChild, aabb.Center);
                localRot = compoundFromChild.rot;
                float3 half = aabb.Extents * 0.5f;
                halfXz = new float2(half.x, half.z) * scale;
                radius = 0f;
            }

            float3 worldCenter = xf.Position + math.rotate(xf.Rotation, localCenter * scale);
            float partMidY = xf.Position.y + localCenter.y * scale;
            worldCenter.y = partMidY;

            if (halfXz.x < 0.01f && halfXz.y < 0.01f && radius < 0.001f)
                return false;

            float yaw = 0f;
            if (radius <= 0.001f)
            {
                quaternion worldRot = math.mul(xf.Rotation, localRot);
                float3 fwd = math.mul(worldRot, new float3(0f, 0f, 1f));
                yaw = math.atan2(fwd.x, fwd.z);
            }

            shape = new MegaPartSweepShape
            {
                WorldCenter = worldCenter,
                BoxHalfExtents = halfXz,
                BoxYawRadians = yaw,
                SphereRadius = radius,
            };
            return true;
        }

        static unsafe bool TryHitOnePart(
            Collider* part,
            in RigidTransform compoundFromChild,
            in LocalTransform xf,
            float3 from,
            float3 to,
            float pad,
            float mapW,
            float mapH,
            out float3 hitPoint,
            out float partMidY)
        {
            hitPoint = to;
            partMidY = xf.Position.y;
            if (part == null)
                return false;

            float scale = math.max(0.01f, xf.Scale);
            float3 localCenter;
            quaternion localRot;
            float2 halfXz;
            float radius;

            if (part->Type == ColliderType.Box)
            {
                var box = (Unity.Physics.BoxCollider*)part;
                BoxGeometry g = box->Geometry;
                localCenter = math.transform(compoundFromChild, g.Center);
                localRot = math.mul(compoundFromChild.rot, g.Orientation);
                float3 half = g.Size * 0.5f;
                halfXz = new float2(half.x, half.z) * scale + pad;
                radius = 0f;
            }
            else if (part->Type == ColliderType.Sphere)
            {
                var sphere = (Unity.Physics.SphereCollider*)part;
                SphereGeometry g = sphere->Geometry;
                localCenter = math.transform(compoundFromChild, g.Center);
                localRot = compoundFromChild.rot;
                halfXz = float2.zero;
                radius = g.Radius * scale + pad;
            }
            else
            {
                Aabb aabb = part->CalculateAabb();
                localCenter = math.transform(compoundFromChild, aabb.Center);
                localRot = compoundFromChild.rot;
                float3 half = aabb.Extents * 0.5f;
                halfXz = new float2(half.x, half.z) * scale + pad;
                radius = 0f;
            }

            float3 worldCenter = xf.Position + math.rotate(xf.Rotation, localCenter * scale);
            partMidY = xf.Position.y + localCenter.y * scale;
            worldCenter.y = xf.Position.y;

            if (radius > 0.001f)
            {
                if (!BulletCollision.SegmentHitsSphereToroidal(
                        from, to, worldCenter, radius, mapW, mapH, out hitPoint))
                    return false;
            }
            else
            {
                quaternion worldRot = math.mul(xf.Rotation, localRot);
                float3 fwd = math.mul(worldRot, new float3(0f, 0f, 1f));
                float yaw = math.atan2(fwd.x, fwd.z);
                if (!BulletCollision.SegmentHitsOrientedBoxToroidal(
                        from, to, worldCenter, halfXz, yaw, mapW, mapH, out hitPoint))
                    return false;
            }

            hitPoint.y = partMidY;
            return true;
        }
    }
}
