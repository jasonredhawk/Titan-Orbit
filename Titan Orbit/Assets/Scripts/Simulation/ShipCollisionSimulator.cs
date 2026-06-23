using UnityEngine;
using TitanOrbit.Entities;
using TitanOrbit.Generation;
using TitanOrbit.Systems;

namespace TitanOrbit.Simulation
{
    public struct ShipCollisionResult
    {
        public bool HadCollision;
        public bool WasAsteroid;
        public Asteroid Asteroid;
        public Starship OtherShip;
        public Vector3 ImpactPoint;
        public Vector3 SurfaceNormal;
        public float ImpactForceNewtons;
        public float RelativeSpeed;
        public Vector3 OutboundVelocity;
    }

    /// <summary>
    /// Deterministic toroidal collision resolution for ships vs asteroids and other ships.
    /// </summary>
    public static class ShipCollisionSimulator
    {
        public static bool TryResolveAsteroidCollision(
            ref ShipMotorState state,
            float shipRadius,
            float restitution,
            float impactMass,
            float fixedDeltaTime,
            Vector3 preStepVelocity,
            out ShipCollisionResult result)
        {
            result = default;
            Vector3 pos = state.Position;
            pos.y = 0f;

            Asteroid bestAsteroid = null;
            float bestPenetration = 0f;
            Vector3 bestNormal = Vector3.zero;
            Vector3 bestCenter = Vector3.zero;

            for (int i = 0; i < Asteroid.AllAsteroids.Count; i++)
            {
                Asteroid asteroid = Asteroid.AllAsteroids[i];
                if (asteroid == null || asteroid.IsDestroyed) continue;

                Vector3 center = asteroid.transform.position;
                center.y = 0f;
                float asteroidR = asteroid.GetCollisionRadiusWorld();
                float combined = shipRadius + asteroidR;

                float dist = ToroidalMap.ToroidalDistance(pos, center);
                if (dist >= combined - 0.0001f) continue;

                Vector3 toShip = ToroidalMap.ShortestWorldOffsetXZ(center, pos);
                if (toShip.sqrMagnitude < 1e-10f)
                    toShip = state.Rotation * Vector3.forward;

                Vector3 n = toShip.normalized;
                float penetration = combined - Mathf.Max(dist, 0.0001f);
                if (penetration > bestPenetration)
                {
                    bestPenetration = penetration;
                    bestAsteroid = asteroid;
                    bestNormal = n;
                    bestCenter = center;
                }
            }

            if (bestAsteroid == null)
                return false;

            // Push out of penetration along surface normal.
            state.Position += bestNormal * bestPenetration;

            Vector3 vInc = preStepVelocity;
            vInc.y = 0f;
            float vn = Vector3.Dot(vInc, bestNormal);
            if (vn >= 0f)
            {
                vInc = state.Velocity;
                vInc.y = 0f;
                vn = Vector3.Dot(vInc, bestNormal);
            }

            float relativeSpeed = vInc.magnitude;
            if (vn >= 0f)
            {
                if (relativeSpeed < 2.5f)
                    return false;
                vn = -Mathf.Max(1f, relativeSpeed * 0.22f);
                vInc = bestNormal * vn;
            }

            float e = restitution;
            Vector3 vOut = vInc - (1f + e) * vn * bestNormal;
            vOut.y = 0f;

            float deltaNormalSpeed = (1f + e) * Mathf.Abs(vn);
            float impactImpulse = impactMass * deltaNormalSpeed;
            float impactForceNewtons = impactImpulse / Mathf.Max(0.0001f, fixedDeltaTime);

            state.Velocity = vOut;

            Vector3 impactPoint = bestCenter + bestNormal * bestAsteroid.GetCollisionRadiusWorld();
            impactPoint.y = 0f;

            result = new ShipCollisionResult
            {
                HadCollision = true,
                WasAsteroid = true,
                Asteroid = bestAsteroid,
                ImpactPoint = impactPoint,
                SurfaceNormal = bestNormal,
                ImpactForceNewtons = impactForceNewtons,
                RelativeSpeed = relativeSpeed,
                OutboundVelocity = vOut,
            };
            return true;
        }

