using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Mathematics;
using Unity.Physics;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// One torus hull-collision path (Starblast model). Uses
    /// <see cref="ToroidalMapEcs.ShortestOffsetXZ"/> for every ship↔world and ship↔ship pair —
    /// same-tile and seams. PhysX integrates ship position only; it does not bounce hulls.
    /// Called from <see cref="ShipToroidalWorldCollisionSystem"/> on server and predicted client.
    /// <para>
    /// Asteroids use <see cref="ShipCollisionImpulseLogic"/> with virtual mass (rocks stay static).
    /// Planets / moons keep infinite-mass wall reflect. Ship↔ship is this two-body path.
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
        /// Extra ship↔ship sphere slack so elongated chassis still catch on purpose rams.
        /// Applied only to ship↔ship (not planet/asteroid keep-out).
        /// </summary>
        public const float ShipShipRadiusSlack = 1.1f;

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
            // Unity.Physics Aabb.Extents is Max−Min (full size), not half-extents. Using the
            // full width as a radius doubled MEGA keep-out and blocked small-planet orbit rings.
            Aabb aabb = physicsCollider.Value.Value.CalculateAabb();
            float r = math.max(aabb.Extents.x, aabb.Extents.z) * 0.5f;
            r *= math.max(0.01f, transformScale);
            if (r < BodyCollisionMath.MinShipHullRadiusWorld)
                return fallback;
            return r;
        }

        /// <summary>
        /// True when Euclidean XZ separation differs from the toroidal shortest path — a wrap
        /// tile is involved. Delegates to <see cref="ToroidalMapEcs.CrossedSeam"/>.
        /// </summary>
        /// <param name="shipPos">Ship sim position (wrapped or not).</param>
        /// <param name="bodyPos">Planet / asteroid / moon sim position.</param>
        /// <param name="mapW">Toroidal map width.</param>
        /// <param name="mapH">Toroidal map height.</param>
        public static bool NeedsToroidalResolve(float3 shipPos, float3 bodyPos, float mapW, float mapH)
        {
            return ToroidalMapEcs.CrossedSeam(shipPos, bodyPos, mapW, mapH);
        }

        /// <summary>
        /// Sphere overlap on the torus with no depenetration or bounce. MEGA asteroid plow uses
        /// this so the hull can queue ram damage without being shoved off its flight path.
        /// </summary>
        /// <param name="shipPos">Ship sim position (may be unbounded).</param>
        /// <param name="shipVel">Ship linear velocity (closing speed only).</param>
        /// <param name="shipRadius">Ship hull sphere radius.</param>
        /// <param name="bodyPos">Asteroid / world body logical center.</param>
        /// <param name="bodyRadius">World body sphere radius.</param>
        /// <param name="mapW">Map width.</param>
        /// <param name="mapH">Map height.</param>
        /// <param name="normalShipFromBody">Unit XZ normal from the body toward the ship.</param>
        /// <param name="closingSpeed">Approach speed along that normal (0 if separating).</param>
        /// <returns>True when the spheres overlap on the torus.</returns>
        public static bool TryGetCrossSeamWorldSphereOverlap(
            float3 shipPos,
            float3 shipVel,
            float shipRadius,
            float3 bodyPos,
            float bodyRadius,
            float mapW,
            float mapH,
            out float3 normalShipFromBody,
            out float closingSpeed)
        {
            normalShipFromBody = new float3(0f, 0f, 1f);
            closingSpeed = 0f;

            float3 offset = ToroidalMapEcs.ShortestOffsetXZ(shipPos, bodyPos, mapW, mapH);
            float dist = math.length(offset);
            float minDist = math.max(0.01f, shipRadius + bodyRadius);
            if (dist >= minDist)
                return false;

            normalShipFromBody = ComputeSeparationNormal(offset, dist, shipVel);
            float3 planarVel = new float3(shipVel.x, 0f, shipVel.z);
            closingSpeed = math.max(0f, -math.dot(planarVel, normalShipFromBody));
            return true;
        }

        /// <summary>
        /// If the ship overlaps the world sphere on the torus, push the ship out and bounce velocity.
        /// Same-tile and seams share this path. Pass <paramref name="bodyMass"/> &gt; 0 with
        /// <paramref name="shipMass"/> for asteroids (virtual mass, rock stays put); leave
        /// bodyMass ≤ 0 for infinite-mass planets.
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
        /// <param name="resolveSameTile">
        /// Unused (all tiles resolve). Kept so MEGA vs planet callers still compile.
        /// </param>
        /// <param name="maxKeepOut">
        /// When &gt; 0, cap <c>shipRadius + bodyRadius</c> so the ship center can still reach this
        /// distance (orbit-ring inner). 0 = use the natural sphere sum.
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
            float bodyMass = 0f,
            bool resolveSameTile = false,
            float maxKeepOut = 0f)
        {
            // --- Toroidal separation (ship → body) — same tile or seam ---
            _ = resolveSameTile;
            float3 offset = ToroidalMapEcs.ShortestOffsetXZ(shipPos, bodyPos, mapW, mapH);
            float dist = math.length(offset);
            float minDist = math.max(0.01f, shipRadius + bodyRadius);
            if (maxKeepOut > 0.01f)
                minDist = math.min(minDist, maxKeepOut);
            if (dist >= minDist)
                return false;

            // --- Separation normal: from body toward ship ---
            float3 normal = ComputeSeparationNormal(offset, dist, shipVel);

            // --- Depenetrate along the shortest path (caller wraps after this system) ---
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
        /// Ship↔ship sphere resolve with two-body mass-aware impulse (same-tile and seams).
        /// A always gets the mass-weighted local share so predicted client and server write
        /// the same Δpose / Δvel for that hull. When <paramref name="writePositionB"/> is
        /// false (interpolated remote), B is left alone and A takes the full penetration.
        /// </summary>
        /// <param name="posA">Ship A position — written when depenetrating.</param>
        /// <param name="velA">Ship A linear velocity — written on bounce.</param>
        /// <param name="radiusA">Ship A hull radius (already slack-scaled by the caller).</param>
        /// <param name="massA">Ship A collision mass.</param>
        /// <param name="posB">Ship B position — written when depenetrating and <paramref name="writePositionB"/>.</param>
        /// <param name="velB">Ship B linear velocity — written on bounce (caller may discard).</param>
        /// <param name="radiusB">Ship B hull radius (already slack-scaled by the caller).</param>
        /// <param name="massB">Ship B collision mass.</param>
        /// <param name="mapW">Map width.</param>
        /// <param name="mapH">Map height.</param>
        /// <param name="restitution">Bounce coefficient (typically ship–ship default).</param>
        /// <param name="writePositionB">False when B is interpolated — never write remotes.</param>
        /// <param name="normalAFromB">Unit XZ normal from B toward A.</param>
        /// <param name="closingSpeed">Pre-impulse approach speed along that normal.</param>
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
            float restitution,
            bool writePositionB,
            out float3 normalAFromB,
            out float closingSpeed)
        {
            normalAFromB = new float3(1f, 0f, 0f);
            closingSpeed = 0f;

            // Offset from A toward B along the shortest toroidal path (same-tile or seam).
            float3 offsetAToB = ToroidalMapEcs.ShortestOffsetXZ(posA, posB, mapW, mapH);
            float dist = math.length(offsetAToB);
            float minDist = math.max(0.01f, radiusA + radiusB);
            if (dist >= minDist)
                return false;

            // Normal from B toward A (matches ApplyTwoBodyImpulse / collision-event convention).
            if (dist < 1e-5f)
            {
                float3 planarRel = new float3(velA.x - velB.x, 0f, velA.z - velB.z);
                if (math.lengthsq(planarRel) > 1e-6f)
                    normalAFromB = -math.normalize(planarRel);
            }
            else
            {
                // offsetAToB points A→B, so B→A is the opposite.
                normalAFromB = -offsetAToB / dist;
            }

            float3 vA = velA;
            float3 vB = velB;
            vA.y = 0f;
            vB.y = 0f;
            closingSpeed = math.max(0f, -math.dot(vA - vB, normalAFromB));

            float mA = math.max(ShipCollisionImpulseLogic.MinCollisionMass, massA);
            float mB = math.max(ShipCollisionImpulseLogic.MinCollisionMass, massB);
            float penetration = minDist - dist;
            float invSum = 1f / (mA + mB);

            if (writePositionB)
            {
                posA += normalAFromB * (penetration * mB * invSum);
                posB -= normalAFromB * (penetration * mA * invSum);
                posB.y = 0f;
            }
            else
            {
                // B is a frozen interpolated remote — A must take the full gap or they stay
                // overlapping (half-share + unmoved B is the "ships stack" bug).
                posA += normalAFromB * penetration;
            }
            posA.y = 0f;

            // --- Energy transfer (caller discards velB when B is interpolated) ---
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
