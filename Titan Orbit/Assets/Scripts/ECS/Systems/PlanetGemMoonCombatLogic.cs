using TitanOrbit.Core;
using TitanOrbit.Simulation;
using Unity.Mathematics;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Gem-moon combat helpers for bullet damage (server) and friendly-fire gates.
    /// Shield absorption and moon gem drain run from <see cref="BulletSimulationSystem"/>.
    /// Enemy/neutral shield push is a kinematic PhysX sphere plus
    /// <see cref="ShipCollisionBounceSystem"/> wall bounce — not a drive-tick velocity kick.
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

        /// <summary>
        /// Near-side aim on an enemy gem moon. Shield shell while the barrier
        /// is up, moon body once it is down. Friendly and unowned moons return false.
        /// <paramref name="mapW"/> / <paramref name="mapH"/> come from <see cref="MapStateSingleton"/>.
        /// </summary>
        public static bool TryGetNonFriendlyMoonAim(
            TeamId moonOwner,
            TeamId attackerTeam,
            float3 planetPosition,
            float planetScale,
            int planetLevel,
            int planetId,
            bool isHomePlanet,
            float currentShield,
            float3 from,
            float mapW,
            float mapH,
            double moonElapsed,
            out float3 aim,
            out float3 orbitalVelocity)
        {
            aim = default;
            orbitalVelocity = float3.zero;
            // Unowned moons stay out of auto-aim. Bullets that physically hit them
            // still use <see cref="IsTeamFriendlyToMoon"/>.
            if (moonOwner == TeamId.None || IsTeamFriendlyToMoon(moonOwner, attackerTeam))
                return false;

            float scale = math.max(0.25f, planetScale);
            float3 moonPos = PlanetOrbitMath.GetMoonWorldPosition(
                planetPosition,
                scale,
                planetLevel,
                planetId,
                moonElapsed,
                isHomePlanet);
            float hitRadius = PlanetGemMoonMath.GetMoonBulletHitRadiusWorld(
                scale,
                isHomePlanet,
                currentShield);
            aim = CannonLaserMath.PullToSphereSurface(from, moonPos, hitRadius, mapW, mapH);
            orbitalVelocity = PlanetOrbitMath.GetMoonOrbitalVelocity(
                scale,
                planetLevel,
                planetId,
                moonElapsed);
            return true;
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
    }
}