        public static bool TryResolveShipShipCollision(
            ref ShipMotorState state,
            Starship self,
            float shipRadius,
            float restitution,
            float fixedDeltaTime,
            out ShipCollisionResult result)
        {
            result = default;
            if (self == null) return false;

            Vector3 myPos = state.Position;
            myPos.y = 0f;
            float mMe = Mathf.Max(0.5f, state.Mass);

            Starship bestOther = null;
            float bestPenetration = 0f;
            Vector3 bestSeparationNormal = Vector3.zero;
            Vector3 bestOtherPos = Vector3.zero;
            float bestOtherMass = 1f;
            Vector3 bestOtherVel = Vector3.zero;

            for (int i = 0; i < Starship.AllStarships.Count; i++)
            {
                Starship other = Starship.AllStarships[i];
                if (other == null || other == self) continue;
                if (other.IsDead || other.GemMoonDocked) continue;

                Vector3 otherPos = other.GetSimPosition();
                otherPos.y = 0f;
                float otherR = other.GetShipCollisionRadiusXZ();
                float combined = shipRadius + otherR;

                float dist = ToroidalMap.ToroidalDistance(myPos, otherPos);
                if (dist >= combined - 0.0001f) continue;

                Vector3 toOther = ToroidalMap.ShortestWorldOffsetXZ(myPos, otherPos);
                if (toOther.sqrMagnitude < 1e-10f)
                {
                    toOther = state.Rotation * Vector3.forward;
                    toOther.y = 0f;
                }
                Vector3 n = toOther.normalized;
                Vector3 separationNormal = -n;

                float penetration = combined - Mathf.Max(dist, 0.0001f);
                if (penetration > bestPenetration)
                {
                    bestPenetration = penetration;
                    bestOther = other;
                    bestSeparationNormal = separationNormal;
                    bestOtherPos = otherPos;
                    bestOtherMass = other.GetSimMass();
                    bestOtherVel = other.GetSimVelocity();
                }
            }

            if (bestOther == null)
                return false;

            float totalMass = mMe + bestOtherMass;
            float mySepShare = totalMass > 0.001f ? bestOtherMass / totalMass : 0.5f;
            state.Position += bestSeparationNormal * (bestPenetration * mySepShare);

            Vector3 nSep = -bestSeparationNormal;
            nSep.y = 0f;
            if (nSep.sqrMagnitude < 1e-8f) return false;
            nSep.Normalize();

            Vector3 vMe = state.Velocity;
            vMe.y = 0f;
            Vector3 vOther = bestOtherVel;
            vOther.y = 0f;
            float vRelN = Vector3.Dot(vMe - vOther, nSep);
            const float minClosingSpeed = 0.35f;
            if (vRelN >= -minClosingSpeed)
            {
                result = new ShipCollisionResult
                {
                    HadCollision = true,
                    WasAsteroid = false,
                    OtherShip = bestOther,
                    ImpactPoint = (myPos + bestOtherPos) * 0.5f,
                    SurfaceNormal = nSep,
                    RelativeSpeed = (vMe - vOther).magnitude,
                };
                return true;
            }

            float invMassSum = 1f / mMe + 1f / Mathf.Max(0.5f, bestOtherMass);
            float j = -(1f + restitution) * vRelN / invMassSum;
            Vector3 newVel = vMe + nSep * (j / mMe);
            newVel.y = 0f;
            state.Velocity = newVel;

            result = new ShipCollisionResult
            {
                HadCollision = true,
                WasAsteroid = false,
                OtherShip = bestOther,
                ImpactPoint = (myPos + bestOtherPos) * 0.5f,
                SurfaceNormal = nSep,
                RelativeSpeed = (vMe - vOther).magnitude,
                OutboundVelocity = newVel,
            };
            return true;
        }
    }
}
