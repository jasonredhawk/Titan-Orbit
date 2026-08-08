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
    /// <para>
    /// Asteroids use <see cref="ShipCollisionImpulseLogic"/> with virtual mass (rocks stay static).
    /// Planets / moons keep infinite-mass wall reflect. Ship↔ship seam pairs use two-body impulse.
    /// </para>
    /// </summary>
    public static class ShipToroidalWorldCollisionLogic
    {
        /// <summary>
        /// [TITAN-ORBIT] Restitution used for infinite-mass walls (planets / moons) when Unity
        /// Physics is not in the loop (cross-seam contacts).
        /// </summary>
        public const float WorldRestitution = ShipCollisionImpulseLogic.DefaultInfiniteMassRestitution;

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
        /// No-op when the pair is on the same tile (Unity Physics + bounce/friction + drive contact
        /// reject own that contact) or not overlapping.
        /// Pass <paramref name="bodyMass"/> &gt; 0 with <paramref name="shipMass"/> for asteroids
        /// (virtual mass, rock stays put); leave bodyMass ≤ 0 for infinite-mass planets.
        /// </summary>
        /// <param name="shipPos">Ship position — written when depenetrating.</param>
        /// <param name="shipVel">Ship linear velocity — written when reflecting.</param>
        /// <param name="shipRadius">Ship hull sphere radius.</param>
        /// <param name="bodyPos">Static / kinematic world body center (logical sim).</param>
        /// <param name="bodyRadius">World body sphere radius.</param>
        /// <param name="mapW">Map width.</param>
        /// <param name="mapH">Map height.</param>
        /// <param name="restitution">Bounce coefficient.</param>
        /// <param name="friction">
        /// Tangential grip (0 = ice). Asteroids pass <c>AsteroidSettings.Friction</c>; planets pass 0.
        /// </param>
        /// <param name="dt">Fixed step for friction damping (ignored when friction ≤ 0).</param>
        /// <param name="shipMass">Ship collision mass (ramming mass). Ignored when bodyMass ≤ 0.</param>
        /// <param name="bodyMass">
        /// Virtual asteroid mass (&gt; 0). ≤ 0 ⇒ infinite-mass wall (planets).
        /// </param>
        /// <returns>True when a penetration was resolved this call.</returns>
        public static bool TryResolveShipVsWorldSphere(
            ref float3 shipPos,
            ref float3 shipVel,
            float shipRadius,
            float3 bodyPos,
            float bodyRadius,
            float mapW,
            float mapH,
            float restitution,
            float friction = 0f,
            float dt = 0f,
            float shipMass = 0f,
            float bodyMass = 0f)
        {
            // --- Same tile: leave to Unity Physics + bounce/friction + drive inward-reject ---
            // [TITAN-ORBIT] Avoids double-bounce near the origin where Euclidean contacts already work.
            // Progressive grind dig-in is stopped by ShipAsteroidContactState (motor cannot push into
            // the rock). Do NOT sphere-push from AABB radii — compound hulls over-estimate and shove.
            if (!NeedsToroidalResolve(shipPos, bodyPos, mapW, mapH))
                return false;

            // --- Toroidal separation (ship → body) ---
            float3 offset = ToroidalMapEcs.ShortestOffsetXZ(shipPos, bodyPos, mapW, mapH);
            float dist = math.length(offset);
            float minDist = math.max(0.01f, shipRadius + bodyRadius);
            if (dist >= minDist)
                return false;

            // --- Separation normal: from body toward ship ---
            float3 normal = ComputeSeparationNormal(offset, dist, shipVel);

            // --- Depenetrate ship in unbounded sim space ---
            float penetration = minDist - dist;
            shipPos += normal * penetration;
            shipPos.y = 0f;

            // --- Normal bounce ---
            float3 vel = shipVel;
            vel.y = 0f;
            if (bodyMass > 0f && shipMass > 0f)
            {
                // [TITAN-ORBIT] Finite virtual mass — rock does not move; mass still shapes rebound.
                ShipCollisionImpulseLogic.ApplyShipVsStaticMassiveImpulse(
                    ref vel, normal, shipMass, bodyMass, restitution);
            }
            else
            {
                // Infinite-mass wall (planets).
                ShipCollisionImpulseLogic.ApplyInfiniteMassWallImpulse(ref vel, normal, restitution);
            }

            // --- Asteroid grip across seams (PhysX never sees this pair) ---
            if (friction > 0f)
                vel = AsteroidColliderMaterialLogic.ApplyTangentialFriction(vel, normal, friction, dt);

            shipVel = vel;
            return true;
        }

        /// <summary>
        /// Cross-seam ship↔ship sphere resolve with two-body mass-aware impulse.
        /// Same-tile pairs are left to PhysX + <c>ShipCollisionBounceSystem</c>.
        /// Both ships depenetrate along the shared normal (mass-weighted split).
        /// </summary>
        /// <param name="posA">Ship A position — written when depenetrating.</param>
        /// <param name="velA">Ship A linear velocity — written on bounce.</param>
        /// <param name="radiusA">Ship A hull radius.</param>
        /// <param name="massA">Ship A collision mass.</param>
        /// <param name="posB">Ship B position — written when depenetrating.</param>
        /// <param name="velB">Ship B linear velocity — written on bounce.</param>
        /// <param name="radiusB">Ship B hull radius.</param>
        /// <param name="massB">Ship B collision mass.</param>
        /// <param name="mapW">Map width.</param>
        /// <param name="mapH">Map height.</param>
        /// <param name="restitution">Bounce coefficient (typically ship–ship default).</param>
        /// <returns>True when a penetration was resolved this call.</returns>
        public static bool TryResolveShipVsShip(
            ref float3 posA,
            ref float3 velA,
            float radiusA,
            float massA,
            ref float3 posB,
            ref float3 velB,
            float radiusB,
            float massB,
            float mapW,
            float mapH,
            float restitution)
        {
            if (!NeedsToroidalResolve(posA, posB, mapW, mapH))
                return false;

            // Offset from A toward B along the shortest toroidal path.
            float3 offsetAToB = ToroidalMapEcs.ShortestOffsetXZ(posA, posB, mapW, mapH);
            float dist = math.length(offsetAToB);
            float minDist = math.max(0.01f, radiusA + radiusB);
            if (dist >= minDist)
                return false;

            // Normal from B toward A (matches ApplyTwoBodyImpulse / collision-event convention).
            float3 normalAFromB;
            if (dist < 1e-5f)
            {
                float3 planarRel = new float3(velA.x - velB.x, 0f, velA.z - velB.z);
                if (math.lengthsq(planarRel) > 1e-6f)
                    normalAFromB = -math.normalize(planarRel);
                else
                    normalAFromB = new float3(1f, 0f, 0f);
            }
            else
            {
                // offsetAToB points A→B, so B→A is the opposite.
                normalAFromB = -offsetAToB / dist;
            }

            // --- Mass-weighted depenetration (heavier ship moves less) ---
            float mA = math.max(ShipCollisionImpulseLogic.MinCollisionMass, massA);
            float mB = math.max(ShipCollisionImpulseLogic.MinCollisionMass, massB);
            float penetration = minDist - dist;
            float invSum = 1f / (mA + mB);
            posA += normalAFromB * (penetration * mB * invSum);
            posB -= normalAFromB * (penetration * mA * invSum);
            posA.y = 0f;
            posB.y = 0f;

            // --- Energy transfer ---
            ShipCollisionImpulseLogic.ApplyTwoBodyImpulse(
                ref velA, ref velB, normalAFromB, mA, mB, restitution);
            return true;
        }

        /// <summary>
        /// Euclidean ship↔sphere depenetration in unbounded sim space (no toroidal early-out).
        /// Use when the obstacle was already placed on the ship's map tile via
        /// <see cref="PlanetOrbitMath.GetMoonWorldPositionNear"/> — Unity Physics still sees only
        /// the canonical moon collider a full map width away, so same-tile PhysX cannot bounce.
        /// </summary>
        /// <param name="shipPos">Ship position — written when depenetrating.</param>
        /// <param name="shipVel">Ship linear velocity — written when reflecting.</param>
        /// <param name="shipRadius">Ship hull sphere radius.</param>
        /// <param name="bodyPos">Obstacle center already unwrapped near the ship.</param>
        /// <param name="bodyRadius">Obstacle sphere radius.</param>
        /// <param name="restitution">Bounce coefficient (typically <see cref="WorldRestitution"/>).</param>
        /// <returns>True when a penetration was resolved this call.</returns>
        public static bool TryResolveShipVsNearWorldSphere(
            ref float3 shipPos,
            ref float3 shipVel,
            float shipRadius,
            float3 bodyPos,
            float bodyRadius,
            float restitution)
        {
            // --- Planar Euclidean separation (body already on the ship's tile) ---
            // [TITAN-ORBIT] Do not call NeedsToroidalResolve / ShortestOffset here — Near placement
            // already chose the correct copy. Toroidal shortest to the *canonical* moon would look
            // like center-overlap and shove the hull every tick (stepped orbit / dock snap-back).
            float3 offset = bodyPos - shipPos;
            offset.y = 0f;
            float dist = math.length(offset);
            float minDist = math.max(0.01f, shipRadius + bodyRadius);
            if (dist >= minDist)
                return false;

            float3 normal = ComputeSeparationNormal(offset, dist, shipVel);

            // --- Depenetrate in unbounded space (stay on the Near tile) ---
            float penetration = minDist - dist;
            shipPos += normal * penetration;
            shipPos.y = 0f;

            float3 vel = shipVel;
            ShipCollisionImpulseLogic.ApplyInfiniteMassWallImpulse(ref vel, normal, restitution);
            shipVel = vel;
            return true;
        }

        /// <summary>
        /// Separation normal from body toward ship. On exact center overlap, push opposite
        /// planar velocity (or +X if parked).
        /// </summary>
        static float3 ComputeSeparationNormal(float3 offsetShipToBodyOrBodyMinusShip, float dist, float3 shipVel)
        {
            // Callers pass either (body - ship) Euclidean or ShortestOffset(ship, body).
            // We want normal from body toward ship = -offset / dist when offset is ship→body.
            if (dist < 1e-5f)
            {
                float3 planarVel = new float3(shipVel.x, 0f, shipVel.z);
                if (math.lengthsq(planarVel) > 1e-6f)
                    return -math.normalize(planarVel);
                return new float3(1f, 0f, 0f);
            }

            return -offsetShipToBodyOrBodyMinusShip / dist;
        }
    }
}
