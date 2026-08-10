using TitanOrbit.Core;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Shared helper that finds a team's home-planet spawn point on the XZ flight plane.
    /// Used by death respawn, rejoin resume, and Join Team ship spawn — any server path that must
    /// place a hull at home rather than at its last world position.
    /// <para>
    /// [TITAN-ORBIT] Spawn sits on the planet's ship orbit ring centerline at a random angle,
    /// excluding the gem-moon dock zone so the hull does not instantly begin landing and open the
    /// Orbit Menu. Client prediction does not call this — spawn pose is server-authoritative and
    /// replicated via the ship ghost.
    /// </para>
    /// </summary>
    public static class ShipHomeSpawnLogic
    {
        /// <summary>
        /// Legacy fallback offset from planet center when ring math cannot run (missing size).
        /// Kept so callers that still reference the constant compile; prefer orbit-ring spawn.
        /// </summary>
        public const float HomeSpawnOffsetX = 20f;

        /// <summary>
        /// Extra world-space padding beyond <see cref="PlanetGemMoonMath.GetMoonDockZoneRadiusWorld"/>
        /// so a spawn at the exclusion edge cannot drift into the dock sphere on the next tick.
        /// </summary>
        const float MoonDockExclusionMarginWorld = 2.5f;

        /// <summary>
        /// Resolves a random orbit-ring spawn for <paramref name="team"/>: live
        /// <see cref="HomePlanetTag"/> entity first, then baked <see cref="MapLayoutEntryElement"/>
        /// fallback, then origin.
        /// </summary>
        /// <param name="em">Server EntityManager used for planet and layout queries.</param>
        /// <param name="team">Team whose home planet we want.</param>
        /// <param name="elapsedSeconds">
        /// Shared moon orbit clock (<see cref="PlanetGemMoonOrbitClock"/> / ServerTick seconds)
        /// so the exclusion wedge tracks the live moon angle — not <c>World.Time.ElapsedTime</c>.
        /// </param>
        /// <returns>
        /// World position on the home orbit ring centerline, outside the moon dock wedge.
        /// </returns>
        public static float3 FindHomeSpawnPosition(EntityManager em, TeamId team, double elapsedSeconds)
        {
            // --- Prefer live home planet entities ---
            // [ECS/DOTS] HomePlanetTag marks team capitals after map generation.
            // We need position + scale + PlanetId so we can place on the ring and skip the moon.
            float3 homePos = float3.zero;
            float planetSize = 0f;
            int planetId = 0;
            int planetLevel = 1;
            bool found = false;

            using (var homes = em.CreateEntityQuery(
                       ComponentType.ReadOnly<PlanetState>(),
                       ComponentType.ReadOnly<LocalTransform>(),
                       ComponentType.ReadOnly<PlanetTag>(),
                       ComponentType.ReadOnly<HomePlanetTag>()))
            using (var entities = homes.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    var planet = em.GetComponentData<PlanetState>(entities[i]);
                    // Ownership must match — neutrals are never homes.
                    if (planet.Ownership != team)
                        continue;

                    var lt = em.GetComponentData<LocalTransform>(entities[i]);
                    homePos = lt.Position;
                    planetSize = math.max(0.25f, lt.Scale);
                    planetId = planet.PlanetId;
                    planetLevel = math.max(1, planet.PlanetLevel);
                    found = true;
                    break;
                }
            }

            // --- Fallback: baked map layout buffer on MapStateSingleton ---
            // [TITAN-ORBIT] EntityKind 1 = home planet slot written during map generation.
            // Layout has Position / Scale / PlanetId but not live PlanetLevel (ring ignores level).
            if (!found)
            {
                using var mapQuery = em.CreateEntityQuery(ComponentType.ReadOnly<MapStateSingleton>());
                if (mapQuery.CalculateEntityCount() == 1)
                {
                    var mapEntity = mapQuery.GetSingletonEntity();
                    if (em.HasBuffer<MapLayoutEntryElement>(mapEntity))
                    {
                        var layout = em.GetBuffer<MapLayoutEntryElement>(mapEntity);
                        for (int i = 0; i < layout.Length; i++)
                        {
                            var entry = layout[i];
                            if (entry.EntityKind == 1 && entry.Team == team)
                            {
                                homePos = entry.Position;
                                planetSize = math.max(0.25f, entry.Scale);
                                planetId = entry.PlanetId != 0 ? entry.PlanetId : (int)team;
                                planetLevel = 1;
                                found = true;
                                break;
                            }
                        }
                    }
                }
            }

            if (!found)
                return float3.zero;

            // --- Random ring spawn outside the moon dock wedge ---
            return PickOrbitRingSpawnOutsideMoon(
                homePos,
                planetSize,
                planetLevel,
                planetId,
                isHomePlanet: true,
                elapsedSeconds,
                BuildSpawnRandomSeed(team, planetId, elapsedSeconds));
        }

        /// <summary>
        /// Picks a world position on the ship orbit ring centerline at a random angle that stays
        /// outside the gem-moon's dock / landing sphere.
        /// </summary>
        /// <param name="planetPos">Home planet world position (canonical tile).</param>
        /// <param name="planetSize">Planet uniform scale (world radius proxy).</param>
        /// <param name="planetLevel">Planet level (ring radii currently ignore level; API parity).</param>
        /// <param name="planetId">Stable planet id — seeds the same moon phase as dock / visuals.</param>
        /// <param name="isHomePlanet">True for homeworlds (larger moon → larger dock zone).</param>
        /// <param name="elapsedSeconds">Shared ServerTick orbit clock for the live moon angle.</param>
        /// <param name="randomSeed">Per-spawn seed so successive respawns land at different angles.</param>
        /// <returns>Unbounded world spawn on the ring (do not Wrap — ships fly unbounded).</returns>
        public static float3 PickOrbitRingSpawnOutsideMoon(
            float3 planetPos,
            float planetSize,
            int planetLevel,
            int planetId,
            bool isHomePlanet,
            double elapsedSeconds,
            uint randomSeed)
        {
            // --- Orbit ring centerline ---
            // [TITAN-ORBIT] Same radius the passive orbit motor and gem moon use.
            PlanetOrbitMath.GetRingRadiiWorld(
                planetSize, planetLevel, out _, out _, out float centerWorld);
            if (centerWorld < 0.01f)
                return planetPos + new float3(HomeSpawnOffsetX, 0f, 0f);

            // --- Live moon angle on that ring ---
            // [TITAN-ORBIT] θ = phase − ω t — identical formula to ShipMoonDockSystem zone center.
            float3 moonOffset = PlanetOrbitMath.GetShipOrbitRingOffset(
                planetSize, planetLevel, PlanetOrbitMath.GetShipOrbitPhaseOffset(planetId), elapsedSeconds);
            float moonTheta = math.atan2(moonOffset.z, moonOffset.x);

            // --- Exclusion half-width in radians ---
            // Ship and moon share radius R. Chord length between two ring angles is
            // 2 R sin(|Δθ|/2). Require that chord ≥ dock zone + margin so spawn is not "in zone."
            float dockZone = PlanetGemMoonMath.GetMoonDockZoneRadiusWorld(planetSize, isHomePlanet);
            float excludeChord = dockZone + MoonDockExclusionMarginWorld;
            float minDeltaTheta;
            if (excludeChord >= 2f * centerWorld)
            {
                // Pathological: dock sphere larger than the ring diameter — park opposite the moon.
                minDeltaTheta = math.PI;
            }
            else
            {
                minDeltaTheta = 2f * math.asin(math.clamp(excludeChord / (2f * centerWorld), 0f, 1f));
            }

            float excludeFull = minDeltaTheta * 2f;
            float twoPi = math.PI * 2f;

            float theta;
            if (excludeFull >= twoPi - 0.05f)
            {
                // Almost no safe arc left — opposite the moon is the farthest point on the ring.
                theta = moonTheta + math.PI;
            }
            else
            {
                // --- Sample uniformly on the safe arc ---
                // Safe arc starts just past the moon's dock wedge and wraps around to the other side.
                // [STANDARD] CreateFromIndex — deterministic for a given seed (good for repro logs).
                float available = twoPi - excludeFull;
                var rng = Random.CreateFromIndex(randomSeed);
                float u = rng.NextFloat() * available;
                theta = moonTheta + minDeltaTheta + u;
            }

            // --- World position on XZ (Y stays at planet height) ---
            // [TITAN-ORBIT] Leave spawn unbounded — do not ToroidalMapEcs.Wrap the hull.
            return planetPos + new float3(math.cos(theta), 0f, math.sin(theta)) * centerWorld;
        }

        /// <summary>
        /// Builds a per-spawn RNG seed from team, planet id, and orbit clock so consecutive
        /// respawns (and different teams) rarely land on the same angle.
        /// </summary>
        static uint BuildSpawnRandomSeed(TeamId team, int planetId, double elapsedSeconds)
        {
            // Mix team + planet + millisecond orbit time. Cast truncates; XOR spreads bits.
            uint t = (uint)team;
            uint p = (uint)planetId;
            uint ms = (uint)math.max(0d, elapsedSeconds * 1000.0);
            return (t * 73856093u) ^ (p * 19349663u) ^ (ms * 83492791u) ^ 0xA24BAED5u;
        }
    }
}
