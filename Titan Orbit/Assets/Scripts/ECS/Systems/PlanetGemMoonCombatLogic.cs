using TitanOrbit.Core;
using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>Server-side gem-moon combat helpers ported from legacy PlanetGemMoon.</summary>
    public static class PlanetGemMoonCombatLogic
    {
        public static bool IsTeamFriendlyToMoon(TeamId moonOwner, TeamId team)
        {
            if (moonOwner == TeamId.None)
                return false;
            if (team == TeamId.None)
                return false;
            return moonOwner == team;
        }

        public static void InitMoonGems(ref PlanetGemMoonState moon)
        {
            moon.MaxMoonGems = PlanetGemMoonMath.BaseMaxMoonGemPoints;
            moon.CurrentMoonGems = moon.MaxMoonGems;
            moon.GemDrainAccumulator = 0f;
            moon.GemSpawnTimer = 0f;
        }

        /// <summary>Shield absorbs damage first; remainder reduces the moon gem reservoir.</summary>
        public static void ApplyBulletDamage(
            ref PlanetGemMoonState moon,
            float damage,
            TeamId attackerTeam,
            TeamId moonOwner,
            double now)
        {
            if (damage <= 0f)
                return;
            if (IsTeamFriendlyToMoon(moonOwner, attackerTeam))
                return;

            moon.LastShieldHitServerTime = (float)now;
            float remaining = damage;

            if (moon.CurrentShield > 0f)
            {
                float used = math.min(moon.CurrentShield, remaining);
                moon.CurrentShield -= used;
                remaining -= used;
            }

            if (remaining > 0f)
                moon.CurrentMoonGems = math.max(0f, moon.CurrentMoonGems - remaining);
        }

        /// <summary>
        /// Enemy moon shield repel — used by <see cref="ShipMovementBurstLogic"/>.
        /// Scans a pre-collected planet snapshot (no EntityManager).
        /// </summary>
        public static void ApplyShieldRepelIfNeeded(
            ref ShipMotorState motor,
            TeamId shipTeam,
            in NativeArray<PlanetMotorSnapshot> planets,
            float mapW,
            float mapH,
            double elapsedSeconds)
        {
            float3 shipPos = motor.Position;
            shipPos.y = 0f;

            for (int i = 0; i < planets.Length; i++)
            {
                var snapshot = planets[i];
                var planet = snapshot.Planet;
                var moon = snapshot.Moon;
                if (moon.CurrentShield <= 0.001f)
                    continue;
                if (IsTeamFriendlyToMoon(planet.Ownership, shipTeam))
                    continue;

                float planetSize = math.max(0.25f, snapshot.Transform.Scale);
                float shieldRadius = snapshot.ShieldOuterRadiusWorld;
                if (shieldRadius <= 0.0001f)
                    continue;

                float3 moonPos = PlanetOrbitMath.GetMoonWorldPositionNear(
                    shipPos,
                    snapshot.Transform.Position,
                    planetSize,
                    planet.PlanetLevel,
                    planet.PlanetId,
                    elapsedSeconds,
                    mapW,
                    mapH);
                moonPos.y = 0f;

                float dist = ToroidalMapEcs.ToroidalDistance(shipPos, moonPos, mapW, mapH);
                if (dist > shieldRadius)
                    continue;

                float3 dir = shipPos - moonPos;
                dir.y = 0f;
                if (math.lengthsq(dir) < 0.0001f)
                    dir = new float3(0f, 0f, 1f);
                else
                    dir = math.normalize(dir);

                float penetration = math.clamp(1f - dist / math.max(0.0001f, shieldRadius), 0f, 1f);
                float repelSpeed = math.lerp(
                    PlanetGemMoonMath.EnemyShieldRepelMinSpeed,
                    PlanetGemMoonMath.EnemyShieldRepelMaxSpeed,
                    penetration);
                float3 outwardVel = dir * repelSpeed;
                outwardVel.y = 0f;

                float inward = math.dot(motor.Velocity, -dir);
                if (inward > 0f)
                    motor.Velocity += dir * inward;

                motor.Velocity += outwardVel;
            }
        }
    }
}
