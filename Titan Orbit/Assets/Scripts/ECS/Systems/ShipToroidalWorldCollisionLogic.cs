using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Mathematics;
using Unity.Physics;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Shared ship↔world hull resolve on the toroidal map. Unity Physics only sees absolute
    /// <see cref="Unity.Transforms.LocalTransform"/> positions, so after the local ship flies past a
    /// map edge the nearest displayed planet/asteroid can sit next to the hull while the sim bodies
    /// are still ~one map width apart — Euclidean contacts miss. This math uses
    /// <see cref="ToroidalMapEcs.ShortestOffsetXZ"/> (same idea as <see cref="BulletCollision"/>)
    /// so bounce still works across seams. Called from <see cref="ShipToroidalWorldCollisionSystem"/>
    /// on both server and predicted client — never move shared planet transforms for one client's display.
    /// </summary>
    public static class ShipToroidalWorldCollisionLogic
    {
        /// <summary>
        /// [TITAN-ORBIT] Restitution used when Unity Physics is not in the loop (cross-seam contacts).
        /// Matches world-static bake (~0.5 on planets/asteroids/moons).
        /// </summary>
        public const float WorldRestitution = 0.5f;

        /// <summary>
        /// If the raw XZ delta and the toroidal shortest offset differ, the pair is on different
        /// map tiles and Unity Physics will not generate a contact.
        /// </summary>
        const float DifferentTileEpsilonSq = 0.01f;

        /// <summary>
        /// Effective ship hull radius for sphere vs sphere tests. Prefers the baked
        /// <see cref="PhysicsCollider"/> AABB on XZ; falls back to <see cref="BodyCollisionMath"/>.
        /// </summary>
        /// <param name="physicsCollider">Ship hull collider (sphere/box/capsule/compound).</param>
        /// <param name="transformScale">Entity <c>LocalTransform.Scale</c>.</param>
        /// <returns>World-space radius used for toroidal overlap tests.</returns>
        public static float GetShipCollisionRadiusWorld(in PhysicsCollider physicsCollider, float transformScale)
        {
            float fallback = BodyCollisionMath.GetShipHullRadiusWorld(transformScale);
            // [PHYSICS] BlobAssetReference — IsCreated is false before hull bake/sync.
            if (!physicsCollider.Value.IsCreated)
                return fallback;

            // --- Collider-local AABB, then apply entity scale ---
            // [PHYSICS] Chassis bake often stores presentation-sized geometry with Scale ≈ 1.
            Aabb aabb = physicsCollider.Value.Value.CalculateAabb();
            float r = math.max(aabb.Extents.x, aabb.Extents.z);
            r *= math.max(0.01f, transformScale);
            if (r < BodyCollisionMath.MinShipHullRadiusWorld)
                return fallback;
            return r;
        }

        /// <summary>
        /// True when Euclidean XZ separation differs from the toroidal shortest path — i.e. a wrap
        /// tile is involved and Unity Physics will not bounce this pair.
        /// </summary>
        /// <param name="shipPos">Ship sim position (may be unbounded).</param>
        /// <param name="bodyPos">Planet / asteroid / moon sim position.</param>
        /// <param name="mapW">Toroidal map width.</param>
        /// <param name="mapH">Toroidal map height.</param>
        public static bool NeedsToroidalResolve(float3 shipPos, float3 bodyPos, float mapW, float mapH)
        {
            float3 raw = bodyPos - shipPos;
            raw.y = 0f;
            float3 shortest = ToroidalMapEcs.ShortestOffsetXZ(shipPos, bodyPos, mapW, mapH);
            return math.lengthsq(raw - shortest) > DifferentTileEpsilonSq;
        }

        /// <summary>
        /// If the ship overlaps the world sphere on the torus, push the ship out and bounce velocity.
        /// No-op when the pair is on the same tile (Unity Physics owns that contact) or not overlapping.
        /// </summary>
        /// <param name="shipPos">Ship position — written when depenetrating.</param>
        /// <param name="shipVel">Ship linear velocity — written when reflecting.</param>
        /// <param name="shipRadius">Ship hull sphere radius.</param>
        /// <param name="bodyPos">Static / kinematic world body center (logical sim).</param>
        /// <param name="bodyRadius">World body sphere radius.</param>
        /// <param name="mapW">Map width.</param>
        /// <param name="mapH">Map height.</param>
        /// <param name="restitution">Bounce coefficient (typically <see cref="WorldRestitution"/>).</param>
        /// <returns>True when a penetration was resolved this call.</returns>
        public static bool TryResolveShipVsWorldSphere(
            ref float3 shipPos,
            ref float3 shipVel,
            float shipRadius,
            float3 bodyPos,
            float bodyRadius,
            float mapW,
            float mapH,
            float restitution)
        {
            // --- Same tile: leave to Unity Physics ---
            // [TITAN-ORBIT] Avoids double-bounce near the origin where Euclidean contacts already work.
            if (!NeedsToroidalResolve(shipPos, bodyPos, mapW, mapH))
                return false;

            // --- Toroidal separation (ship → body) ---
            float3 offset = ToroidalMapEcs.ShortestOffsetXZ(shipPos, bodyPos, mapW, mapH);
            float dist = math.length(offset);
            float minDist = math.max(0.01f, shipRadius + bodyRadius);
            if (dist >= minDist)
                return false;

            // --- Separation normal: from body toward ship ---
            float3 normal;
            if (dist < 1e-5f)
            {
                // Exact center overlap — push opposite planar velocity, or +X if parked.
                float3 planarVel = new float3(shipVel.x, 0f, shipVel.z);
                if (math.lengthsq(planarVel) > 1e-6f)
                    normal = -math.normalize(planarVel);
                else
                    normal = new float3(1f, 0f, 0f);
            }
            else
            {
                normal = -offset / dist;
            }

            // --- Depenetrate ship in unbounded sim space ---
            float penetration = minDist - dist;
            shipPos += normal * penetration;
            shipPos.y = 0f;

            // --- Reflect inward velocity (static world body) ---
            float3 vel = shipVel;
            vel.y = 0f;
            float vn = math.dot(vel, normal);
            if (vn < 0f)
            {
                float e = math.saturate(restitution);
                vel -= normal * vn * (1f + e);
            }

            shipVel = vel;
            return true;
        }
    }
}
