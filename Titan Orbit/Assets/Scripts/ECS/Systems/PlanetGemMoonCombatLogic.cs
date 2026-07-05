using TitanOrbit.Core;
using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

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

        public static void ApplyShieldRepelIfNeeded(
            EntityManager em,
            ref ShipMotorState motor,
            TeamId shipTeam,
            float mapW,
            float mapH,
            double elapsedSeconds)
        {
            using var planetQuery = em.CreateEntityQuery(
                ComponentType.ReadOnly<PlanetTag>(),
                ComponentType.ReadOnly<PlanetState>(),
                ComponentType.ReadOnly<PlanetGemMoonState>(),
                ComponentType.ReadOnly<LocalTransform>());
            using var planetStates = planetQuery.ToComponentDataArray<PlanetState>(Unity.Collections.Allocator.Temp);
            using var moonStates = planetQuery.ToComponentDataArray<PlanetGemMoonState>(Unity.Collections.Allocator.Temp);
            using var planetTransforms = planetQuery.ToComponentDataArray<LocalTransform>(Unity.Collections.Allocator.Temp);

            float3 shipPos = motor.Position;
            shipPos.y = 0f;

            for (int i = 0; i < planetStates.Length; i++)
            {
                var planet = planetStates[i];
                var moon = moonStates[i];
                if (moon.CurrentShield <= 0.001f)
                    continue;
                if (IsTeamFriendlyToMoon(planet.Ownership, shipTeam))
                    continue;

                float planetSize = math.max(0.25f, planetTransforms[i].Scale);
                float shieldRadius = PlanetGemMoonMath.GetMoonShieldOuterRadiusWorld(planetSize, planet.IsHomePlanet);
                if (shieldRadius <= 0.0001f)
                    continue;

                float3 moonPos = PlanetOrbitMath.GetMoonWorldPositionNear(
                    shipPos,
                    planetTransforms[i].Position,
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

                var dirV = new Vector3(dir.x, dir.y, dir.z);
                float inward = Vector3.Dot(motor.Velocity, -dirV);
                if (inward > 0f)
                    motor.Velocity += dirV * inward;

                motor.Velocity += new Vector3(outwardVel.x, outwardVel.y, outwardVel.z);
            }
        }
    }
}
