using TitanOrbit.Core;
using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Gem-moon combat helpers shared by bullet damage (server) and predicted shield repel (client+server).
    /// Shield absorption and moon gem drain run from <see cref="BulletSimulationSystem"/>.
    /// Enemy shield push uses the same planet snapshots as <see cref="ShipPhysicsDriveLogic"/> so
    /// prediction stays deterministic — never a client-only GameObject force.
    /// </summary>
    public static class PlanetGemMoonCombatLogic
    {
        /// <summary>True when attacker cannot damage this moon (same team or neutral attacker).</summary>
        public static bool IsTeamFriendlyToMoon(TeamId moonOwner, TeamId team)
        {
            // --- Friendly-fire gate ---
            // [TITAN-ORBIT] Neutral moons (no owner) are always hostile to attackers.
            if (moonOwner == TeamId.None)
                return false;
            if (team == TeamId.None)
                return false;
            return moonOwner == team;
        }

        /// <summary>Sets moon gem reservoir to default max on planet spawn.</summary>
        public static void InitMoonGems(ref PlanetGemMoonState moon)
        {
            // --- Fresh reservoir ---
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

            // --- Shield first ---
            if (moon.CurrentShield > 0f)
            {
                float used = math.min(moon.CurrentShield, remaining);
                moon.CurrentShield -= used;
                remaining -= used;
            }

            // --- Overflow into moon gem stock ---
            if (remaining > 0f)
                moon.CurrentMoonGems = math.max(0f, moon.CurrentMoonGems - remaining);
        }

        /// <summary>
        /// Pushes a ship outward when it penetrates an enemy gem-moon shield shell.
        /// Moons have no Unity Physics colliders for shields — this is pure gameplay math.
        /// Must run on both client prediction and server authority with the same planet snapshots.
        /// </summary>
        /// <param name="shipPos">Ship world position (unbounded; not Wrapped).</param>
        /// <param name="velocity">In/out planar velocity modified by cancel-inward + outward repel.</param>
        /// <param name="shipTeam">Ship team for friendly-moon skip.</param>
        /// <param name="planets">Pre-collected planet+moon snapshots for this drive tick.</param>
        /// <param name="mapW">Toroidal map width.</param>
        /// <param name="mapH">Toroidal map height.</param>
        /// <param name="elapsedSeconds">Sim time for moon orbital phase.</param>
        public static void ApplyShieldRepelIfNeeded(
            float3 shipPos,
            ref float3 velocity,
            TeamId shipTeam,
            in NativeArray<PlanetMotorSnapshot> planets,
            float mapW,
            float mapH,
            double elapsedSeconds)
        {
            shipPos.y = 0f;

            for (int i = 0; i < planets.Length; i++)
            {
                var snapshot = planets[i];
                var planet = snapshot.Planet;
                var moon = snapshot.Moon;

                // --- Skip dead shields and friendly moons ---
                if (moon.CurrentShield <= 0.001f)
                    continue;
                if (IsTeamFriendlyToMoon(planet.Ownership, shipTeam))
                    continue;

                float planetSize = math.max(0.25f, snapshot.Transform.Scale);
                float shieldRadius = snapshot.ShieldOuterRadiusWorld;
                if (shieldRadius <= 0.0001f)
                    continue;

                // --- Moon on the map tile nearest the ship (toroidal unwrap) ---
                // [TITAN-ORBIT] GetMoonWorldPositionNear uses ShortestOffsetXZ — required across seams.
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

                // --- Outward direction from moon toward ship (planar) ---
                float3 dir = shipPos - moonPos;
                dir.y = 0f;
                if (math.lengthsq(dir) < 0.0001f)
                    dir = new float3(0f, 0f, 1f);
                else
                    dir = math.normalize(dir);

                // Deeper penetration → stronger kick (clamped between min/max repel speeds).
                float penetration = math.clamp(1f - dist / math.max(0.0001f, shieldRadius), 0f, 1f);
                float repelSpeed = math.lerp(
                    PlanetGemMoonMath.EnemyShieldRepelMinSpeed,
                    PlanetGemMoonMath.EnemyShieldRepelMaxSpeed,
                    penetration);
                float3 outwardVel = dir * repelSpeed;
                outwardVel.y = 0f;

                // Cancel any velocity component still diving into the shield.
                float inward = math.dot(velocity, -dir);
                if (inward > 0f)
                    velocity += dir * inward;

                velocity += outwardVel;
            }
        }
    }
}
