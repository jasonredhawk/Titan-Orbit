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
    /// Enemy/neutral shield push uses the same planet snapshots as <see cref="ShipPhysicsDriveLogic"/> so
    /// prediction stays deterministic — never a client-only GameObject force.
    /// Passiveive orbit-ring coast uses a soft slide so ring motion stays continuous; thrust/fire keep
    /// the hard combat kick.
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
        /// <param name="softenForPassiveOrbit">
        /// True when the shared motor is in passive orbit-ring coast (<c>useOrbit</c>).
        /// Soft slide keeps ring motion continuous; hard kick stays for thrust/fire into the shell.
        /// </param>
        public static void ApplyShieldRepelIfNeeded(
            float3 shipPos,
            ref float3 velocity,
            TeamId shipTeam,
            in NativeArray<PlanetMotorSnapshot> planets,
            float mapW,
            float mapH,
            double elapsedSeconds,
            bool softenForPassiveOrbit = false)
        {
            for (int i = 0; i < planets.Length; i++)
            {
                var snapshot = planets[i];
                var planet = snapshot.Planet;
                var moon = snapshot.Moon;

                // --- Skip dead shields and friendly moons ---
                // [TITAN-ORBIT] Neutral ownership is always hostile — same gate as bullet damage.
                if (moon.CurrentShield <= 0.001f)
                    continue;
                if (IsTeamFriendlyToMoon(planet.Ownership, shipTeam))
                    continue;

                float planetSize = math.max(0.25f, snapshot.Transform.Scale);
                float shieldRadius = snapshot.ShieldOuterRadiusWorld;
                if (shieldRadius <= 0.0001f)
                    continue;

                // Moon world pose on the shell. Do not flatten Y — that collapsed every
                // high-latitude ship onto the same XZ disk as the moons and zeroed northbound speed.
                float3 moonPos = PlanetOrbitMath.GetMoonWorldPositionNear(
                    shipPos,
                    snapshot.Transform.Position,
                    planetSize,
                    planet.PlanetLevel,
                    planet.PlanetId,
                    elapsedSeconds,
                    mapW,
                    mapH);

                float dist = ToroidalMapEcs.ToroidalDistance(shipPos, moonPos, mapW, mapH);
                if (dist > shieldRadius)
                    continue;

                // --- Outward geodesic direction from moon toward ship (tangent at the hull) ---
                float3 dir = ToroidalMapEcs.ToroidalDirection(moonPos, shipPos, mapW, mapH);
                if (math.lengthsq(dir) < 0.0001f)
                    dir = SphericalMapEcs.OrthonormalTangent(SphericalMapEcs.LocalUp(shipPos));
                else
                    dir = math.normalize(dir);

                // Deeper penetration → stronger response (soft or hard clamp range).
                float penetration = math.clamp(1f - dist / math.max(0.0001f, shieldRadius), 0f, 1f);

                // Cancel any velocity component still diving into the shield.
                float inward = math.dot(velocity, -dir);
                if (inward > 0f)
                    velocity += dir * inward;

                if (softenForPassiveOrbit)
                {
                    // --- Soft orbit-coast slide (neutral / enemy rings) ---
                    // [TITAN-ORBIT] Hard path used velocity += 8..22 every tick. Orbit target speed is
                    // ~0.8, so one graze left the hull on a multi-tick ramp that fought the orbit
                    // lerp and looked like stepped motion around the ring. Friendly moons never hit
                    // this path — that is why home/team rings felt smooth.
                    // Soft path raises the outward component up to a near-orbit cap (set-up-to, not
                    // add) so the shield still nudges you off the moon without breaking coast.
                    float softOut = math.lerp(
                        PlanetGemMoonMath.SoftOrbitShieldOutMinSpeed,
                        PlanetGemMoonMath.SoftOrbitShieldOutMaxSpeed,
                        penetration);
                    float outwardAlong = math.dot(velocity, dir);
                    if (outwardAlong < softOut)
                        velocity += dir * (softOut - outwardAlong);
                }
                else
                {
                    // --- Hard combat kick (thrust / fire / not in passive orbit motor) ---
                    // [TITAN-ORBIT] Additive kick — intentional boot when diving into enemy shields.
                    float repelSpeed = math.lerp(
                        PlanetGemMoonMath.EnemyShieldRepelMinSpeed,
                        PlanetGemMoonMath.EnemyShieldRepelMaxSpeed,
                        penetration);
                    velocity += dir * repelSpeed;
                }

                velocity = SphericalMapEcs.FlattenToTangent(velocity, shipPos);
            }
        }
    }
}
