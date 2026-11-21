using TitanOrbit.Core;
using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.Mathematics;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Gem-moon combat helpers for bullet damage (server) and friendly-fire gates.
    /// Shield absorption runs first. Damage that reaches the moon body removes the planet's
    /// stored gems and spawns them as pickups. The slow planet-bank siphon while the shield
    /// stays down is <see cref="PlanetGemMoonCombatDrainSystem"/>.
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
            // Same team cannot aim this moon. Unowned moons are hostile.
            if (IsTeamFriendlyToMoon(moonOwner, attackerTeam))
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
            moon.GemExpelCarry = 0f;
        }

        /// <summary>
        /// Shield absorbs first. Damage that gets through expels the planet's stored
        /// gems (<paramref name="planetGems"/>, <see cref="PlanetState.CurrentGems"/>),
        /// one pickup sized to that damage. A slice under
        /// <see cref="GemEconomyConstants.MinGemSpawnValue"/> stays on
        /// <see cref="PlanetGemMoonState.GemExpelCarry"/> until the next hit can spawn it.
        /// </summary>
        public static void ApplyBulletDamage(
            ref PlanetGemMoonState moon,
            ref float planetGems,
            float damage,
            TeamId attackerTeam,
            TeamId moonOwner,
            double now,
            EntityCommandBuffer ecb,
            Entity gemPrefab,
            float3 gemSpawnPosition,
            uint gemSalt,
            float gemSpawnServerTime)
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

            if (remaining <= 0f)
                return;

            planetGems = math.max(0f, planetGems);
            float take = math.min(planetGems, remaining);
            planetGems -= take;
            moon.GemExpelCarry += take;
            if (moon.GemExpelCarry < GemEconomyConstants.MinGemSpawnValue)
                return;

            float spawn = moon.GemExpelCarry;
            moon.GemExpelCarry = 0f;
            gemSpawnPosition.y = 0f;
            SpawnOneGem(ecb, gemPrefab, gemSpawnPosition, spawn, gemSalt, gemSpawnServerTime);
        }

        /// <summary>One bursting pickup. Value is the gem's size. No per-point split.</summary>
        static void SpawnOneGem(
            EntityCommandBuffer ecb,
            Entity gemPrefab,
            float3 position,
            float value,
            uint salt,
            float spawnServerTime)
        {
            if (value <= 0.0001f)
                return;
            ShipGemExpulsion.SpawnFromDamage(
                ecb,
                gemPrefab,
                position,
                value,
                intensity: 0.5f,
                salt,
                spawnServerTime,
                sourceShipNetworkId: 0);
        }
    }
}
