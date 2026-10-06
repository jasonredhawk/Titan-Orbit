using TitanOrbit.Data;
using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Near-side collider contact along a cannon laser (muzzle → target).
    /// Ships use baked hull parts; rocks / pads / moons use the same hit spheres as bullets.
    /// Map size comes from <see cref="MapStateSingleton"/> via the caller.
    /// </summary>
    public static class CannonLaserSurface
    {
        /// <summary>
        /// First collider contact from <paramref name="muzzle"/> toward the lock.
        /// Falls back to the body center when no collider is available.
        /// </summary>
        public static bool TryGetHitPoint(
            EntityManager em,
            Entity target,
            float3 muzzle,
            float mapW,
            float mapH,
            double moonElapsed,
            out float3 hit)
        {
            hit = muzzle;
            if (target == Entity.Null || !em.Exists(target)
                || !em.HasComponent<LocalTransform>(target))
                return false;

            if (em.HasComponent<ShipState>(target))
                return TryGetShipHit(em, target, muzzle, mapW, mapH, out hit);

            if (em.HasComponent<AsteroidState>(target))
                return TryGetAsteroidHit(em, target, muzzle, mapW, mapH, out hit);

            if (em.HasComponent<PlanetState>(target))
                return TryGetPlanetHit(em, target, muzzle, mapW, mapH, moonElapsed, out hit);

            var xf = em.GetComponentData<LocalTransform>(target);
            hit = xf.Position;
            hit.y = muzzle.y;
            return true;
        }

        static bool TryGetShipHit(
            EntityManager em,
            Entity ship,
            float3 muzzle,
            float mapW,
            float mapH,
            out float3 hit)
        {
            var xf = em.GetComponentData<LocalTransform>(ship);
            float3 center = MegaShipCombatAim.GetAimPoint(em, ship, xf);
            BuildRayThrough(muzzle, center, mapW, mapH, out float3 beyond, out _);

            if (MegaShipCombatAim.TryHitBulletSegment(
                    em, ship, xf, muzzle, beyond, 0f, mapW, mapH, out hit, out _))
            {
                hit.y = muzzle.y;
                return true;
            }

            float radius = 1.1f;
            if (em.HasComponent<PhysicsCollider>(ship))
            {
                var collider = em.GetComponentData<PhysicsCollider>(ship);
                if (collider.Value.IsCreated)
                    radius = MegaShipCombatAim.GetHitRadiusWorld(em, ship, collider, xf.Scale);
            }

            hit = CannonLaserMath.PullToSphereSurface(muzzle, center, radius, mapW, mapH);
            hit.y = muzzle.y;
            return true;
        }

        static bool TryGetAsteroidHit(
            EntityManager em,
            Entity asteroid,
            float3 muzzle,
            float mapW,
            float mapH,
            out float3 hit)
        {
            var xf = em.GetComponentData<LocalTransform>(asteroid);
            float radius = BulletCollision.AsteroidHitRadius(xf.Scale);
            hit = CannonLaserMath.PullToSphereSurface(muzzle, xf.Position, radius, mapW, mapH);
            hit.y = muzzle.y;
            return true;
        }

        static bool TryGetPlanetHit(
            EntityManager em,
            Entity planet,
            float3 muzzle,
            float mapW,
            float mapH,
            double moonElapsed,
            out float3 hit)
        {
            hit = muzzle;
            var planetState = em.GetComponentData<PlanetState>(planet);
            var planetXf = em.GetComponentData<LocalTransform>(planet);
            float3 center = planetXf.Position;
            float radius = 1.1f;
            bool found = false;
            float best = float.MaxValue;
            var padConfig = PlanetaryDefenseConfig.LoadDefault();

            if (em.HasBuffer<PlanetaryDefenseSlotElement>(planet))
            {
                var slots = em.GetBuffer<PlanetaryDefenseSlotElement>(planet);
                int slotCount = slots.Length;
                for (int s = 0; s < slotCount; s++)
                {
                    var slot = slots[s];
                    if (slot.TurretLevel == 0 || slot.Health <= 0f)
                        continue;

                    float3 pad = PlanetaryDefenseMath.GetSlotWorldPosition(
                        planetXf.Position,
                        math.max(0.25f, planetXf.Scale),
                        planetState.PlanetLevel,
                        s,
                        slotCount);
                    float d = ToroidalMapEcs.ToroidalDistance(muzzle, pad, mapW, mapH);
                    if (d >= best)
                        continue;
                    best = d;
                    center = pad;
                    radius = PlanetaryDefenseHitScan.ComputeTurretHitRadius(padConfig, slot.TurretLevel);
                    found = true;
                }
            }

            if (em.HasComponent<PlanetGemMoonState>(planet))
            {
                var moon = em.GetComponentData<PlanetGemMoonState>(planet);
                float3 moonPos = PlanetOrbitMath.GetMoonWorldPosition(
                    planetXf.Position,
                    math.max(0.25f, planetXf.Scale),
                    planetState.PlanetLevel,
                    planetState.PlanetId,
                    moonElapsed,
                    planetState.IsHomePlanet);
                float hitRadius = PlanetGemMoonMath.GetMoonBulletHitRadiusWorld(
                    math.max(0.25f, planetXf.Scale),
                    planetState.IsHomePlanet,
                    moon.CurrentShield);
                float3 surface = CannonLaserMath.PullToSphereSurface(
                    muzzle, moonPos, hitRadius, mapW, mapH);
                float moonDist = ToroidalMapEcs.ToroidalDistance(muzzle, surface, mapW, mapH);
                if (moonDist < best)
                {
                    best = moonDist;
                    center = moonPos;
                    radius = hitRadius;
                    found = true;
                }
            }

            if (!found)
                return false;

            hit = CannonLaserMath.PullToSphereSurface(muzzle, center, radius, mapW, mapH);
            hit.y = muzzle.y;
            return true;
        }

        /// <summary>Segment from muzzle through the center and a bit past it.</summary>
        static void BuildRayThrough(
            float3 muzzle,
            float3 center,
            float mapW,
            float mapH,
            out float3 beyond,
            out float distance)
        {
            float3 offset = ToroidalMapEcs.ShortestOffsetXZ(muzzle, center, mapW, mapH);
            offset.y = 0f;
            distance = math.length(offset);
            if (distance < 0.05f)
            {
                beyond = center;
                return;
            }

            beyond = center + (offset / distance) * 12f;
            beyond.y = center.y;
        }
    }
}
