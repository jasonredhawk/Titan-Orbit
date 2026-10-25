using TitanOrbit.Core;
using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.Mathematics;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Gem-moon combat helpers for bullet damage (server) and friendly-fire gates.
    /// Shield absorption runs first. Damage that reaches the moon body removes moon gems
    /// and spawns them as bursting pickups. The slow planet-bank siphon while the shield
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
            moon.GemExpelCarry = 0f;
        }

        /// <summary>
        /// Shield absorbs first. Damage that reaches the gem reservoir becomes one pickup
        /// whose value is that damage — a 16-point hit spawns one gem of 16, not sixteen
        /// gems of 1. Bullets and mines pass <paramref name="gemBatchDamage"/> 0 so the
        /// whole hit is one gem. The cannon laser passes one authored shot of damage and
        /// banks its per-tick slices on <see cref="PlanetGemMoonState.GemExpelCarry"/> until
        /// that shot is full, then pops one gem of that size.
        /// </summary>
        public static void ApplyBulletDamage(
            ref PlanetGemMoonState moon,
            float damage,
            TeamId attackerTeam,
            TeamId moonOwner,
            double now,
            EntityCommandBuffer ecb,
            Entity gemPrefab,
            float3 gemSpawnPosition,
            uint gemSalt,
            float gemSpawnServerTime,
            float gemBatchDamage = 0f)
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

            if (remaining <= 0f || moon.CurrentMoonGems <= 0f)
                return;

            gemSpawnPosition.y = 0f;

            // --- One discrete hit: bullet or mine ---
            // The gem value is the damage that landed on the reservoir, capped by what's left.
            if (gemBatchDamage <= 0.001f)
            {
                float expelled = math.min(moon.CurrentMoonGems, remaining);
                moon.CurrentMoonGems -= expelled;
                // A bullet can land while the laser still has a partial shot banked.
                moon.GemExpelCarry = math.min(moon.GemExpelCarry, moon.CurrentMoonGems);
                SpawnOneGem(ecb, gemPrefab, gemSpawnPosition, expelled, gemSalt, gemSpawnServerTime);
                return;
            }

            // --- Laser: bank slices, emit one gem per authored shot ---
            float available = math.max(0f, moon.CurrentMoonGems - moon.GemExpelCarry);
            moon.GemExpelCarry += math.min(available, remaining);

            float packet = gemBatchDamage;
            while (moon.GemExpelCarry >= packet - 0.0001f && moon.CurrentMoonGems > 0.0001f)
            {
                float chunk = math.min(packet, moon.CurrentMoonGems);
                moon.CurrentMoonGems -= chunk;
                moon.GemExpelCarry = math.max(0f, moon.GemExpelCarry - chunk);
                SpawnOneGem(ecb, gemPrefab, gemSpawnPosition, chunk, gemSalt, gemSpawnServerTime);
                if (chunk + 0.0001f < packet)
                    break;
            }

            // Tail: not enough gems left to fill another shot-sized gem.
            if (moon.CurrentMoonGems <= moon.GemExpelCarry + 0.0001f && moon.GemExpelCarry > 0.0001f)
            {
                float chunk = math.min(moon.GemExpelCarry, moon.CurrentMoonGems);
                moon.CurrentMoonGems -= chunk;
                moon.GemExpelCarry = 0f;
                SpawnOneGem(ecb, gemPrefab, gemSpawnPosition, chunk, gemSalt, gemSpawnServerTime);
            }
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
