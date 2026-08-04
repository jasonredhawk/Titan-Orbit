using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Generation;
using TitanOrbit.NetCode;
using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// [HYBRID] Quarantine-safe obstacle spheres for <b>cosmetic</b> bullet impact prediction.
    /// <para>
    /// Walks <see cref="EcsWorldVisualizer"/> hybrid proxy entity keys only — per-entity
    /// <c>HasComponent</c> / <c>GetComponentData</c> — never full asteroid/planet
    /// <c>ToEntityArray</c> (Windows late-join Crash!!! after Settling OFF).
    /// </para>
    /// <para>
    /// Damage stays server-authoritative in <see cref="BulletSimulationSystem"/>. This cache only
    /// lets <see cref="BulletVfxDriver"/> destroy tracers and play impact VFX early so bullets do
    /// not visually tunnel through rocks while waiting for <see cref="BulletHitRpc"/>.
    /// </para>
    /// </summary>
    public static class BulletCosmeticHitQuery
    {
        /// <summary>One sphere (or moon orbiting a planet) the cosmetic tracer may collide with.</summary>
        public struct Obstacle
        {
            /// <summary>Planet body, asteroid rock, enemy ship hull, or enemy gem-moon shield.</summary>
            public ObstacleKind Kind;

            /// <summary>
            /// Hybrid-proxy entity this sphere was built from (asteroid / planet / ship).
            /// Used by floating-count feedback to read <see cref="AsteroidState"/> without a full gather.
            /// </summary>
            public Entity SourceEntity;

            /// <summary>
            /// Logical / unbounded XZ center. For moons this is the <b>planet</b> center —
            /// <see cref="TryHitSegment"/> recomputes the orbiting moon pose each test.
            /// </summary>
            public float3 LogicalCenter;

            /// <summary>Hit radius in world units (asteroid / planet / ship). Moons recompute from shield.</summary>
            public float Radius;

            /// <summary>Transform scale used for planet/moon radius helpers.</summary>
            public float Scale;

            /// <summary>Ship team or moon ownership — used to skip friendly hits.</summary>
            public byte TeamOrOwnership;

            /// <summary>[NETCODE] GhostOwner NetworkId for ships (skip own hull).</summary>
            public int OwnerNetworkId;

            /// <summary>Moon orbit inputs (ignored for non-moon kinds).</summary>
            public int PlanetLevel;
            public int PlanetId;
            public bool IsHomePlanet;
            public float CurrentShield;
        }

        /// <summary>Obstacle category — mirrors server <c>TryResolveBulletHit</c> order.</summary>
        public enum ObstacleKind : byte
        {
            Planet = 0,
            Moon = 1,
            Ship = 2,
            Asteroid = 3,
        }

        /// <summary>How often to rebuild the sphere list while tracers are flying (frames).</summary>
        public const int RefreshIntervalFrames = 2;

        static readonly List<Obstacle> Obstacles = new List<Obstacle>(512);
        static readonly List<Entity> ProxyScratch = new List<Entity>(512);
        static int s_LastRefreshFrame = -1;
        // [TITAN-ORBIT] 0 = unset — never invent 1000×1000 for cosmetic hit tests.
        static float s_MapW;
        static float s_MapH;

        /// <summary>Read-only view of the last rebuilt obstacle list.</summary>
        public static IReadOnlyList<Obstacle> CurrentObstacles => Obstacles;

        /// <summary>Toroidal map width used by the last refresh.</summary>
        public static float MapWidth => s_MapW;

        /// <summary>Toroidal map height used by the last refresh.</summary>
        public static float MapHeight => s_MapH;

        /// <summary>
        /// Rebuilds the obstacle list from hybrid proxies when due (or forced).
        /// Safe to call every LateUpdate — no-ops until the interval elapses.
        /// </summary>
        /// <param name="force">True to rebuild even if the frame interval has not elapsed.</param>
        /// <returns>True when a non-empty list is available for testing.</returns>
        public static bool TryRefresh(bool force = false)
        {
            // --- Join gate: proxies incomplete during Instantiates storms ---
            // [TITAN-ORBIT] Settling / GhostSpawnBacklog — skip prediction; HitRpc still destroys tracers.
            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
            {
                Obstacles.Clear();
                return false;
            }

            int frame = Time.frameCount;
            if (!force && s_LastRefreshFrame >= 0 &&
                (frame - s_LastRefreshFrame) < RefreshIntervalFrames &&
                Obstacles.Count > 0)
                return true;

            s_LastRefreshFrame = frame;
            Obstacles.Clear();

            var world = EcsGameBridge.ClientWorld;
            if (world == null || !world.IsCreated)
                return false;

            var em = world.EntityManager;
            var visualizer = EcsWorldVisualizer.Active;
            if (visualizer == null)
                return false;

            // --- Map size for toroidal unwrap ---
            // Missing size → skip cosmetic prediction (HitRpc still destroys tracers).
            if (!TryResolveMapSize(em))
                return false;

            // Scratch only — do not keep EntityManager queries alive across frames.
            visualizer.CopyLiveProxyEntities(ProxyScratch);

            for (int i = 0; i < ProxyScratch.Count; i++)
            {
                Entity entity = ProxyScratch[i];
                if (!em.Exists(entity) || !em.HasComponent<LocalTransform>(entity))
                    continue;

                var lt = em.GetComponentData<LocalTransform>(entity);

                // --- Planets (+ optional enemy moon shield as a second obstacle) ---
                // Per-entity HasComponent — not GatherEntitiesWithoutFilter over all planets.
                if (em.HasComponent<PlanetTag>(entity) && em.HasComponent<PlanetState>(entity))
                {
                    var planet = em.GetComponentData<PlanetState>(entity);
                    float planetScale = math.max(0.25f, lt.Scale);
                    Obstacles.Add(new Obstacle
                    {
                        Kind = ObstacleKind.Planet,
                        SourceEntity = entity,
                        LogicalCenter = lt.Position,
                        Radius = BodyCollisionMath.GetPlanetBodyRadiusWorld(planetScale),
                        Scale = planetScale,
                        TeamOrOwnership = (byte)planet.Ownership,
                        PlanetLevel = planet.PlanetLevel,
                        PlanetId = planet.PlanetId,
                        IsHomePlanet = planet.IsHomePlanet,
                    });

                    // Enemy moon only when PlanetGemMoonState is present (ghosted with planet).
                    if (em.HasComponent<PlanetGemMoonState>(entity))
                    {
                        var moon = em.GetComponentData<PlanetGemMoonState>(entity);
                        float hitRadius = PlanetGemMoonMath.GetMoonBulletHitRadiusWorld(
                            planetScale, planet.IsHomePlanet, moon.CurrentShield);
                        Obstacles.Add(new Obstacle
                        {
                            Kind = ObstacleKind.Moon,
                            SourceEntity = entity,
                            LogicalCenter = lt.Position,
                            Radius = hitRadius,
                            Scale = planetScale,
                            TeamOrOwnership = (byte)planet.Ownership,
                            PlanetLevel = planet.PlanetLevel,
                            PlanetId = planet.PlanetId,
                            IsHomePlanet = planet.IsHomePlanet,
                            CurrentShield = moon.CurrentShield,
                        });
                    }

                    continue;
                }

                // --- Asteroids ---
                if (em.HasComponent<AsteroidTag>(entity) && em.HasComponent<AsteroidState>(entity))
                {
                    var asteroid = em.GetComponentData<AsteroidState>(entity);
                    // Mirror server — Health<=0 is already a kill even if IsDestroyed lags.
                    if (asteroid.IsDestroyed || asteroid.Health <= 0f)
                        continue;
                    // HitRpc may have culled while ghost Health still looks alive.
                    if (em.HasComponent<AsteroidClientCulledTag>(entity))
                        continue;

                    Obstacles.Add(new Obstacle
                    {
                        Kind = ObstacleKind.Asteroid,
                        SourceEntity = entity,
                        LogicalCenter = lt.Position,
                        Radius = BulletCollision.AsteroidHitRadius(lt.Scale),
                        Scale = lt.Scale,
                    });
                    continue;
                }

                // --- Enemy ships (hull proxies live in the same dictionary) ---
                // Ships are few; reading LocalTransform per proxy key is quarantine-safe.
                if (em.HasComponent<ShipTag>(entity) && em.HasComponent<ShipState>(entity))
                {
                    var ship = em.GetComponentData<ShipState>(entity);
                    if (ship.IsDead)
                        continue;

                    int networkId = 0;
                    if (em.HasComponent<GhostOwner>(entity))
                        networkId = em.GetComponentData<GhostOwner>(entity).NetworkId;

                    Obstacles.Add(new Obstacle
                    {
                        Kind = ObstacleKind.Ship,
                        SourceEntity = entity,
                        LogicalCenter = lt.Position,
                        Radius = BodyCollisionMath.GetShipHullRadiusWorld(lt.Scale),
                        Scale = lt.Scale,
                        TeamOrOwnership = (byte)ship.Team,
                        OwnerNetworkId = networkId,
                    });
                }
            }

            return Obstacles.Count > 0;
        }

        /// <summary>
        /// Swept segment vs all cached obstacles. Earliest contact along [from, to] wins.
        /// Must match server <c>BulletSimulationSystem.TryResolveBulletHit</c> nearest-t rule
        /// so optimistic floats/VFX land on the same body the server damages.
        /// </summary>
        /// <param name="from">Segment start (logical or display — must match <paramref name="isDisplaySpace"/>).</param>
        /// <param name="to">Segment end.</param>
        /// <param name="ownerTeam">Firing team — skips friendly ships/moons.</param>
        /// <param name="ownerNetworkId">Shooter NetworkId — skips own hull.</param>
        /// <param name="isDisplaySpace">True when the tracer flies in presentation / display coords.</param>
        /// <param name="hitPoint">Contact point in the same space as from/to.</param>
        /// <param name="hitKind">Which obstacle category was hit (asteroid / planet / …).</param>
        /// <param name="hitEntity">Hybrid-proxy entity for the hit body (Null if none).</param>
        /// <param name="damageFilter">
        /// [TITAN-ORBIT] Matches server <c>BulletDamageFilter</c> so mining tracers pass through
        /// ships and fighter tracers pass through asteroids.
        /// </param>
        public static bool TryHitSegment(
            float3 from,
            float3 to,
            byte ownerTeam,
            int ownerNetworkId,
            bool isDisplaySpace,
            out float3 hitPoint,
            out ObstacleKind hitKind,
            out Entity hitEntity,
            byte damageFilter = 0)
        {
            hitPoint = to;
            hitKind = ObstacleKind.Asteroid;
            hitEntity = Entity.Null;
            if (Obstacles.Count == 0)
                return false;

            bool hasRef = ToroidalDisplay.TryGetReferencePosition(out Vector3 reference);
            double moonElapsed = PlanetGemMoonOrbitClock.TryGetElapsedSeconds(
                out double orbitElapsed, includeTickFraction: true)
                ? orbitElapsed
                : Time.timeAsDouble;

            float bestT = float.MaxValue;
            float3 bestHit = to;
            ObstacleKind bestKind = ObstacleKind.Asteroid;
            Entity bestEntity = Entity.Null;
            bool any = false;
            float3 delta = to - from;
            float deltaLenSq = math.lengthsq(delta);
            var filter = (BulletDamageFilter)damageFilter;

            for (int i = 0; i < Obstacles.Count; i++)
            {
                var o = Obstacles[i];
                if (!PassesTeamFilter(in o, ownerTeam, ownerNetworkId))
                    continue;
                if (!PassesDamageFilter(filter, o.Kind))
                    continue;

                bool hit;
                float3 hp;

                if (o.Kind == ObstacleKind.Moon)
                {
                    // --- Same unwrap origin as server SegmentHitsMoonNear (segment start) ---
                    // [TITAN-ORBIT] Do not unwrap moons from the ship reference while the segment
                    // starts at the muzzle — that disagreed with server and mis-ordered hits.
                    float radius = PlanetGemMoonMath.GetMoonBulletHitRadiusWorld(
                        o.Scale, o.IsHomePlanet, o.CurrentShield);
                    hit = BulletCollision.SegmentHitsMoonNear(
                        from, to, o.LogicalCenter, o.Scale,
                        o.PlanetLevel, o.PlanetId, moonElapsed,
                        o.IsHomePlanet, radius, s_MapW, s_MapH, out hp);
                }
                else
                {
                    // --- Match server TryResolveBulletHit ---
                    // [TITAN-ORBIT] Local anticipation tracers fly in unbounded ship space (same
                    // frame as sim LocalTransform). Old path used Euclidean vs
                    // ToDisplayPosition(center, shipRef) which could pick a different rock than
                    // SegmentHitsSphereToroidal(from, …) — floats on A, server damage on B.
                    // Keep isDisplaySpace only for callers that need the flag; hit math is toroidal.
                    _ = isDisplaySpace;
                    _ = hasRef;
                    _ = reference;
                    hit = BulletCollision.SegmentHitsSphereToroidal(
                        from, to, o.LogicalCenter, o.Radius, s_MapW, s_MapH, out hp);
                }

                if (!hit)
                    continue;

                if (TryUpdateBest(from, delta, deltaLenSq, hp, ref bestT, ref bestHit))
                {
                    any = true;
                    bestKind = o.Kind;
                    bestEntity = o.SourceEntity;
                }
            }

            if (!any)
                return false;

            hitPoint = bestHit;
            hitKind = bestKind;
            hitEntity = bestEntity;
            return true;
        }

        /// <summary>
        /// Overload kept for callers that only need the contact point.
        /// </summary>
        public static bool TryHitSegment(
            float3 from,
            float3 to,
            byte ownerTeam,
            int ownerNetworkId,
            bool isDisplaySpace,
            out float3 hitPoint)
        {
            return TryHitSegment(
                from, to, ownerTeam, ownerNetworkId, isDisplaySpace,
                out hitPoint, out _, out _);
        }

        /// <summary>Keeps the contact closest to segment start (parameter t in [0,1]).</summary>
        static bool TryUpdateBest(
            float3 from,
            float3 delta,
            float deltaLenSq,
            float3 hp,
            ref float bestT,
            ref float3 bestHit)
        {
            float t;
            if (deltaLenSq < 1e-8f)
                t = 0f;
            else
                t = math.dot(hp - from, delta) / deltaLenSq;

            if (t > bestT)
                return false;

            bestT = t;
            bestHit = hp;
            return true;
        }

        /// <summary>
        /// Team / self filters matching server <c>TryResolveBulletHit</c>.
        /// Planets and asteroids always collide; moons/ships skip friendlies.
        /// </summary>
        static bool PassesTeamFilter(in Obstacle o, byte ownerTeam, int ownerNetworkId)
        {
            var attacker = (TeamId)ownerTeam;

            if (o.Kind == ObstacleKind.Moon)
            {
                // Same gate as server — friendly moons do not absorb bullets.
                return !PlanetGemMoonCombatLogic.IsTeamFriendlyToMoon((TeamId)o.TeamOrOwnership, attacker);
            }

            if (o.Kind == ObstacleKind.Ship)
            {
                if (o.TeamOrOwnership == ownerTeam)
                    return false;
                if (ownerNetworkId > 0 && o.OwnerNetworkId == ownerNetworkId)
                    return false;
                return true;
            }

            // Planet absorb + asteroid mining — always test.
            return true;
        }

        /// <summary>
        /// [TITAN-ORBIT] Mirrors server <c>BulletSimulationSystem.AllowsHitKind</c> for cosmetic tracers.
        /// Planets always block; mining skips ships/moons; fighters skip asteroids/moons;
        /// planetary defense clips ships and asteroids (transports are server-only for now).
        /// </summary>
        /// <param name="filter">Spawn-time mask from the tracer request (byte cast).</param>
        /// <param name="kind">Hybrid-proxy obstacle class under test.</param>
        /// <returns>True when the cosmetic tracer should stop on this kind.</returns>
        static bool PassesDamageFilter(BulletDamageFilter filter, ObstacleKind kind)
        {
            // Planets are solid world for every filter (same as server).
            if (kind == ObstacleKind.Planet)
                return true;

            switch (filter)
            {
                case BulletDamageFilter.Everything:
                    return true;
                case BulletDamageFilter.AsteroidsOnly:
                    return kind == ObstacleKind.Asteroid;
                case BulletDamageFilter.ShipsOnly:
                    return kind == ObstacleKind.Ship;
                case BulletDamageFilter.ShipsAndTransports:
                    // PD: ships + asteroids. Transports are not in this hybrid list yet —
                    // server BulletSimulation owns real transport hits.
                    return kind == ObstacleKind.Ship || kind == ObstacleKind.Asteroid;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Reads map size from MapState, session meta, or ToroidalMapEcs cache.
        /// Returns false when missing — never invents a period.
        /// </summary>
        static bool TryResolveMapSize(EntityManager em)
        {
            using var mapQuery = em.CreateEntityQuery(typeof(MapStateSingleton));
            if (mapQuery.TryGetSingleton<MapStateSingleton>(out var map) &&
                ToroidalMapEcs.IsValidMapSize(map.MapWidth, map.MapHeight))
            {
                s_MapW = map.MapWidth;
                s_MapH = map.MapHeight;
                return true;
            }

            if (MapSessionMetaCache.HasMapSize)
            {
                s_MapW = MapSessionMetaCache.MapWidth;
                s_MapH = MapSessionMetaCache.MapHeight;
                return true;
            }

            if (ToroidalMapEcs.TryGetMapSize(out float w, out float h))
            {
                s_MapW = w;
                s_MapH = h;
                return true;
            }

            s_MapW = 0f;
            s_MapH = 0f;
            return false;
        }

        /// <summary>Clears cache when leaving a match / disabling the VFX driver.</summary>
        public static void Clear()
        {
            Obstacles.Clear();
            ProxyScratch.Clear();
            s_LastRefreshFrame = -1;
        }

        /// <summary>
        /// Finds the asteroid that best matches a server impact point (surface fit).
        /// <para>
        /// [TITAN-ORBIT] Do <b>not</b> pick nearest-center among overlapping spheres. A surface hit
        /// on rock A toward neighbor B often lies closer to B’s center — logs showed HitRpc
        /// remaining HP / hide applied to the wrong proxy (e.g. server kill at -150 while client
        /// hid the rock at -146). Score = |dist(hit,center) − hitRadius|; lowest wins.
        /// When <paramref name="asteroidHealthAfter"/> ≥ 0, reject candidates whose ghost Health
        /// is already below the server remaining (impossible for the damaged rock).
        /// </para>
        /// Quarantine-safe — hybrid proxy keys only. Skips already-hidden proxies.
        /// </summary>
        /// <param name="hitDisplayPos">Server hit position converted to display space.</param>
        /// <param name="asteroidEntity">Best surface-fit live asteroid, or Null.</param>
        /// <param name="asteroidHealthAfter">
        /// Server Health after this hit (&lt; 0 = unknown / non-asteroid). Used as a soft filter.
        /// </param>
        /// <returns>True when a live proxy rock matches the impact.</returns>
        public static bool TryFindAsteroidAtImpact(
            Vector3 hitDisplayPos,
            out Entity asteroidEntity,
            float asteroidHealthAfter = -1f)
        {
            asteroidEntity = Entity.Null;
            var visualizer = EcsWorldVisualizer.Active;
            if (visualizer == null)
                return false;

            var world = EcsGameBridge.ClientWorld;
            if (world == null || !world.IsCreated)
                return false;

            var em = world.EntityManager;
            visualizer.CopyLiveProxyEntities(ProxyScratch);

            bool hasRef = ToroidalDisplay.TryGetReferencePosition(out Vector3 reference);
            // Lowest surface residual wins (0 = hit sits exactly on the bullet sphere).
            float bestSurfaceError = float.MaxValue;
            Entity best = Entity.Null;
            float3 hit = new float3(hitDisplayPos.x, 0f, hitDisplayPos.z);
            bool hasAuthHp = asteroidHealthAfter >= 0f;

            for (int i = 0; i < ProxyScratch.Count; i++)
            {
                Entity entity = ProxyScratch[i];
                if (!em.Exists(entity) ||
                    !em.HasComponent<AsteroidTag>(entity) ||
                    !em.HasComponent<AsteroidState>(entity) ||
                    !em.HasComponent<LocalTransform>(entity))
                    continue;

                // Already-hidden kill proxies stay in the dictionary — skip them so a neighbor
                // hit cannot re-attribute floats/hide to a dead GO.
                if (!visualizer.TryGetProxy(entity, out var proxyGo) ||
                    proxyGo == null ||
                    !proxyGo.activeSelf)
                    continue;

                var state = em.GetComponentData<AsteroidState>(entity);
                if (state.IsDestroyed || state.Health <= 0f)
                    continue;

                // Ghost already below server post-hit HP → cannot be the rock that was just hit
                // (that rock's ghost is equal or still higher due to lag). Rejects wrong neighbors
                // that had lower HP than the authoritative remaining (log line 78: rem 39 vs ghost 25).
                if (hasAuthHp && state.Health + 0.5f < asteroidHealthAfter)
                    continue;

                var lt = em.GetComponentData<LocalTransform>(entity);
                float3 logical = lt.Position;
                float3 display = hasRef
                    ? (float3)ToroidalDisplay.ToDisplayPosition(logical, reference)
                    : logical;
                display.y = 0f;

                float hitRadius = BulletCollision.AsteroidHitRadius(lt.Scale);
                // Tight slack — only absorb network/display jitter, not a neighbor’s center.
                float maxDist = hitRadius + 0.35f;
                float dist = math.distance(display, hit);
                if (dist > maxDist)
                    continue;

                float surfaceError = math.abs(dist - hitRadius);
                if (surfaceError >= bestSurfaceError)
                    continue;

                bestSurfaceError = surfaceError;
                best = entity;
            }

            if (best == Entity.Null)
                return false;

            asteroidEntity = best;
            return true;
        }

        /// <summary>
        /// Finds the nearest live asteroid hybrid proxy within <paramref name="maxDistance"/>.
        /// Prefer <see cref="TryFindAsteroidAtImpact"/> for HitRpc mining floats.
        /// </summary>
        /// <param name="hitDisplayPos">Impact position in display / presentation space.</param>
        /// <param name="maxDistance">Search radius in world units.</param>
        /// <param name="asteroidEntity">Matching asteroid ghost entity, or Null.</param>
        /// <returns>True when a live proxy is within range.</returns>
        public static bool TryFindNearestAsteroidEntity(
            Vector3 hitDisplayPos,
            float maxDistance,
            out Entity asteroidEntity)
        {
            asteroidEntity = Entity.Null;
            var visualizer = EcsWorldVisualizer.Active;
            if (visualizer == null)
                return false;

            var world = EcsGameBridge.ClientWorld;
            if (world == null || !world.IsCreated)
                return false;

            var em = world.EntityManager;
            visualizer.CopyLiveProxyEntities(ProxyScratch);

            bool hasRef = ToroidalDisplay.TryGetReferencePosition(out Vector3 reference);
            float maxDistSq = maxDistance * maxDistance;
            float bestDistSq = maxDistSq;
            Entity best = Entity.Null;
            float3 hit = new float3(hitDisplayPos.x, 0f, hitDisplayPos.z);

            for (int i = 0; i < ProxyScratch.Count; i++)
            {
                Entity entity = ProxyScratch[i];
                if (!em.Exists(entity) ||
                    !em.HasComponent<AsteroidTag>(entity) ||
                    !em.HasComponent<AsteroidState>(entity) ||
                    !em.HasComponent<LocalTransform>(entity))
                    continue;

                var state = em.GetComponentData<AsteroidState>(entity);
                if (state.IsDestroyed || state.Health <= 0f)
                    continue;

                float3 logical = em.GetComponentData<LocalTransform>(entity).Position;
                float3 display = hasRef
                    ? (float3)ToroidalDisplay.ToDisplayPosition(logical, reference)
                    : logical;
                display.y = 0f;

                float distSq = math.distancesq(display, hit);
                if (distSq > bestDistSq)
                    continue;

                bestDistSq = distSq;
                best = entity;
            }

            if (best == Entity.Null)
                return false;

            asteroidEntity = best;
            return true;
        }
    }
}
