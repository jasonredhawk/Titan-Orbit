using System.Collections.Generic;
using TitanOrbit.ECS;
using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Client presentation for people-transport float VFX (load planet→ship and unload ship→planet).
    /// <para>
    /// Server <see cref="PeopleTransportSimulationSystem"/> still owns delivery and population counts.
    /// This system never writes ghost <see cref="LocalTransform"/> — it only advances a cosmetic pose
    /// into <see cref="GhostPresentationTransformCache"/> for <see cref="EcsWorldVisualizer"/>.
    /// </para>
    /// <para>
    /// Catch-up: when the ghost arrives late on the client, we fast-forward magnet steps using
    /// <see cref="PeopleTransportState.SpawnTime"/> so the sphere is already mid-flight instead of
    /// restarting from the planet and only becoming obvious near delivery. Magnet targets prefer
    /// ship presentation poses (not raw sim LT) so the float does not chase a jittery hull.
    /// </para>
    /// World: ClientSimulation. Group: PresentationSystemGroup, after <see cref="ShipVisualSyncSystem"/>.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(PresentationSystemGroup), OrderLast = true)]
    [UpdateAfter(typeof(ShipVisualSyncSystem))]
    public partial class PeopleTransportVisualSyncSystem : SystemBase
    {
        /// <summary>Fixed step used when catching up a late-spawned ghost to server flight progress.</summary>
        const float CatchUpStepSeconds = 1f / 60f;

        /// <summary>Hard cap on catch-up iterations (avoids hitch if SpawnTime is stale).</summary>
        const int MaxCatchUpSteps = 90;

        /// <summary>
        /// Cosmetic lift above the XZ plane so spheres clear planet/ship hulls (presentation only).
        /// </summary>
        const float PresentationLiftY = 0.45f;

        /// <summary>Extra outward nudge from planet surface for the first visual seed (presentation only).</summary>
        const float PresentationSpawnOutwardBoost = 0.85f;

        /// <summary>Per-entity cosmetic flight state (not replicated, not sim).</summary>
        struct FlightVisual
        {
            public float3 Position;
            public float3 Velocity;
            public float Scale;
        }

        /// <summary>Active presentation flights keyed by transport entity.</summary>
        readonly Dictionary<Entity, FlightVisual> _flights = new Dictionary<Entity, FlightVisual>(32);

        /// <summary>Scratch set of entities seen this frame — used to prune destroyed transports.</summary>
        readonly HashSet<Entity> _seen = new HashSet<Entity>(32);

        /// <summary>Reusable prune list — avoids per-frame List alloc when flights despawn.</summary>
        readonly List<Entity> _prune = new List<Entity>(16);

        /// <summary>Reused ship NetworkId → presentation/sim transform (cleared each frame with transports).</summary>
        readonly Dictionary<int, LocalTransform> _shipByNetworkId = new Dictionary<int, LocalTransform>(16);

        /// <summary>Reused planet id → transform.</summary>
        readonly Dictionary<int, LocalTransform> _planetById = new Dictionary<int, LocalTransform>(64);

        EntityQuery _transportQuery;

        /// <summary>Caches the transport query so we can early-out when none exist.</summary>
        protected override void OnCreate()
        {
            _transportQuery = GetEntityQuery(
                ComponentType.ReadOnly<PeopleTransportTag>(),
                ComponentType.ReadOnly<PeopleTransportState>(),
                ComponentType.ReadOnly<LocalTransform>());
        }

        /// <summary>
        /// Each presentation frame: magnet-steer cosmetic poses for every people-transport ghost
        /// and publish them for hybrid proxies. No-ops when no transports exist (avoids planet/ship scans).
        /// </summary>
        protected override void OnUpdate()
        {
            // --- Hot path: skip all work when nothing is in flight ---
            if (_transportQuery.IsEmptyIgnoreFilter)
            {
                if (_flights.Count > 0)
                    _flights.Clear();
                return;
            }

            // [UNITY] Presentation must use frame delta — PredictedFixedStep dt is wrong here.
            float dt = math.min(0.05f, math.max(0f, UnityEngine.Time.deltaTime));
            if (dt <= 0f)
                return;

            GetMapSize(out float mapW, out float mapH);
            float now = (float)SystemAPI.Time.ElapsedTime;

            BuildTargetLookups();

            _seen.Clear();

            foreach (var (state, transform, entity) in SystemAPI
                         .Query<RefRO<PeopleTransportState>, RefRO<LocalTransform>>()
                         .WithAll<PeopleTransportTag>()
                         .WithEntityAccess())
            {
                _seen.Add(entity);
                var t = state.ValueRO;
                var ghostLt = transform.ValueRO;
                bool isLoad = t.IsLoad != 0;

                // --- Seed or resume cosmetic flight ---
                bool isNew = !_flights.TryGetValue(entity, out var flight);
                if (isNew)
                {
                    flight = SeedFlight(in t, in ghostLt, isLoad, mapW, mapH);
                    CatchUpFlight(ref flight, in t, isLoad, now, mapW, mapH);
                }

                if (!TryResolvePresentationTarget(
                        in t, isLoad, flight.Position, _shipByNetworkId, _planetById, mapW, mapH,
                        out float3 target))
                {
                    PublishFlight(entity, in flight);
                    _flights[entity] = flight;
                    continue;
                }

                // --- One presentation frame of magnet ---
                StepMagnet(ref flight, target, in t, isLoad, dt, mapW, mapH);
                PublishFlight(entity, in flight);
                _flights[entity] = flight;
            }

            PruneMissingFlights();
        }

        /// <summary>
        /// Fills ship/planet lookups. Ships prefer <see cref="GhostPresentationTransformCache"/> poses
        /// so the magnet tracks the same hull the player sees (reduces float jitter / “lag”).
        /// </summary>
        void BuildTargetLookups()
        {
            _shipByNetworkId.Clear();
            foreach (var (owner, transform, entity) in SystemAPI
                         .Query<RefRO<GhostOwner>, RefRO<LocalTransform>>()
                         .WithAll<ShipTag>()
                         .WithEntityAccess())
            {
                int id = owner.ValueRO.NetworkId;
                if (id == 0)
                    continue;

                // Prefer presentation hull when ShipVisualSync already published this frame.
                if (GhostPresentationTransformCache.TryGetShip(entity, out var snap))
                {
                    _shipByNetworkId[id] = LocalTransform.FromPositionRotationScale(
                        snap.Position, snap.Rotation, snap.Scale > 0.001f ? snap.Scale : transform.ValueRO.Scale);
                }
                else
                {
                    _shipByNetworkId[id] = transform.ValueRO;
                }
            }

            _planetById.Clear();
            foreach (var (planet, transform) in SystemAPI
                         .Query<RefRO<PlanetState>, RefRO<LocalTransform>>()
                         .WithAll<PlanetTag>())
            {
                int id = planet.ValueRO.PlanetId;
                if (id != 0)
                    _planetById[id] = transform.ValueRO;
            }
        }

        /// <summary>
        /// First visual sample: spawn from ghosted SpawnPosition, nudged outward so it clears the planet mesh.
        /// </summary>
        FlightVisual SeedFlight(in PeopleTransportState t, in LocalTransform ghostLt, bool isLoad, float mapW, float mapH)
        {
            float3 seed = math.lengthsq(t.SpawnPosition) > 0.0001f ? t.SpawnPosition : ghostLt.Position;
            seed.y = 0f;

            // Presentation-only: push load spawns farther off the planet so they are not buried in the mesh.
            if (isLoad &&
                t.SourcePlanetId != 0 &&
                _planetById.TryGetValue(t.SourcePlanetId, out var planetXform))
            {
                float3 outward = ToroidalMapEcs.ToroidalDirection(planetXform.Position, seed, mapW, mapH);
                seed += outward * PresentationSpawnOutwardBoost;
                seed.y = 0f;
            }

            float3 vel = t.Velocity;
            vel.y = 0f;
            // If velocity has not replicated yet, kick along spawn→rough target so catch-up is not stuck.
            if (math.lengthsq(vel) < 0.0001f &&
                TryResolvePresentationTarget(in t, isLoad, seed, _shipByNetworkId, _planetById, mapW, mapH, out float3 kickTarget))
            {
                float cruise = t.CruiseSpeed > 0.01f
                    ? t.CruiseSpeed
                    : PeopleTransportMath.ComputeCruiseSpeed(seed, kickTarget, isLoad, mapW, mapH);
                vel = ToroidalMapEcs.ToroidalDirection(seed, kickTarget, mapW, mapH) * cruise;
            }

            return new FlightVisual
            {
                Position = seed,
                Velocity = vel,
                Scale = ghostLt.Scale > 0.001f ? ghostLt.Scale : 0.25f,
            };
        }

        /// <summary>
        /// Fast-forwards a newly seen transport to match server flight age so late ghost spawn
        /// does not replay the whole trip from the planet (which looked like “appear at the end”).
        /// </summary>
        void CatchUpFlight(ref FlightVisual flight, in PeopleTransportState t, bool isLoad, float now, float mapW, float mapH)
        {
            float elapsed = now - t.SpawnTime;
            if (elapsed <= CatchUpStepSeconds || t.SpawnTime <= 0f)
                return;

            // Cap to one visual trip — beyond that the sphere should already be near delivery/despawn.
            float remaining = math.min(elapsed, PeopleTransportMath.EffectiveVisualTravelSeconds + 0.5f);
            int steps = 0;
            while (remaining > 0.0001f && steps < MaxCatchUpSteps)
            {
                float stepDt = math.min(CatchUpStepSeconds, remaining);
                if (!TryResolvePresentationTarget(
                        in t, isLoad, flight.Position, _shipByNetworkId, _planetById, mapW, mapH,
                        out float3 target))
                    break;

                StepMagnet(ref flight, target, in t, isLoad, stepDt, mapW, mapH);
                remaining -= stepDt;
                steps++;
            }
        }

        /// <summary>One magnet integration step using shared server math.</summary>
        static void StepMagnet(
            ref FlightVisual flight,
            float3 target,
            in PeopleTransportState t,
            bool isLoad,
            float dt,
            float mapW,
            float mapH)
        {
            float cruise = t.CruiseSpeed > 0.01f
                ? t.CruiseSpeed
                : PeopleTransportMath.ComputeCruiseSpeed(flight.Position, target, isLoad, mapW, mapH);
            flight.Velocity = PeopleTransportMath.SteerMagnetVelocity(
                flight.Position, target, flight.Velocity, dt, cruise, mapW, mapH);
            flight.Position += flight.Velocity * dt;
            flight.Position.y = 0f;
        }

        /// <summary>
        /// Picks load (ship magnet) or unload (planet surface) target using toroidal helpers.
        /// </summary>
        static bool TryResolvePresentationTarget(
            in PeopleTransportState t,
            bool isLoad,
            float3 myPos,
            Dictionary<int, LocalTransform> shipByNetworkId,
            Dictionary<int, LocalTransform> planetById,
            float mapW,
            float mapH,
            out float3 target)
        {
            target = float3.zero;

            if (isLoad)
            {
                if (t.TargetShipNetworkId != 0 &&
                    shipByNetworkId.TryGetValue(t.TargetShipNetworkId, out var shipXform))
                {
                    target = PeopleTransportMath.GetShipMagnetTarget(
                        shipXform.Position,
                        PeopleTransportMath.GetShipHullRadius(shipXform.Scale),
                        myPos,
                        mapW,
                        mapH);
                    return true;
                }

                if (t.SourcePlanetId != 0 &&
                    planetById.TryGetValue(t.SourcePlanetId, out var sourcePlanet))
                {
                    float planetSize = math.max(0.5f, sourcePlanet.Scale);
                    target = PeopleTransportMath.GetPlanetSurfaceToward(
                        sourcePlanet.Position, planetSize, myPos, mapW, mapH);
                    return true;
                }

                return false;
            }

            int planetId = t.TargetPlanetId != 0 ? t.TargetPlanetId : t.SourcePlanetId;
            if (planetId != 0 && planetById.TryGetValue(planetId, out var unloadPlanet))
            {
                float planetSize = math.max(0.5f, unloadPlanet.Scale);
                target = PeopleTransportMath.GetPlanetSurfaceToward(
                    unloadPlanet.Position, planetSize, myPos, mapW, mapH);
                return true;
            }

            return false;
        }

        /// <summary>Writes one transport snapshot into the hybrid presentation cache (with lift).</summary>
        static void PublishFlight(Entity entity, in FlightVisual flight)
        {
            float3 pos = flight.Position;
            pos.y = PresentationLiftY;
            GhostPresentationTransformCache.PublishPeopleTransport(
                entity,
                new GhostPresentationTransformCache.Snapshot
                {
                    Position = pos,
                    Rotation = quaternion.identity,
                    Scale = flight.Scale,
                });
        }

        /// <summary>Drops flight state for transports that despawned this frame.</summary>
        void PruneMissingFlights()
        {
            if (_flights.Count == _seen.Count)
                return;

            _prune.Clear();
            foreach (var key in _flights.Keys)
            {
                if (!_seen.Contains(key))
                    _prune.Add(key);
            }

            for (int i = 0; i < _prune.Count; i++)
            {
                _flights.Remove(_prune[i]);
                GhostPresentationTransformCache.ForgetPeopleTransport(_prune[i]);
            }
        }

        /// <summary>
        /// Reads toroidal map size from <see cref="MapStateSingleton"/>, else cached
        /// <see cref="ToroidalMapEcs"/> defaults.
        /// </summary>
        void GetMapSize(out float mapW, out float mapH)
        {
            mapW = ToroidalMapEcs.MapWidth;
            mapH = ToroidalMapEcs.MapHeight;
            if (SystemAPI.TryGetSingleton<MapStateSingleton>(out var map))
            {
                mapW = math.max(100f, map.MapWidth);
                mapH = math.max(100f, map.MapHeight);
            }
        }
    }
}
