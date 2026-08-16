using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// MEGA hull colliders are a compound of the prefab's part colliders.
    /// These helpers aim and test against that collider footprint.
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
        /// Sphere radius around <see cref="GetAimPoint"/> that covers the MEGA box.
        /// Does not add the pivot-to-box offset — that belongs on the center, not the radius.
        /// Regular ships keep the AABB-extent helper.
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
            localExtents = aabb.Extents;
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
    }
}
