using TitanOrbit.Core;
using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Shared helper that finds a spawn point on the XZ flight plane around a planet.
    /// Used by death respawn (chosen friendly planet), rejoin resume, Join Team server spawn,
    /// and client predicted TeamChoice Instantiates when the server pose was not forwarded.
    /// <para>
    /// [TITAN-ORBIT] Spawn sits <b>inside</b> the decorative planet rings — not on the ship
    /// orbit ring. The orbit ring is the thin annulus that captures a coasting hull into the
    /// passive orbit motor and is also where the gem moon lives. Spawning on that centerline
    /// made a new ship start in-orbit and often drift into the moon dock zone (Orbit Menu).
    /// Interior samples stay well inside the moon's inward dock reach (not just
    /// <c>orbit inner − a sliver</c>) so a later moon pass cannot sweep the hull into
    /// the Orbit Menu. We also reject any point whose toroidal distance to the live
    /// moon is inside the dock sphere + padding.
    /// </para>
    /// Server home lookup prefers <see cref="HomePlanetTag"/>; ClientWorld must use replicated
    /// <see cref="PlanetState.IsHomePlanet"/> (the tag is not ghosted).
    /// </summary>
    public static class ShipHomeSpawnLogic
    {
        /// <summary>
        /// Legacy fallback offset from planet center when ring math cannot run (missing size).
        /// Kept so callers that still reference the constant compile; prefer interior-ring spawn.
        /// </summary>
        public const float HomeSpawnOffsetX = 20f;

        /// <summary>
        /// Extra world-space padding beyond <see cref="PlanetGemMoonMath.GetMoonDockZoneRadiusWorld"/>
        /// so a spawn at the exclusion edge cannot drift into the dock sphere on the next tick,
        /// and so a later moon pass still misses the hull.
        /// </summary>
        const float MoonDockExclusionMarginWorld = 4f;

        /// <summary>
        /// World-space gap inside the orbit-ring inner radius. Keeps the hull out of
        /// <see cref="PlanetOrbitMath.IsInOrbitRing"/> so the passive motor cannot grab spawn.
        /// </summary>
        const float OrbitRingInnerClearanceWorld = 0.75f;

        /// <summary>How many random interior samples we try before parking opposite the moon.</summary>
        const int MaxInteriorSampleAttempts = 24;

        /// <summary>
        /// Same ship-radius estimate moon dock uses. Added to the planet body so spawn is not
        /// inside the hull+planet collision keep-out.
        /// </summary>
        const float ShipRadiusEstimate = 0.8f;

        /// <summary>
        /// Local radius of a typical Unity sphere mesh (0.5) — planet <c>LocalTransform.Scale</c>
        /// times this is the body radius in world units.
        /// </summary>
        const float PlanetBodyRadiusLocal = 0.5f;

        /// <summary>
        /// Resolves a random interior-ring spawn for <paramref name="team"/>: live home planet first,
        /// then baked <see cref="MapLayoutEntryElement"/> fallback, then origin.
        /// <para>
        /// [TITAN-ORBIT] Server uses <see cref="HomePlanetTag"/>. Client ghosts do <b>not</b> have
        /// that tag — they must use replicated <see cref="PlanetState.IsHomePlanet"/> (same trap as
        /// orbit Bank / <c>EcsGameBridge.TryGetHomePlanetIdForTeam</c>). Without this, predicted
        /// TeamChoice Instantiates landed at (0,0,0) and stole the local hull.
        /// </para>
        /// </summary>
        /// <param name="em">Server or Client EntityManager for planet and layout queries.</param>
        /// <param name="team">Team whose home planet we want.</param>
        /// <param name="elapsedSeconds">
        /// Shared moon orbit clock (<see cref="PlanetGemMoonOrbitClock"/> / ServerTick seconds)
        /// so the moon keep-out tracks the live moon — not <c>World.Time.ElapsedTime</c>.
        /// </param>
        /// <returns>
        /// World position inside the home planet rings, outside the moon dock disc.
        /// Returns <c>float3.zero</c> only when no home can be resolved yet (caller should retry).
        /// </returns>
        public static float3 FindHomeSpawnPosition(EntityManager em, TeamId team, double elapsedSeconds)
        {
            TryFindHomeSpawnPosition(em, team, elapsedSeconds, out float3 spawnPos);
            return spawnPos;
        }

        /// <summary>
        /// Same as <see cref="FindHomeSpawnPosition"/> but returns false when no home can be
        /// resolved yet — callers must retry instead of treating origin as a spawn.
        /// </summary>
        public static bool TryFindHomeSpawnPosition(
            EntityManager em,
            TeamId team,
            double elapsedSeconds,
            out float3 spawnPos)
        {
            spawnPos = float3.zero;

            // --- Resolve home planet pose ---
            // We need position + scale + PlanetId so we can place in the rings and skip the moon.
            float3 homePos = float3.zero;
            float planetSize = 0f;
            int planetId = 0;
            int planetLevel = 1;
            bool found = TryFindLiveHomePlanet(
                em, team, out homePos, out planetSize, out planetId, out planetLevel);

            // --- Fallback: baked map layout buffer on MapStateSingleton ---
            // [TITAN-ORBIT] EntityKind 1 = home planet slot written during map generation.
            // Layout has Position / Scale / PlanetId but not live PlanetLevel (ring ignores level).
            // Often present on server only — clients usually rely on IsHomePlanet above.
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
                return false;

            spawnPos = PickInteriorRingSpawnOutsideMoon(
                homePos,
                planetSize,
                planetLevel,
                planetId,
                isHomePlanet: true,
                elapsedSeconds,
                BuildSpawnRandomSeed(team, planetId, elapsedSeconds));
            return true;
        }

        /// <summary>
        /// Resolves an interior-ring spawn around a specific live planet (any friendly world).
        /// Used by death respawn after the player picks a planet on the expanded minimap.
        /// </summary>
        /// <param name="em">Server EntityManager (authoritative planet ghosts).</param>
        /// <param name="planetId">Stable <see cref="PlanetState.PlanetId"/> the player chose.</param>
        /// <param name="elapsedSeconds">Shared ServerTick moon orbit clock.</param>
        /// <param name="spawnPos">Wrapped world pose on success; zero on failure.</param>
        /// <returns>False when the planet is missing or has no usable scale yet.</returns>
        public static bool TryFindPlanetSpawnPosition(
            EntityManager em,
            int planetId,
            double elapsedSeconds,
            out float3 spawnPos)
        {
            spawnPos = float3.zero;
            if (planetId == 0)
                return false;

            if (!TryFindLivePlanetById(
                    em,
                    planetId,
                    out float3 planetPos,
                    out float planetSize,
                    out int planetLevel,
                    out bool isHomePlanet,
                    out TeamId ownership))
                return false;

            spawnPos = PickInteriorRingSpawnOutsideMoon(
                planetPos,
                planetSize,
                planetLevel,
                planetId,
                isHomePlanet,
                elapsedSeconds,
                BuildSpawnRandomSeed(ownership, planetId, elapsedSeconds));
            return true;
        }

        /// <summary>
        /// True when <paramref name="team"/> currently owns at least one planet.
        /// Server uses this to decide elimination: a dead ship with no friendly worlds cannot respawn.
        /// </summary>
        /// <param name="em">Server EntityManager.</param>
        /// <param name="team">Ship team. <see cref="TeamId.None"/> never owns planets.</param>
        public static bool TeamOwnsAnyPlanet(EntityManager em, TeamId team)
        {
            if (team == TeamId.None)
                return false;

            using var planets = em.CreateEntityQuery(
                ComponentType.ReadOnly<PlanetState>(),
                ComponentType.ReadOnly<PlanetTag>());
            using var states = planets.ToComponentDataArray<PlanetState>(Allocator.Temp);
            for (int i = 0; i < states.Length; i++)
            {
                if (states[i].Ownership == team)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// True when <paramref name="planetId"/> is a live planet owned by <paramref name="team"/>.
        /// Respawn RPCs call this so a captured world cannot be used as a spawn.
        /// </summary>
        public static bool TryGetFriendlyPlanetPose(
            EntityManager em,
            int planetId,
            TeamId team,
            out float3 planetPos,
            out float planetSize,
            out int planetLevel,
            out bool isHomePlanet)
        {
            planetPos = float3.zero;
            planetSize = 0f;
            planetLevel = 1;
            isHomePlanet = false;
            if (planetId == 0 || team == TeamId.None)
                return false;

            if (!TryFindLivePlanetById(
                    em, planetId, out planetPos, out planetSize, out planetLevel, out isHomePlanet, out TeamId ownership))
                return false;

            return ownership == team;
        }

        /// <summary>
        /// Finds the team's live home planet pose. Prefers server-only <see cref="HomePlanetTag"/>,
        /// then replicated <see cref="PlanetState.IsHomePlanet"/> for ClientWorld ghosts.
        /// </summary>
        static bool TryFindLiveHomePlanet(
            EntityManager em,
            TeamId team,
            out float3 homePos,
            out float planetSize,
            out int planetId,
            out int planetLevel)
        {
            homePos = float3.zero;
            planetSize = 0f;
            planetId = 0;
            planetLevel = 1;

            // --- Server path: HomePlanetTag is a tiny set (one capital per team) ---
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
                    return true;
                }
            }

            // --- Client path: HomePlanetTag is not replicated ---
            // [NETCODE] Ghosted planets carry IsHomePlanet + Ownership + LocalTransform.
            // [TITAN-ORBIT] Predicted TeamChoice Instantiates call this on ClientWorld — without
            // this branch, spawn was float3.zero and the player appeared off-map / frozen.
            // [TITAN-ORBIT] Skip planet gathers during Join Team Instantiates (Crash!!! window).
            // ServerWorld must never honor the client settle statics (Local Host shares them).
            var world = em.World;
            bool isClient = world != null && world.IsClient();
            if (isClient && ClientJoinSettleCache.ShouldSkipMapBodyQueries)
                return false;

            using (var planets = em.CreateEntityQuery(
                       ComponentType.ReadOnly<PlanetState>(),
                       ComponentType.ReadOnly<LocalTransform>(),
                       ComponentType.ReadOnly<PlanetTag>()))
            using (var entities = planets.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    var planet = em.GetComponentData<PlanetState>(entities[i]);
                    if (!planet.IsHomePlanet || planet.Ownership != team)
                        continue;

                    var lt = em.GetComponentData<LocalTransform>(entities[i]);
                    homePos = lt.Position;
                    planetSize = math.max(0.25f, lt.Scale);
                    planetId = planet.PlanetId;
                    planetLevel = math.max(1, planet.PlanetLevel);
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Looks up a live planet by stable id (server or client ghosts).
        /// Skips the client gather during join settle so we do not Crash!!! the EntityManager.
        /// </summary>
        static bool TryFindLivePlanetById(
            EntityManager em,
            int planetId,
            out float3 planetPos,
            out float planetSize,
            out int planetLevel,
            out bool isHomePlanet,
            out TeamId ownership)
        {
            planetPos = float3.zero;
            planetSize = 0f;
            planetLevel = 1;
            isHomePlanet = false;
            ownership = TeamId.None;

            var world = em.World;
            bool isClient = world != null && world.IsClient();
            if (isClient && ClientJoinSettleCache.ShouldSkipMapBodyQueries)
                return false;

            using var planets = em.CreateEntityQuery(
                ComponentType.ReadOnly<PlanetState>(),
                ComponentType.ReadOnly<LocalTransform>(),
                ComponentType.ReadOnly<PlanetTag>());
            using var entities = planets.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
            {
                var planet = em.GetComponentData<PlanetState>(entities[i]);
                if (planet.PlanetId != planetId)
                    continue;

                var lt = em.GetComponentData<LocalTransform>(entities[i]);
                planetPos = lt.Position;
                planetSize = math.max(0.25f, lt.Scale);
                planetLevel = math.max(1, planet.PlanetLevel);
                isHomePlanet = planet.IsHomePlanet;
                ownership = planet.Ownership;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Picks a world position just outside the planet body, inside the moon's inward
        /// dock reach (so a later moon pass cannot capture the hull), and outside the
        /// gem-moon's current dock / landing sphere.
        /// </summary>
        /// <param name="planetPos">Planet world position (canonical tile).</param>
        /// <param name="planetSize">Planet uniform scale (world radius proxy).</param>
        /// <param name="planetLevel">Planet level (orbit radii currently ignore level; API parity).</param>
        /// <param name="planetId">Stable planet id — seeds the same moon phase as dock / visuals.</param>
        /// <param name="isHomePlanet">True for homeworlds (larger moon → larger dock zone).</param>
        /// <param name="elapsedSeconds">Shared ServerTick orbit clock for the live moon angle.</param>
        /// <param name="randomSeed">Per-spawn seed so successive respawns land at different points.</param>
        public static float3 PickInteriorRingSpawnOutsideMoon(
            float3 planetPos,
            float planetSize,
            int planetLevel,
            int planetId,
            bool isHomePlanet,
            double elapsedSeconds,
            uint randomSeed)
        {
            // --- Interior disc: just outside the planet body, far inside the moon's dock reach ---
            // [TITAN-ORBIT] The moon rides the orbit-ring centerline. Its dock sphere reaches
            // inward. A spawn near orbit-inner looked "off the ring" but still got captured
            // when the moon later swept that angle. Cap rMax by (orbit center − dock − margin)
            // so every spawn stays inside that future sweep, not only the current moon pose.
            PlanetOrbitMath.GetRingRadiiWorld(
                planetSize, planetLevel, out float orbitInnerWorld, out _, out float orbitCenterWorld);
            if (orbitInnerWorld < 0.01f || orbitCenterWorld < 0.01f)
                return planetPos + new float3(HomeSpawnOffsetX, 0f, 0f);

            float bodyKeepOut = planetSize * PlanetBodyRadiusLocal + ShipRadiusEstimate;
            float rMin = bodyKeepOut;

            float dockZone = PlanetGemMoonMath.GetMoonDockZoneRadiusWorld(planetSize, isHomePlanet);
            float keepOut = dockZone + MoonDockExclusionMarginWorld;
            float rMaxMoonSweep = orbitCenterWorld - keepOut;
            float rMaxOrbit = orbitInnerWorld - OrbitRingInnerClearanceWorld;
            float rMax = math.min(rMaxOrbit, rMaxMoonSweep);
            if (rMax < rMin)
                rMax = rMin;

            // --- Live moon world pose (same formula as ShipMoonDockSystem) ---
            float3 moonOffset = PlanetOrbitMath.GetShipOrbitRingOffset(
                planetSize, planetLevel, PlanetOrbitMath.GetShipOrbitPhaseOffset(planetId), elapsedSeconds);
            float3 moonWorld = planetPos + moonOffset;
            float moonTheta = math.atan2(moonOffset.z, moonOffset.x);

            float mapW = 0f;
            float mapH = 0f;
            bool hasMap = ToroidalMapEcs.TryGetMapSize(out mapW, out mapH);

            // --- Rejection sample: random point close to the planet, skip moon disc ---
            // Square the blend so samples cluster toward rMin (near the body), not the outer lip.
            var rng = Random.CreateFromIndex(randomSeed);
            for (int attempt = 0; attempt < MaxInteriorSampleAttempts; attempt++)
            {
                float u = rng.NextFloat();
                u *= u;
                float r = math.lerp(rMin, rMax, u);
                float theta = rng.NextFloat() * (math.PI * 2f);
                float3 candidate = planetPos + new float3(math.cos(theta), 0f, math.sin(theta)) * r;
                if (hasMap)
                    candidate = ToroidalMapEcs.Wrap(candidate, mapW, mapH);

                float distToMoon = hasMap
                    ? ToroidalMapEcs.ToroidalDistance(candidate, moonWorld, mapW, mapH)
                    : math.distance(candidate, moonWorld);
                if (distToMoon >= keepOut)
                    return candidate;
            }

            // --- Fallback: opposite the moon at the inner ring (farthest safe interior point) ---
            // If every random sample landed in the dock disc (tiny planet + huge home moon),
            // the antipode at rMin maximises radial + angular distance from the moon.
            float3 fallback = planetPos + new float3(math.cos(moonTheta + math.PI), 0f, math.sin(moonTheta + math.PI)) * rMin;
            if (hasMap)
                fallback = ToroidalMapEcs.Wrap(fallback, mapW, mapH);
            return fallback;
        }

        /// <summary>
        /// Builds a per-spawn RNG seed from team, planet id, and orbit clock so consecutive
        /// respawns (and different teams) rarely land on the same point.
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
