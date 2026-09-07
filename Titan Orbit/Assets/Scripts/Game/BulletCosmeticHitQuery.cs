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
        /// <summary>One sphere or MEGA hull box the cosmetic tracer may collide with.</summary>
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

            /// <summary>
            /// MEGA XZ half-extents. Zero means sphere-only (regular ships).
            /// </summary>
            public float2 BoxHalfExtents;

            /// <summary>MEGA hull yaw around Y when <see cref="HasOrientedBox"/> is true.</summary>
            public float BoxYawRadians;

            /// <summary>
            /// MEGA 3D AABB center height. Tracers/impacts lift to this so they do not
            /// slide through the tall mesh on the Y=0 play plane.
            /// </summary>
            public float HullMidY;

            /// <summary>True when this ship should use a yaw-aligned box instead of a covering sphere.</summary>
            public bool HasOrientedBox => BoxHalfExtents.x > 0.01f && BoxHalfExtents.y > 0.01f;
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

        /// <summary>
        /// Toroidal XZ grid of asteroids / ships / transports / drones / PD pads.
        /// Planets and moons stay on a linear scan (few, and moons orbit).
        /// </summary>
        const float GridCellSize = 16f;
        static readonly Dictionary<int, List<int>> s_Grid = new Dictionary<int, List<int>>(256);
        static readonly List<int> s_GridScratch = new List<int>(64);
        static readonly HashSet<int> s_GridSeen = new HashSet<int>();
        static readonly List<int> s_TestIndices = new List<int>(128);
        static readonly List<MegaShipCombatAim.MegaPartSweepShape> s_MegaPartScratch =
            new List<MegaShipCombatAim.MegaPartSweepShape>(64);
        static NativeList<CosmeticSweepBody> s_SweepBodies;
        static NativeList<int> s_SweepAlways;
        static NativeParallelMultiHashMap<int, int> s_SweepCells;
        static NativeList<int> s_SweepNearby;
        static NativeHashSet<int> s_SweepSeen;
        static int s_SweepCellsX = 1;
        static int s_SweepCellsZ = 1;
        static bool s_GridHasEntries;
        static int s_GridCellsX = 1;
        static int s_GridCellsZ = 1;

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

                    float asteroidRadius = visualizer.TryGetProxy(entity, out GameObject asteroidGo) &&
                                           asteroidGo != null
                        ? BulletImpactAttach.GetAsteroidVisualRadiusWorld(asteroidGo.transform)
                        : BodyCollisionMath.GetAsteroidBodyRadiusWorld(lt.Scale)
                          + BodyCollisionMath.AsteroidVisualDisplacementLocal * math.max(0.1f, lt.Scale);
                    Obstacles.Add(new Obstacle
                    {
                        Kind = ObstacleKind.Asteroid,
                        SourceEntity = entity,
                        LogicalCenter = lt.Position,
                        Radius = asteroidRadius,
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

                    // [TITAN-ORBIT] Match server BulletSimulationSystem — MEGA uses the
                    // collider box; regular ships keep the attribute-grown sphere.
                    float shipRadius;
                    float2 boxHe = float2.zero;
                    float boxYaw = 0f;
                    float hullMidY = lt.Position.y;
                    float3 shipCenter = MegaShipCombatAim.GetAimPoint(em, entity, lt);
                    if (MegaShipCombatAim.TryGetHitBoxWorld(
                            em, entity, lt, out shipCenter, out boxHe, out boxYaw, out hullMidY))
                    {
                        shipRadius = math.length(boxHe);
                    }
                    else if (em.HasComponent<PhysicsCollider>(entity))
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
                        LogicalCenter = shipCenter,
                        Radius = shipRadius,
                        Scale = lt.Scale,
                        TeamOrOwnership = (byte)ship.Team,
                        OwnerNetworkId = networkId,
                        BoxHalfExtents = boxHe,
                        BoxYawRadians = boxYaw,
                        HullMidY = hullMidY,
                    });
                    ShipProxyScratch.Add(entity);
                }
            }

            PeopleTransportVfxDriver.AppendBulletObstacles(Obstacles);
            AppendDroneObstacles(em);
            RebuildObstacleGrid();
            RebuildSweepBodies(em);

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
        /// Inserts non-orbiting obstacles into a toroidal XZ grid.
        /// Map size from the last <see cref="TryRefresh"/> (<c>s_MapW</c> / <c>s_MapH</c>).
        /// </summary>
        static void RebuildObstacleGrid()
        {
            foreach (var kv in s_Grid)
                kv.Value.Clear();
            s_GridHasEntries = false;
            s_GridCellsX = 1;
            s_GridCellsZ = 1;
            if (!ToroidalMapEcs.IsValidMapSize(s_MapW, s_MapH))
                return;

            s_GridCellsX = math.max(1, (int)math.ceil(s_MapW / GridCellSize));
            s_GridCellsZ = math.max(1, (int)math.ceil(s_MapH / GridCellSize));

            for (int i = 0; i < Obstacles.Count; i++)
            {
                var o = Obstacles[i];
                if (o.Kind == ObstacleKind.Planet || o.Kind == ObstacleKind.Moon)
                    continue;

                AddCoveringCells(i, o.LogicalCenter, o.Radius);
                s_GridHasEntries = true;
            }
        }

        /// <summary>
        /// Copies hybrid spheres into a Burst-friendly list for mega volleys.
        /// </summary>
        static void EnsureSweepScratch()
        {
            if (!s_SweepBodies.IsCreated)
                s_SweepBodies = new NativeList<CosmeticSweepBody>(512, Allocator.Persistent);
            if (!s_SweepAlways.IsCreated)
                s_SweepAlways = new NativeList<int>(16, Allocator.Persistent);
            if (!s_SweepCells.IsCreated)
                s_SweepCells = new NativeParallelMultiHashMap<int, int>(512, Allocator.Persistent);
            if (!s_SweepNearby.IsCreated)
                s_SweepNearby = new NativeList<int>(64, Allocator.Persistent);
            if (!s_SweepSeen.IsCreated)
                s_SweepSeen = new NativeHashSet<int>(64, Allocator.Persistent);
        }

        static void DisposeSweepScratch()
        {
            if (s_SweepBodies.IsCreated)
                s_SweepBodies.Dispose();
            if (s_SweepAlways.IsCreated)
                s_SweepAlways.Dispose();
            if (s_SweepCells.IsCreated)
                s_SweepCells.Dispose();
            if (s_SweepNearby.IsCreated)
                s_SweepNearby.Dispose();
            if (s_SweepSeen.IsCreated)
                s_SweepSeen.Dispose();
            s_SweepBodies = default;
            s_SweepAlways = default;
            s_SweepCells = default;
            s_SweepNearby = default;
            s_SweepSeen = default;
        }

        static void RebuildSweepBodies(EntityManager em)
        {
            EnsureSweepScratch();
            s_SweepBodies.Clear();
            s_SweepAlways.Clear();
            s_SweepCells.Clear();
            s_SweepCellsX = 1;
            s_SweepCellsZ = 1;
            if (ToroidalMapEcs.IsValidMapSize(s_MapW, s_MapH))
            {
                s_SweepCellsX = math.max(1, (int)math.ceil(s_MapW / BulletCosmeticSweepJob.CellSize));
                s_SweepCellsZ = math.max(1, (int)math.ceil(s_MapH / BulletCosmeticSweepJob.CellSize));
            }

            for (int i = 0; i < Obstacles.Count; i++)
            {
                var o = Obstacles[i];
                if (o.Kind == ObstacleKind.Ship &&
                    o.HasOrientedBox &&
                    TryAddMegaPartSweepBodies(em, in o))
                    continue;

                bool home = o.IsHomePlanet;
                int bodyIndex = s_SweepBodies.Length;
                s_SweepBodies.Add(new CosmeticSweepBody
                {
                    Position = o.LogicalCenter,
                    Radius = o.Radius,
                    BoxHalfExtents = o.Kind == ObstacleKind.Ship ? o.BoxHalfExtents : float2.zero,
                    BoxYawRadians = o.Kind == ObstacleKind.Ship ? o.BoxYawRadians : 0f,
                    Scale = o.Scale,
                    MoonBodyRadius = PlanetGemMoonMath.GetMoonBodyRadiusWorld(o.Scale, home),
                    MoonShieldRadius = PlanetGemMoonMath.GetMoonBulletHitRadiusWorld(
                        o.Scale, home, o.CurrentShield, attackerFriendlyToMoon: false),
                    CurrentShield = o.CurrentShield,
                    PlanetLevel = o.PlanetLevel,
                    PlanetId = o.PlanetId,
                    OwnerNetworkId = o.OwnerNetworkId,
                    SlotIndex = o.SlotIndex,
                    Kind = (byte)o.Kind,
                    Team = o.TeamOrOwnership,
                    IsHome = home ? (byte)1 : (byte)0,
                });

                if (o.Kind == ObstacleKind.Planet || o.Kind == ObstacleKind.Moon)
                {
                    s_SweepAlways.Add(bodyIndex);
                    continue;
                }

                AddSweepCoveringCells(bodyIndex, o.LogicalCenter, o.Radius);
            }
        }

        /// <summary>
        /// Burst cannot walk a PhysicsCollider, so each MEGA part is copied as its
        /// own box/sphere. The covering hull sphere is what parked PD tracers in
        /// empty space before they reached the mesh.
        /// </summary>
        static bool TryAddMegaPartSweepBodies(EntityManager em, in Obstacle o)
        {
            if (!em.Exists(o.SourceEntity) || !em.HasComponent<LocalTransform>(o.SourceEntity))
                return false;

            s_MegaPartScratch.Clear();
            var xf = em.GetComponentData<LocalTransform>(o.SourceEntity);
            if (!MegaShipCombatAim.TryAppendPartSweepShapes(em, o.SourceEntity, xf, s_MegaPartScratch))
                return false;

            for (int p = 0; p < s_MegaPartScratch.Count; p++)
            {
                var part = s_MegaPartScratch[p];
                bool sphere = part.SphereRadius > 0.001f;
                float cover = sphere
                    ? part.SphereRadius
                    : math.length(part.BoxHalfExtents);
                int bodyIndex = s_SweepBodies.Length;
                s_SweepBodies.Add(new CosmeticSweepBody
                {
                    Position = part.WorldCenter,
                    Radius = cover,
                    BoxHalfExtents = sphere ? float2.zero : part.BoxHalfExtents,
                    BoxYawRadians = part.BoxYawRadians,
                    Scale = o.Scale,
                    OwnerNetworkId = o.OwnerNetworkId,
                    Kind = (byte)ObstacleKind.Ship,
                    Team = o.TeamOrOwnership,
                });
                AddSweepCoveringCells(bodyIndex, part.WorldCenter, cover);
            }

            return true;
        }

        static void AddSweepCoveringCells(int index, float3 pos, float radius)
        {
            int cellR = (int)math.ceil((radius + 0.85f) / BulletCosmeticSweepJob.CellSize);
            float3 wrapped = ToroidalMapEcs.Wrap(pos, s_MapW, s_MapH);
            float u = wrapped.x + s_MapW * 0.5f;
            float v = wrapped.z + s_MapH * 0.5f;
            int baseX = math.clamp((int)math.floor(u / BulletCosmeticSweepJob.CellSize), 0, s_SweepCellsX - 1);
            int baseZ = math.clamp((int)math.floor(v / BulletCosmeticSweepJob.CellSize), 0, s_SweepCellsZ - 1);
            if (cellR <= 0)
            {
                s_SweepCells.Add(baseX + baseZ * s_SweepCellsX, index);
                return;
            }

            for (int dz = -cellR; dz <= cellR; dz++)
            {
                int cz = WrapGridCell(baseZ + dz, s_SweepCellsZ);
                for (int dx = -cellR; dx <= cellR; dx++)
                    s_SweepCells.Add(WrapGridCell(baseX + dx, s_SweepCellsX) + cz * s_SweepCellsX, index);
            }
        }

        /// <summary>
        /// Burst-advances every straight tracer in one job. Homing rockets stay managed.
        /// </summary>
        public static bool TryAdvanceStraightTracers(
            NativeArray<CosmeticSweepRequest> requests,
            NativeArray<CosmeticSweepResult> results)
        {
            if (!s_SweepBodies.IsCreated || s_SweepBodies.Length == 0 || requests.Length == 0)
                return false;
            if (!ToroidalMapEcs.IsValidMapSize(s_MapW, s_MapH))
                return false;
            if (requests.Length != results.Length)
                return false;

            double moonElapsed = PlanetGemMoonOrbitClock.TryGetElapsedSeconds(
                out double elapsed, includeTickFraction: true)
                ? elapsed
                : Time.timeAsDouble;
            int n = requests.Length;
            int maxSubsteps = n >= 80 ? 2 : n >= 32 ? 4 : 8;
            BulletCosmeticSweepJob.Run(
                requests,
                results,
                s_SweepBodies.AsArray(),
                s_SweepAlways.AsArray(),
                s_SweepCells,
                s_SweepNearby,
                s_SweepSeen,
                s_MapW,
                s_MapH,
                moonElapsed,
                maxSubsteps,
                s_SweepCellsX,
                s_SweepCellsZ);
            return true;
        }

        /// <summary>
        /// Stamp a body into every cell its radius overlaps so queries stay
        /// step-sized (a MEGA covering sphere must not inflate every tracer).
        /// </summary>
        static void AddCoveringCells(int index, float3 pos, float radius)
        {
            int cellR = (int)math.ceil((radius + 0.85f) / GridCellSize);
            int baseX = GridCellX(pos.x);
            int baseZ = GridCellZ(pos.z);
            if (cellR <= 0)
            {
                AddGridIndex(baseX + baseZ * s_GridCellsX, index);
                return;
            }

            for (int dz = -cellR; dz <= cellR; dz++)
            {
                int cz = WrapGridCell(baseZ + dz, s_GridCellsZ);
                for (int dx = -cellR; dx <= cellR; dx++)
                    AddGridIndex(WrapGridCell(baseX + dx, s_GridCellsX) + cz * s_GridCellsX, index);
            }
        }

        static void AddGridIndex(int key, int index)
        {
            if (!s_Grid.TryGetValue(key, out var list))
            {
                list = new List<int>(8);
                s_Grid[key] = list;
            }

            list.Add(index);
        }

        /// <summary>
        /// Planets + moons always, plus nearby hashed bodies for this segment.
        /// Falls back to a full scan when the grid is empty.
        /// </summary>
        static void CollectHitCandidates(float3 from, float3 to)
        {
            s_TestIndices.Clear();
            bool haveGrid = s_GridHasEntries &&
                            ToroidalMapEcs.IsValidMapSize(s_MapW, s_MapH);

            for (int i = 0; i < Obstacles.Count; i++)
            {
                var k = Obstacles[i].Kind;
                if (k == ObstacleKind.Planet || k == ObstacleKind.Moon)
                    s_TestIndices.Add(i);
                else if (!haveGrid)
                    s_TestIndices.Add(i);
            }

            if (!haveGrid)
                return;

            s_GridScratch.Clear();
            s_GridSeen.Clear();
            float radius = math.distance(from, to) + 1.85f;
            int cellRadius = (int)math.ceil(radius / GridCellSize) + 1;
            int baseX = GridCellX(from.x);
            int baseZ = GridCellZ(from.z);

            for (int dz = -cellRadius; dz <= cellRadius; dz++)
            {
                int cz = WrapGridCell(baseZ + dz, s_GridCellsZ);
                for (int dx = -cellRadius; dx <= cellRadius; dx++)
                {
                    int cx = WrapGridCell(baseX + dx, s_GridCellsX);
                    int key = cx + cz * s_GridCellsX;
                    if (!s_Grid.TryGetValue(key, out var list))
                        continue;

                    for (int i = 0; i < list.Count; i++)
                    {
                        int idx = list[i];
                        if (!s_GridSeen.Add(idx))
                            continue;
                        s_GridScratch.Add(idx);
                    }
                }
            }

            for (int i = 0; i < s_GridScratch.Count; i++)
                s_TestIndices.Add(s_GridScratch[i]);
        }

        static int GridCellX(float x)
        {
            float3 wrapped = ToroidalMapEcs.Wrap(new float3(x, 0f, 0f), s_MapW, s_MapH);
            float u = wrapped.x + s_MapW * 0.5f;
            int c = (int)math.floor(u / GridCellSize);
            return math.clamp(c, 0, s_GridCellsX - 1);
        }

        static int GridCellZ(float z)
        {
            float3 wrapped = ToroidalMapEcs.Wrap(new float3(0f, 0f, z), s_MapW, s_MapH);
            float v = wrapped.z + s_MapH * 0.5f;
            int c = (int)math.floor(v / GridCellSize);
            return math.clamp(c, 0, s_GridCellsZ - 1);
        }

        static int WrapGridCell(int c, int count)
        {
            if (count <= 0)
                return 0;
            int m = c % count;
            return m < 0 ? m + count : m;
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
            int bankIndex = 0,
            bool allowSelfHarm = false)
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

            CollectHitCandidates(from, to);
            for (int n = 0; n < s_TestIndices.Count; n++)
            {
                int i = s_TestIndices[n];
                var o = Obstacles[i];
                if (!PassesTeamFilter(in o, ownerTeam, ownerNetworkId, healFriendly, allowSelfHarm))
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

                    if (o.Kind == ObstacleKind.Ship && o.HasOrientedBox)
                    {
                        float pad = math.clamp(scaleMultiplier * 0.18f, 0f, 0.85f);
                        hit = false;
                        hp = to;
                        var world = EcsGameBridge.ClientWorld ?? EcsGameBridge.ServerWorld;
                        if (world != null && world.IsCreated)
                        {
                            var hitEm = world.EntityManager;
                            if (hitEm.Exists(o.SourceEntity) &&
                                hitEm.HasComponent<LocalTransform>(o.SourceEntity))
                            {
                                var shipXf = hitEm.GetComponentData<LocalTransform>(o.SourceEntity);
                                hit = MegaShipCombatAim.TryHitBulletSegment(
                                    hitEm, o.SourceEntity, shipXf,
                                    from, to, pad, s_MapW, s_MapH, out hp, out _);
                            }
                        }
                    }
                    else
                    {
                        hit = BulletCollision.SegmentHitsSphereToroidal(
                            from, to, o.LogicalCenter, radius, s_MapW, s_MapH, out hp);
                    }
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
        /// Nearest cached obstacle to a logical point (surface-fit). Used to parent
        /// Sequence-0 burn / ram flashes to the body this observer sees.
        /// </summary>
        public static bool TryFindNearestObstacle(float3 logicalHit, out Obstacle obstacle)
        {
            obstacle = default;
            if (Obstacles.Count == 0)
                return false;

            float3 hit = logicalHit;
            hit.y = 0f;
            float best = float.MaxValue;
            int bestIndex = -1;
            for (int i = 0; i < Obstacles.Count; i++)
            {
                var o = Obstacles[i];
                float3 center = o.LogicalCenter;
                center.y = 0f;
                float dist;
                float radius = math.max(0.05f, o.Radius);
                float score;
                if (o.Kind == ObstacleKind.Ship && o.HasOrientedBox)
                {
                    float3 boxCenter = o.LogicalCenter;
                    if (ToroidalMapEcs.IsValidMapSize(s_MapW, s_MapH))
                        boxCenter = BulletCollision.UnwrapCenterNear(hit, o.LogicalCenter, s_MapW, s_MapH);
                    dist = BulletCollision.DistanceToOrientedBoxXZ(
                        hit, boxCenter, o.BoxHalfExtents, o.BoxYawRadians);
                    score = dist;
                    if (dist > math.max(2f, math.length(o.BoxHalfExtents) * 0.35f))
                        continue;
                }
                else
                {
                    dist = ToroidalMapEcs.IsValidMapSize(s_MapW, s_MapH)
                        ? ToroidalMapEcs.ToroidalDistance(hit, center, s_MapW, s_MapH)
                        : math.distance(hit, center);
                    if (o.Kind == ObstacleKind.Moon)
                    {
                        radius = math.max(
                            radius,
                            PlanetGemMoonMath.GetMoonBulletHitRadiusWorld(
                                o.Scale, o.IsHomePlanet, o.CurrentShield));
                    }

                    score = math.abs(dist - radius);
                    if (dist > radius + math.max(2f, radius * 0.35f))
                        continue;
                }
                if (score < best)
                {
                    best = score;
                    bestIndex = i;
                }
            }

            if (bestIndex < 0)
                return false;
            obstacle = Obstacles[bestIndex];
            return true;
        }

        /// <summary>
        /// Lifts a play-plane tracer toward a nearby MEGA hull midline so the slug
        /// meets the 3D mesh instead of sliding through it at Y=0.
        /// </summary>
        public static bool TryGetMegaFlightLiftY(float3 logicalPos, out float hullMidY, out float blend)
        {
            hullMidY = 0f;
            blend = 0f;
            float best = float.MaxValue;
            for (int i = 0; i < Obstacles.Count; i++)
            {
                var o = Obstacles[i];
                if (o.Kind != ObstacleKind.Ship || !o.HasOrientedBox)
                    continue;

                float3 boxCenter = o.LogicalCenter;
                if (ToroidalMapEcs.IsValidMapSize(s_MapW, s_MapH))
                    boxCenter = BulletCollision.UnwrapCenterNear(logicalPos, o.LogicalCenter, s_MapW, s_MapH);
                float dist = BulletCollision.DistanceToOrientedBoxXZ(
                    logicalPos, boxCenter, o.BoxHalfExtents, o.BoxYawRadians);
                if (dist >= best)
                    continue;

                best = dist;
                hullMidY = o.HullMidY;
                float fade = math.max(3f, math.cmax(o.BoxHalfExtents) * 0.45f);
                blend = 1f - math.saturate(dist / fade);
            }

            return blend > 0.01f;
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
        /// ships and planetary-defense turrets skip friendlies / self unless
        /// <paramref name="allowSelfHarm"/> (debug homing rockets after the arm delay).
        /// </summary>
        static bool PassesTeamFilter(
            in Obstacle o, byte ownerTeam, int ownerNetworkId, bool healFriendly, bool allowSelfHarm)
        {
            if (o.Kind == ObstacleKind.Ship)
            {
                if (allowSelfHarm)
                    return true;
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
            foreach (var kv in s_Grid)
                kv.Value.Clear();
            s_GridScratch.Clear();
            s_GridSeen.Clear();
            s_TestIndices.Clear();
            s_GridHasEntries = false;
            s_LastRefreshFrame = -1;
            DisposeSweepScratch();
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
        /// Finds the ship hull that best matches a server impact point (surface fit).
        /// Same residual score as <see cref="TryFindAsteroidAtImpact"/> so a shot that
        /// grazes one hull does not attribute damage to a neighbor.
        /// Quarantine-safe — hybrid proxy keys only.
        /// </summary>
        /// <param name="hitDisplayPos">Server hit position converted to display space.</param>
        /// <param name="shipEntity">Best surface-fit live ship, or Null.</param>
        /// <returns>True when a live hull contains the impact.</returns>
        public static bool TryFindShipAtImpact(Vector3 hitDisplayPos, out Entity shipEntity)
        {
            shipEntity = Entity.Null;
            var visualizer = EcsWorldVisualizer.Active;
            if (visualizer == null)
                return false;

            var world = EcsGameBridge.ClientWorld;
            if (world == null || !world.IsCreated)
                return false;

            var em = world.EntityManager;
            visualizer.CopyLiveProxyEntities(ProxyScratch);

            bool hasRef = ToroidalDisplay.TryGetReferencePosition(out Vector3 reference);
            float bestSurfaceError = float.MaxValue;
            Entity best = Entity.Null;
            float3 hit = new float3(hitDisplayPos.x, 0f, hitDisplayPos.z);

            for (int i = 0; i < ProxyScratch.Count; i++)
            {
                Entity entity = ProxyScratch[i];
                if (!em.Exists(entity) ||
                    !em.HasComponent<ShipTag>(entity) ||
                    !em.HasComponent<ShipState>(entity) ||
                    !em.HasComponent<LocalTransform>(entity))
                    continue;

                var state = em.GetComponentData<ShipState>(entity);
                if (state.IsDead)
                    continue;
                if (!visualizer.TryGetProxy(entity, out var proxyGo) ||
                    proxyGo == null ||
                    !proxyGo.activeSelf)
                    continue;

                var lt = em.GetComponentData<LocalTransform>(entity);
                float3 shipCenter = MegaShipCombatAim.GetAimPoint(em, entity, lt);
                float shipRadius;
                if (MegaShipCombatAim.TryGetHitBoxWorld(
                        em, entity, lt, out shipCenter, out float2 boxHe, out _, out _))
                {
                    shipRadius = math.length(boxHe);
                }
                else if (em.HasComponent<PhysicsCollider>(entity))
                {
                    var physicsCollider = em.GetComponentData<PhysicsCollider>(entity);
                    shipRadius = MegaShipCombatAim.GetHitRadiusWorld(
                        em, entity, physicsCollider, lt.Scale);
                }
                else
                {
                    shipRadius = BodyCollisionMath.GetShipHullRadiusWorld(lt.Scale);
                }

                float3 display = hasRef
                    ? (float3)ToroidalDisplay.ToDisplayPosition(shipCenter, reference)
                    : shipCenter;
                display.y = 0f;

                float maxDist = shipRadius + 0.35f;
                float dist = math.distance(display, hit);
                if (dist > maxDist)
                    continue;

                float surfaceError = math.abs(dist - shipRadius);
                if (surfaceError >= bestSurfaceError)
                    continue;

                bestSurfaceError = surfaceError;
                best = entity;
            }

            if (best == Entity.Null)
                return false;

            shipEntity = best;
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
