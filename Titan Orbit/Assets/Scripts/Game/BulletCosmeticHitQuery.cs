using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Entities;
using TitanOrbit.Generation;
using TitanOrbit.NetCode;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Physics;
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
    /// not visually tunnel through rocks / ships / planetary-defense turrets while waiting for
    /// <see cref="BulletHitRpc"/>. Turrets are not ghosts — we derive the same pad spheres the
    /// server uses in <see cref="PlanetaryDefenseHitScan"/> from the planet's slot buffer.
    /// </para>
    /// </summary>
    public static class BulletCosmeticHitQuery
    {
        /// <summary>One sphere (or moon orbiting a planet) the cosmetic tracer may collide with.</summary>
        public struct Obstacle
        {
            /// <summary>
            /// Planet body, asteroid rock, enemy ship hull, gem-moon body/shield, or
            /// planetary-defense turret pad.
            /// </summary>
            public ObstacleKind Kind;

            /// <summary>
            /// Hybrid-proxy entity this sphere was built from (asteroid / planet / ship).
            /// Used by floating-count feedback to read <see cref="AsteroidState"/> without a full gather.
            /// </summary>
            public Entity SourceEntity;

            /// <summary>
            /// Logical / unbounded XZ center. For moons this is the <b>planet</b> center —
            /// <see cref="TryHitSegment"/> recomputes the orbiting moon pose each test.
            /// For planetary-defense pads this is the slot world position (not the planet center).
            /// </summary>
            public float3 LogicalCenter;

            /// <summary>
            /// Hit radius in world units (asteroid / planet / ship / turret pad).
            /// Moons recompute from shield. Turret radius is the base pad sphere —
            /// <see cref="TryHitSegment"/> expands it by bullet scale like the server.
            /// </summary>
            public float Radius;

            /// <summary>Transform scale used for planet/moon radius helpers.</summary>
            public float Scale;

            /// <summary>
            /// Ship team (friendly/self skip) or moon ownership (friendly shield pass-through).
            /// </summary>
            public byte TeamOrOwnership;

            /// <summary>[NETCODE] GhostOwner NetworkId for ships (skip own hull).</summary>
            public int OwnerNetworkId;

            /// <summary>Moon orbit inputs (ignored for non-moon kinds).</summary>
            public int PlanetLevel;
            public int PlanetId;
            public bool IsHomePlanet;
            public float CurrentShield;

            /// <summary>
            /// Planetary-defense slot index on <see cref="PlanetId"/>. −1 for every other kind.
            /// Identifies which pad this sphere belongs to (kill skip uses the HitRpc HP store).
            /// </summary>
            public int SlotIndex;
        }

        /// <summary>Obstacle category — mirrors server <c>TryResolveBulletHit</c> order.</summary>
        public enum ObstacleKind : byte
        {
            Planet = 0,
            Moon = 1,
            Ship = 2,
            Asteroid = 3,
            /// <summary>
            /// Derived pad sphere on an owned planet (not a ghost). Same math as
            /// <see cref="PlanetaryDefenseHitScan"/>.
            /// </summary>
            PlanetaryDefense = 4,
            /// <summary>People-transport sphere from the client VFX driver (not a ghost).</summary>
            Transport = 5,
            /// <summary>Derived shield-drone sphere from owner ship equipment.</summary>
            Drone = 6,
        }

        /// <summary>How often to rebuild the sphere list while tracers are flying (frames).</summary>
        public const int RefreshIntervalFrames = 2;

        static readonly List<Obstacle> Obstacles = new List<Obstacle>(512);
        static readonly List<Entity> ProxyScratch = new List<Entity>(512);
        static readonly List<Entity> ShipProxyScratch = new List<Entity>(64);
        static readonly List<DroneHitTarget> DroneScratch = new List<DroneHitTarget>(64);
        static readonly List<int> DroneRearScratch = new List<int>(8);
        static readonly List<int> DroneShieldScratch = new List<int>(8);
        static readonly List<int> DroneEnemyIdsScratch = new List<int>(16);
        static readonly Dictionary<int, float3> DroneEnemyPos = new Dictionary<int, float3>(16);
        static readonly Dictionary<int, DroneSwarmPositioning.ShieldAssignment> DroneShieldAssign =
            new Dictionary<int, DroneSwarmPositioning.ShieldAssignment>(8);
        static int s_LastRefreshFrame = -1;
        // [TITAN-ORBIT] 0 = unset — never invent 1000×1000 for cosmetic hit tests.
        static float s_MapW;
        static float s_MapH;

        /// <summary>
        /// Family catalog for per-planet turret recipes. Loaded once — same Resources path
        /// as server <c>PlanetaryDefenseHitScan</c>.
        /// </summary>
        static PlanetShipFamilyConfig s_FamilyConfig;

        /// <summary>True after the first attempt to load family / default turret configs.</summary>
        static bool s_DefenseConfigWarmed;

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
            ShipProxyScratch.Clear();

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

                    // --- Gem moon (every ownership) — body always blocks; shield team-gated ---
                    // [TITAN-ORBIT] Refresh stores ownership + shield; TryHitSegment picks body vs
                    // shield radius from the firing team (friendly bullets ignore the bubble).
                    if (em.HasComponent<PlanetGemMoonState>(entity))
                    {
                        var moon = em.GetComponentData<PlanetGemMoonState>(entity);
                        Obstacles.Add(new Obstacle
                        {
                            Kind = ObstacleKind.Moon,
                            SourceEntity = entity,
                            LogicalCenter = lt.Position,
                            // Placeholder — TryHitSegment recomputes body vs shield from ownerTeam.
                            Radius = PlanetGemMoonMath.GetMoonBodyRadiusWorld(
                                planetScale, planet.IsHomePlanet),
                            Scale = planetScale,
                            TeamOrOwnership = (byte)planet.Ownership,
                            PlanetLevel = planet.PlanetLevel,
                            PlanetId = planet.PlanetId,
                            IsHomePlanet = planet.IsHomePlanet,
                            CurrentShield = moon.CurrentShield,
                        });
                    }

                    // --- Planetary defense turrets (derived pad spheres, not ghosts) ---
                    // [TITAN-ORBIT] Server BulletSimulationSystem hits these via PlanetaryDefenseHitScan.
                    // Without this list, tracers fly through the gun while HitRpc still punches HP —
                    // the player sees damage with no impact. Same per-entity buffer read as
                    // PlanetaryDefenseVisualDriver (proxy key only — no planet ToEntityArray).
                    AppendPlanetaryDefenseObstacles(em, entity, in planet, lt.Position, planetScale);

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
                        Radius = BulletCollision.AsteroidHitRadiusForSweep(lt.Scale)
                                 + BodyCollisionMath.AsteroidVisualDisplacementLocal * math.max(0.1f, lt.Scale),
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

                    // [TITAN-ORBIT] Match server BulletSimulationSystem — attribute-grown
                    // PhysicsCollider XZ AABB (fallback tier sphere when collider missing).
                    float shipRadius;
                    if (em.HasComponent<PhysicsCollider>(entity))
                    {
                        var physicsCollider = em.GetComponentData<PhysicsCollider>(entity);
                        shipRadius = MegaShipCombatAim.GetHitRadiusWorld(
                            em, entity, physicsCollider, lt.Scale);
                    }
                    else
                    {
                        shipRadius = BodyCollisionMath.GetShipHullRadiusWorld(lt.Scale);
                    }

                    Obstacles.Add(new Obstacle
                    {
                        Kind = ObstacleKind.Ship,
                        SourceEntity = entity,
                        LogicalCenter = MegaShipCombatAim.GetAimPoint(em, entity, lt),
                        Radius = shipRadius,
                        Scale = lt.Scale,
                        TeamOrOwnership = (byte)ship.Team,
                        OwnerNetworkId = networkId,
                    });
                    ShipProxyScratch.Add(entity);
                }
            }

            PeopleTransportVfxDriver.AppendBulletObstacles(Obstacles);
            AppendDroneObstacles(em);

            return Obstacles.Count > 0;
        }

        /// <summary>
        /// Derived shield-drone spheres from ship-proxy equipment (no drone ghosts).
        /// Same <see cref="DroneSwarmHitScan.RebuildTargets"/> math as the server.
        /// </summary>
        static void AppendDroneObstacles(EntityManager em)
        {
            if (ShipProxyScratch.Count == 0)
                return;

            var ships = new NativeArray<Entity>(ShipProxyScratch.Count, Allocator.Temp);
            for (int i = 0; i < ShipProxyScratch.Count; i++)
                ships[i] = ShipProxyScratch[i];

            double timeSeconds = PlanetGemMoonOrbitClock.TryGetElapsedSeconds(
                out double elapsed, includeTickFraction: true)
                ? elapsed
                : Time.timeAsDouble;
            DroneSwarmHitScan.RebuildTargets(
                em,
                ships,
                ships,
                timeSeconds,
                s_MapW,
                s_MapH,
                DroneScratch,
                DroneRearScratch,
                DroneShieldScratch,
                DroneEnemyIdsScratch,
                DroneEnemyPos,
                DroneShieldAssign);
            ships.Dispose();

            for (int i = 0; i < DroneScratch.Count; i++)
            {
                var d = DroneScratch[i];
                float radius = DroneSwarmPositioning.DroneHitSphereRadius
                    * math.max(0.25f, d.HitRadiusScale > 0.01f ? d.HitRadiusScale : 1f);
                Obstacles.Add(new Obstacle
                {
                    Kind = ObstacleKind.Drone,
                    SourceEntity = d.ShipEntity,
                    LogicalCenter = d.Position,
                    Radius = radius,
                    TeamOrOwnership = d.Team,
                    OwnerNetworkId = d.OwnerNetworkId,
                });
            }
        }

        /// <summary>
        /// Swept segment vs all cached obstacles. Earliest contact along [from, to] wins.
        /// Must match server <c>BulletSimulationSystem.TryResolveBulletHit</c> nearest-t rule
        /// so optimistic floats/VFX land on the same body the server damages.
        /// Planetary-defense pads can steal a slightly nearer planet-body chord
        /// (<see cref="PlanetaryDefenseHitScan.PreferDefenseOverPlanetBody"/>) so tracers
        /// stop on the gun instead of the hull behind it.
        /// </summary>
        /// <param name="from">Segment start (logical or display — must match <paramref name="isDisplaySpace"/>).</param>
        /// <param name="to">Segment end.</param>
        /// <param name="ownerTeam">Firing team — skips friendly ships and friendly turrets.</param>
        /// <param name="ownerNetworkId">Shooter NetworkId — skips own hull.</param>
        /// <param name="isDisplaySpace">True when the tracer flies in presentation / display coords.</param>
        /// <param name="hitPoint">Contact point in the same space as from/to.</param>
        /// <param name="hitKind">Which obstacle category was hit (asteroid / planet / …).</param>
        /// <param name="hitEntity">Hybrid-proxy entity for the hit body (Null if none).</param>
        /// <param name="hitPlanetId">
        /// Stable planet id when <paramref name="hitKind"/> is planetary-defense; 0 otherwise.
        /// </param>
        /// <param name="hitSlotIndex">
        /// Defense slot index when the hit is a turret pad; −1 otherwise.
        /// </param>
        /// <param name="damageFilter">
        /// [TITAN-ORBIT] Matches server <c>BulletDamageFilter</c> so mining tracers pass through
        /// ships and fighter tracers pass through asteroids.
        /// </param>
        /// <param name="scaleMultiplier">
        /// Tracer visual scale. Expands turret (and only turret) spheres the same way
        /// server <see cref="PlanetaryDefenseHitScan.ExpandRadiusForBulletScale"/> does.
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
            out int hitPlanetId,
            out int hitSlotIndex,
            byte damageFilter = 0,
            float scaleMultiplier = 1f,
            int bankIndex = 0)
        {
            hitPoint = to;
            hitKind = ObstacleKind.Asteroid;
            hitEntity = Entity.Null;
            hitPlanetId = 0;
            hitSlotIndex = -1;
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
            int bestPlanetId = 0;
            int bestSlotIndex = -1;
            bool any = false;
            float3 delta = to - from;
            float deltaLenSq = math.lengthsq(delta);
            var filter = (BulletDamageFilter)damageFilter;
            bool healFriendly = BulletBankCombatLogic.HasHealFriendly(bankIndex);

            // --- Same-planet turret steal (mirrors server PreferDefenseOverPlanetBody) ---
            // Planet body often wins nearest-t by a hair because the pad sits on the hull.
            // We keep the best PD contact even when a planet chord was slightly nearer.
            float bestDefenseT = float.MaxValue;
            float3 bestDefenseHit = to;
            Entity bestDefenseEntity = Entity.Null;
            int bestDefensePlanetId = 0;
            int bestDefenseSlotIndex = -1;
            bool anyDefense = false;

            for (int i = 0; i < Obstacles.Count; i++)
            {
                var o = Obstacles[i];
                if (!PassesTeamFilter(in o, ownerTeam, ownerNetworkId, healFriendly))
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
                    // Friendly: body only (pass through shield). Hostile: shield shell when up.
                    bool friendlyMoon = PlanetGemMoonCombatLogic.IsTeamFriendlyToMoon(
                        (TeamId)o.TeamOrOwnership, (TeamId)ownerTeam);
                    float radius = PlanetGemMoonMath.GetMoonBulletHitRadiusWorld(
                        o.Scale, o.IsHomePlanet, o.CurrentShield, friendlyMoon);
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
                    float radius = o.Radius;
                    if (o.Kind == ObstacleKind.PlanetaryDefense)
                    {
                        // Heavy tracers get the same pad the server adds in TryKeepNearestTurretHit.
                        radius = PlanetaryDefenseHitScan.ExpandRadiusForBulletScale(
                            o.Radius, scaleMultiplier);
                    }
                    else if (o.Kind == ObstacleKind.Ship)
                    {
                        radius += math.clamp(scaleMultiplier * 0.18f, 0f, 0.85f);
                    }
                    else if (o.Kind == ObstacleKind.Asteroid)
                    {
                        float pad = math.clamp(
                            BulletCollision.AsteroidSweepPad + scaleMultiplier * 0.05f,
                            BulletCollision.AsteroidSweepPad,
                            0.45f);
                        radius = math.max(radius, BulletCollision.AsteroidHitRadius(o.Scale) + pad
                            + BodyCollisionMath.AsteroidVisualDisplacementLocal * math.max(0.1f, o.Scale));
                    }

                    hit = BulletCollision.SegmentHitsSphereToroidal(
                        from, to, o.LogicalCenter, radius, s_MapW, s_MapH, out hp);
                }

                if (!hit)
                    continue;

                // Remember the nearest turret even if a planet chord currently leads.
                if (o.Kind == ObstacleKind.PlanetaryDefense)
                {
                    float defenseT = deltaLenSq < 1e-8f
                        ? 0f
                        : math.dot(hp - from, delta) / deltaLenSq;
                    if (defenseT <= bestDefenseT)
                    {
                        bestDefenseT = defenseT;
                        bestDefenseHit = hp;
                        bestDefenseEntity = o.SourceEntity;
                        bestDefensePlanetId = o.PlanetId;
                        bestDefenseSlotIndex = o.SlotIndex;
                        anyDefense = true;
                    }
                }

                if (TryUpdateBest(from, delta, deltaLenSq, hp, ref bestT, ref bestHit))
                {
                    any = true;
                    bestKind = o.Kind;
                    bestEntity = o.SourceEntity;
                    bestPlanetId = o.Kind == ObstacleKind.PlanetaryDefense ? o.PlanetId : 0;
                    bestSlotIndex = o.Kind == ObstacleKind.PlanetaryDefense ? o.SlotIndex : -1;
                }
            }

            if (!any)
                return false;

            // --- Pad in front of the planet hull ---
            // [TITAN-ORBIT] Server lets a same-planet turret steal when it is only slightly
            // behind the body chord. Without this, tracers die on the planet (or fly through
            // the gun when the body miss) while HitRpc still damages the turret.
            if (anyDefense &&
                bestKind == ObstacleKind.Planet &&
                bestEntity == bestDefenseEntity &&
                PlanetaryDefenseHitScan.PreferDefenseOverPlanetBody(bestDefenseT, bestT))
            {
                bestHit = bestDefenseHit;
                bestKind = ObstacleKind.PlanetaryDefense;
                bestEntity = bestDefenseEntity;
                bestPlanetId = bestDefensePlanetId;
                bestSlotIndex = bestDefenseSlotIndex;
            }

            hitPoint = bestHit;
            hitKind = bestKind;
            hitEntity = bestEntity;
            hitPlanetId = bestPlanetId;
            hitSlotIndex = bestSlotIndex;
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
                out hitPoint, out _, out _, out _, out _, 0, 1f, 0);
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
        /// Planets/asteroids always collide; moons always test (friendly uses body-only radius);
        /// ships and planetary-defense turrets skip friendlies / self.
        /// </summary>
        static bool PassesTeamFilter(in Obstacle o, byte ownerTeam, int ownerNetworkId, bool healFriendly)
        {
            if (o.Kind == ObstacleKind.Ship)
            {
                if (ownerNetworkId > 0 && o.OwnerNetworkId == ownerNetworkId)
                    return false;
                if (healFriendly)
                    return true;
                if (o.TeamOrOwnership == ownerTeam)
                    return false;
                return true;
            }

            if (o.Kind == ObstacleKind.Transport || o.Kind == ObstacleKind.Drone)
            {
                if (o.TeamOrOwnership == ownerTeam)
                    return false;
                if (ownerNetworkId > 0 && o.OwnerNetworkId == ownerNetworkId)
                    return false;
                return true;
            }

            // [TITAN-ORBIT] Same-team pads are friendly-fire off on the server — tracers must
            // pass through so they can still hit a hostile behind your own gun.
            if (o.Kind == ObstacleKind.PlanetaryDefense)
                return o.TeamOrOwnership != ownerTeam;

            // Planet / moon / asteroid — always test. Moon shield vs body is radius-gated above.
            return true;
        }

        /// <summary>
        /// [TITAN-ORBIT] Mirrors server <c>BulletSimulationSystem.AllowsHitKind</c> for cosmetic tracers.
        /// Planets and moons always block; mining skips ships and turrets; fighters skip asteroids
        /// but still stop on enemy turrets; planetary-defense bolts clip ships and asteroids
        /// (transports are server-only for now).
        /// </summary>
        /// <param name="filter">Spawn-time mask from the tracer request (byte cast).</param>
        /// <param name="kind">Hybrid-proxy obstacle class under test.</param>
        /// <returns>True when the cosmetic tracer should stop on this kind.</returns>
        static bool PassesDamageFilter(BulletDamageFilter filter, ObstacleKind kind)
        {
            // Planets + moons are solid world for every filter (same as server).
            if (kind == ObstacleKind.Planet || kind == ObstacleKind.Moon)
                return true;

            switch (filter)
            {
                case BulletDamageFilter.Everything:
                    return true;
                case BulletDamageFilter.AsteroidsOnly:
                    // Mining: rocks only. Pass through ships, drones, and enemy turrets.
                    return kind == ObstacleKind.Asteroid;
                case BulletDamageFilter.ShipsOnly:
                    // Fighter: enemy ships + their drones + enemy planetary turrets.
                    return kind == ObstacleKind.Ship
                           || kind == ObstacleKind.Drone
                           || kind == ObstacleKind.PlanetaryDefense;
                case BulletDamageFilter.ShipsAndTransports:
                    // PD: ships + people transports + asteroids (same as server AllowsHitKind).
                    return kind == ObstacleKind.Ship
                           || kind == ObstacleKind.Transport
                           || kind == ObstacleKind.Asteroid;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Adds one pad sphere per active turret on this planet. Uses the same
        /// <see cref="PlanetaryDefenseMath.GetSlotWorldPosition"/> +
        /// <see cref="PlanetaryDefenseHitScan.ComputeTurretHitRadius"/> as the server so
        /// cosmetic tracers stop where damage is applied.
        /// </summary>
        /// <param name="em">Client world EntityManager (ghosted slot buffer is readable).</param>
        /// <param name="planetEntity">Planet hybrid-proxy entity (also the SourceEntity on each pad).</param>
        /// <param name="planet">Ghosted planet state (ownership, family, level, id).</param>
        /// <param name="planetPos">Logical planet center (unbounded XZ).</param>
        /// <param name="planetScale">Planet <c>LocalTransform.Scale</c>.</param>
        static void AppendPlanetaryDefenseObstacles(
            EntityManager em,
            Entity planetEntity,
            in PlanetState planet,
            float3 planetPos,
            float planetScale)
        {
            // Neutral planets have no pads. Owned planets with an empty buffer are still building.
            if (planet.Ownership == TeamId.None)
                return;
            if (!em.HasBuffer<PlanetaryDefenseSlotElement>(planetEntity))
                return;

            var buffer = em.GetBuffer<PlanetaryDefenseSlotElement>(planetEntity);
            if (buffer.Length == 0)
                return;

            EnsureDefenseConfigWarmed();
            var config = PlanetaryDefenseConfig.ResolveForFamily(
                s_FamilyConfig, planet.ShipFamilyConfigIndex);
            int slotCount = buffer.Length;

            for (int i = 0; i < slotCount; i++)
            {
                var slot = buffer[i];
                // Empty / destroyed placeholders have no gun to collide with.
                if (slot.TurretLevel == 0 || slot.Health <= 0f)
                    continue;

                // HitRpc store can already be 0 while ghost Health is still spawn HP —
                // skip so tracers do not keep colliding with a dead gun.
                if (PlanetaryDefenseClientHealthSync.TryGetHealth(planet.PlanetId, i, out float overlayHp) &&
                    overlayHp <= 0.01f)
                    continue;

                float3 slotPos = PlanetaryDefenseMath.GetSlotWorldPosition(
                    planetPos, planetScale, planet.PlanetLevel, i, slotCount);
                slotPos.y = PlanetaryDefenseMath.FixedY;

                Obstacles.Add(new Obstacle
                {
                    Kind = ObstacleKind.PlanetaryDefense,
                    SourceEntity = planetEntity,
                    LogicalCenter = slotPos,
                    Radius = PlanetaryDefenseHitScan.ComputeTurretHitRadius(config, slot.TurretLevel),
                    Scale = planetScale,
                    TeamOrOwnership = (byte)planet.Ownership,
                    PlanetId = planet.PlanetId,
                    SlotIndex = i,
                });
            }
        }

        /// <summary>
        /// Loads the family catalog once so per-planet turret recipes match the server.
        /// Safe to call every refresh — no-ops after the first attempt.
        /// </summary>
        static void EnsureDefenseConfigWarmed()
        {
            if (s_DefenseConfigWarmed)
                return;

            // [UNITY] Resources.Load is a main-thread asset lookup; this runs from LateUpdate.
            s_FamilyConfig = Resources.Load<PlanetShipFamilyConfig>("PlanetShipFamilyConfig");
            // LoadDefault also builds an in-memory fallback if the asset is missing.
            PlanetaryDefenseConfig.LoadDefault();
            s_DefenseConfigWarmed = true;
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
            ShipProxyScratch.Clear();
            DroneScratch.Clear();
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
                // [TITAN-ORBIT] Exception: HitRpc kill (healthAfter≈0) must still match a just-culled
                // seed-hydrated rock so presentation can DestroyEntity / tear down the GO.
                bool killHit = hasAuthHp && asteroidHealthAfter <= 0.01f;
                if (!visualizer.TryGetProxy(entity, out var proxyGo) || proxyGo == null)
                    continue;
                if (!proxyGo.activeSelf && !killHit)
                    continue;

                var state = em.GetComponentData<AsteroidState>(entity);
                if (!killHit && (state.IsDestroyed || state.Health <= 0f))
                    continue;
                if (!killHit && em.HasComponent<AsteroidClientCulledTag>(entity))
                    continue;

                // Ghost already below server post-hit HP → cannot be the rock that was just hit
                // (that rock's ghost is equal or still higher due to lag). Rejects wrong neighbors
                // that had lower HP than the authoritative remaining (log line 78: rem 39 vs ghost 25).
                // Kill hits skip this — seed-hydrate already wrote Health=0 before presentation.
                if (hasAuthHp && !killHit && state.Health + 0.5f < asteroidHealthAfter)
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
