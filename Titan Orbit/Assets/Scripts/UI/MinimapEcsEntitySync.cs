using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.ECS;
using TitanOrbit.Game;
using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;

namespace TitanOrbit.UI
{
    /// <summary>
    /// [HYBRID] Syncs ECS ghost entities into hidden MinimapBlipAnchor transforms for minimap UI.
    /// Rebuilds anchor cache periodically; updates positions every LateUpdate.
    /// World: visualization world via EcsGameBridge. Paired with MinimapController.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public sealed class MinimapEcsEntitySync : MonoBehaviour
    {
        /// <summary>Singleton for MinimapController and marker managers.</summary>
        public static MinimapEcsEntitySync Instance { get; private set; }

        /// <summary>Seconds between full entity→anchor rebuild (new ghosts, despawns).</summary>
        const float EntityCacheRefreshInterval = 6f;

        readonly Dictionary<Entity, MinimapBlipAnchor> _anchors = new Dictionary<Entity, MinimapBlipAnchor>();
        readonly Dictionary<int, MinimapBlipAnchor> _gemMoonsByPlanetId = new Dictionary<int, MinimapBlipAnchor>();
        readonly List<MinimapBlipAnchor> _ships = new List<MinimapBlipAnchor>();
        readonly List<MinimapBlipAnchor> _planets = new List<MinimapBlipAnchor>();
        readonly List<MinimapBlipAnchor> _homePlanets = new List<MinimapBlipAnchor>();
        readonly List<MinimapBlipAnchor> _asteroids = new List<MinimapBlipAnchor>();
        readonly List<MinimapBlipAnchor> _gemMoons = new List<MinimapBlipAnchor>();

        Transform _root;
        MinimapBlipAnchor _localPlayer;
        float _lastCacheRefreshTime = -999f;
        float _lastMapWidth = float.NaN;
        float _lastMapHeight = float.NaN;

        public IReadOnlyList<MinimapBlipAnchor> Ships => _ships;
        public IReadOnlyList<MinimapBlipAnchor> Planets => _planets;
        public IReadOnlyList<MinimapBlipAnchor> HomePlanets => _homePlanets;
        public IReadOnlyList<MinimapBlipAnchor> Asteroids => _asteroids;
        public IReadOnlyList<MinimapBlipAnchor> GemMoons => _gemMoons;

        /// <summary>[UNITY] Creates hidden root for blip anchor GameObjects.</summary>
        void Awake()
        {
            // --- Unity lifecycle ---
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }

            Instance = this;
            var rootGo = new GameObject("MinimapEcsAnchors");
            rootGo.hideFlags = HideFlags.HideAndDontSave;
            _root = rootGo.transform;
        }

        void OnDestroy()
        {
            // --- Unity lifecycle ---
            if (Instance == this)
                Instance = null;

            if (_root != null)
                Destroy(_root.gameObject);
        }

        /// <summary>Per-frame minimap blip sync — rebuild or position-only update.</summary>
        void LateUpdate()
        {
            // --- Per-frame refresh ---
            var world = EcsGameBridge.GetVisualizationWorld();
            if (world == null || !world.IsCreated)
                return;

            SyncMapSize(world.EntityManager);

            if (Time.time - _lastCacheRefreshTime >= EntityCacheRefreshInterval)
            {
                RebuildAnchors(world.EntityManager);
                _lastCacheRefreshTime = Time.time;
            }
            else
            {
                UpdateAnchorPositions(world.EntityManager);
            }
        }

        /// <summary>Local player blip for minimap centering and team tint.</summary>
        public bool TryGetLocalPlayer(out MinimapBlipAnchor anchor)
        {
            anchor = _localPlayer;
            return anchor != null;
        }

        /// <summary>Reads toroidal map dimensions from MapStateSingleton when available.</summary>
        void SyncMapSize(EntityManager em)
        {
            // --- SyncMapSize ---
            float mapW = ToroidalMapEcs.MapWidth;
            float mapH = ToroidalMapEcs.MapHeight;
            using var mapQuery = em.CreateEntityQuery(typeof(MapStateSingleton));
            if (mapQuery.TryGetSingleton<MapStateSingleton>(out var map))
            {
                mapW = map.MapWidth;
                mapH = map.MapHeight;
            }

            if (mapW == _lastMapWidth && mapH == _lastMapHeight)
                return;

            _lastMapWidth = mapW;
            _lastMapHeight = mapH;
            ToroidalMap.SetMapSize(mapW, mapH);
        }

        void RebuildAnchors(EntityManager em)
        {
            // --- Rebuild cache ---
            var alive = new HashSet<Entity>();
            _localPlayer = null;

            SyncShips(em, alive);
            SyncPlanets(em, alive);
            SyncAsteroids(em, alive);

            var remove = new List<Entity>();
            foreach (var kv in _anchors)
            {
                if (!alive.Contains(kv.Key))
                    remove.Add(kv.Key);
            }

            foreach (var entity in remove)
                DestroyAnchor(entity);

            var removeMoons = new List<int>();
            foreach (var kv in _gemMoonsByPlanetId)
            {
                bool planetAlive = false;
                foreach (var planet in _anchors.Values)
                {
                    if (planet != null &&
                        (planet.Kind == MinimapBlipKind.Planet || planet.Kind == MinimapBlipKind.HomePlanet) &&
                        planet.PlanetId == kv.Key)
                    {
                        planetAlive = true;
                        break;
                    }
                }

                if (!planetAlive)
                    removeMoons.Add(kv.Key);
            }

            foreach (int planetId in removeMoons)
            {
                if (_gemMoonsByPlanetId.TryGetValue(planetId, out var moon) && moon != null)
                    Destroy(moon.gameObject);
                _gemMoonsByPlanetId.Remove(planetId);
            }

            RebuildLists();
        }

        void UpdateAnchorPositions(EntityManager em)
        {
            // --- Per-frame refresh ---
            double elapsed = Time.timeAsDouble;
            foreach (var kv in _anchors)
            {
                var entity = kv.Key;
                var anchor = kv.Value;
                if (anchor == null || !em.Exists(entity))
                    continue;

                if (!em.HasComponent<LocalTransform>(entity))
                    continue;

                var lt = em.GetComponentData<LocalTransform>(entity);
                anchor.transform.position = lt.Position;
                anchor.transform.localScale = Vector3.one * math.max(0.25f, lt.Scale);

                if (anchor.Kind == MinimapBlipKind.GemMoon)
                    continue;

                if (anchor.Kind == MinimapBlipKind.Ship && em.HasComponent<ShipState>(entity))
                {
                    var ship = em.GetComponentData<ShipState>(entity);
                    anchor.Team = ship.Team;
                    anchor.IsDead = ship.IsDead;
                    anchor.AwaitingTeamSelection = ship.AwaitingTeamSelection;
                    anchor.IsLocalPlayer = em.HasComponent<LocalPlayerShipTag>(entity) ||
                                           em.HasComponent<GhostOwnerIsLocal>(entity);
                    if (anchor.IsLocalPlayer)
                        _localPlayer = anchor;
                }
                else if ((anchor.Kind == MinimapBlipKind.Planet || anchor.Kind == MinimapBlipKind.HomePlanet) &&
                         em.HasComponent<PlanetState>(entity))
                {
                    var planet = em.GetComponentData<PlanetState>(entity);
                    anchor.Team = planet.Ownership;
                    anchor.PlanetLevel = planet.PlanetLevel;
                    anchor.Population = planet.Population;
                    anchor.PlanetId = planet.PlanetId;
                    anchor.BodySize = math.max(0.25f, lt.Scale);
                    UpdateGemMoonAnchor(anchor, lt, planet, elapsed);
                }
                else if (anchor.Kind == MinimapBlipKind.Asteroid && em.HasComponent<AsteroidState>(entity))
                {
                    // --- if ---
                    var asteroid = em.GetComponentData<AsteroidState>(entity);
                    anchor.IsDestroyed = asteroid.IsDestroyed;
                    anchor.BodySize = math.max(0.25f, lt.Scale);
                }
            }

            if (_localPlayer == null)
                TryResolveLocalPlayerByNetworkId(em);
        }

        void TryResolveLocalPlayerByNetworkId(EntityManager em)
        {
            // --- Attempt resolution ---
            int localId = EcsGameBridge.GetLocalNetworkId();
            if (localId <= 0)
                return;

            using var query = em.CreateEntityQuery(typeof(ShipTag), typeof(GhostOwner));
            using var entities = query.ToEntityArray(Allocator.Temp);
            using var owners = query.ToComponentDataArray<GhostOwner>(Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
            {
                if (owners[i].NetworkId != localId)
                    continue;
                if (_anchors.TryGetValue(entities[i], out var anchor))
                {
                    anchor.IsLocalPlayer = true;
                    _localPlayer = anchor;
                }

                break;
            }
        }

        void SyncShips(EntityManager em, HashSet<Entity> alive)
        {
            // --- SyncShips ---
            using var query = em.CreateEntityQuery(typeof(ShipTag), typeof(ShipState), typeof(LocalTransform));
            using var entities = query.ToEntityArray(Allocator.Temp);
            using var states = query.ToComponentDataArray<ShipState>(Allocator.Temp);
            using var transforms = query.ToComponentDataArray<LocalTransform>(Allocator.Temp);

            for (int i = 0; i < entities.Length; i++)
            {
                var entity = entities[i];
                alive.Add(entity);
                var state = states[i];
                var lt = transforms[i];
                var anchor = GetOrCreateAnchor(entity, MinimapBlipKind.Ship);
                anchor.Team = state.Team;
                anchor.IsDead = state.IsDead;
                anchor.AwaitingTeamSelection = state.AwaitingTeamSelection;
                anchor.IsLocalPlayer = em.HasComponent<LocalPlayerShipTag>(entity) ||
                                       em.HasComponent<GhostOwnerIsLocal>(entity);
                anchor.BodySize = math.max(0.25f, lt.Scale);
                anchor.transform.position = lt.Position;
                anchor.transform.localScale = Vector3.one * anchor.BodySize;
                if (anchor.IsLocalPlayer)
                    _localPlayer = anchor;
            }
        }

        void SyncPlanets(EntityManager em, HashSet<Entity> alive)
        {
            // --- SyncPlanets ---
            using var query = em.CreateEntityQuery(typeof(PlanetTag), typeof(PlanetState), typeof(LocalTransform));
            using var entities = query.ToEntityArray(Allocator.Temp);
            using var states = query.ToComponentDataArray<PlanetState>(Allocator.Temp);
            using var transforms = query.ToComponentDataArray<LocalTransform>(Allocator.Temp);
            double elapsed = Time.timeAsDouble;

            for (int i = 0; i < entities.Length; i++)
            {
                var entity = entities[i];
                alive.Add(entity);
                var state = states[i];
                var lt = transforms[i];
                var kind = state.IsHomePlanet ? MinimapBlipKind.HomePlanet : MinimapBlipKind.Planet;
                var anchor = GetOrCreateAnchor(entity, kind);
                anchor.Team = state.Ownership;
                anchor.PlanetLevel = state.PlanetLevel;
                anchor.Population = state.Population;
                anchor.PlanetId = state.PlanetId;
                anchor.BodySize = math.max(0.25f, lt.Scale);
                anchor.transform.position = lt.Position;
                anchor.transform.localScale = Vector3.one * anchor.BodySize;
                UpdateGemMoonAnchor(anchor, lt, state, elapsed);
            }
        }

        void UpdateGemMoonAnchor(MinimapBlipAnchor planetAnchor, LocalTransform lt, PlanetState state, double elapsed)
        {
            // --- Per-frame refresh ---
            if (!_gemMoonsByPlanetId.TryGetValue(state.PlanetId, out var moonAnchor) || moonAnchor == null)
            {
                var go = new GameObject($"MinimapAnchor_GemMoon_{state.PlanetId}");
                go.hideFlags = HideFlags.HideAndDontSave;
                go.transform.SetParent(_root, false);
                moonAnchor = go.AddComponent<MinimapBlipAnchor>();
                moonAnchor.Kind = MinimapBlipKind.GemMoon;
                _gemMoonsByPlanetId[state.PlanetId] = moonAnchor;
            }

            moonAnchor.Team = state.Ownership;
            moonAnchor.PlanetId = state.PlanetId;
            moonAnchor.PlanetLevel = state.PlanetLevel;
            moonAnchor.IsHomePlanet = state.IsHomePlanet;
            moonAnchor.BodySize = planetAnchor.BodySize;

            float homeMul = state.IsHomePlanet ? 1.5f : 1f;
            moonAnchor.MoonVisualSize = PlanetGemMoonMath.ComputeVisualUniformScale(planetAnchor.BodySize, homeMul) *
                                        planetAnchor.BodySize;
            var offset = PlanetGemMoonMath.GetMoonOrbitOffset(
                planetAnchor.BodySize,
                state.PlanetLevel,
                state.IsHomePlanet,
                state.PlanetId,
                elapsed);
            moonAnchor.transform.position = (Vector3)(lt.Position + offset);
            moonAnchor.transform.localScale = Vector3.one * moonAnchor.MoonVisualSize;
        }

        void SyncAsteroids(EntityManager em, HashSet<Entity> alive)
        {
            // --- SyncAsteroids ---
            using var query = em.CreateEntityQuery(typeof(AsteroidTag), typeof(AsteroidState), typeof(LocalTransform));
            using var entities = query.ToEntityArray(Allocator.Temp);
            using var states = query.ToComponentDataArray<AsteroidState>(Allocator.Temp);
            using var transforms = query.ToComponentDataArray<LocalTransform>(Allocator.Temp);

            for (int i = 0; i < entities.Length; i++)
            {
                var entity = entities[i];
                alive.Add(entity);
                var state = states[i];
                var lt = transforms[i];
                var anchor = GetOrCreateAnchor(entity, MinimapBlipKind.Asteroid);
                anchor.IsDestroyed = state.IsDestroyed;
                anchor.BodySize = math.max(0.25f, lt.Scale);
                anchor.transform.position = lt.Position;
                anchor.transform.localScale = Vector3.one * anchor.BodySize;
            }
        }

        MinimapBlipAnchor GetOrCreateAnchor(Entity entity, MinimapBlipKind kind)
        {
            // --- Compute value ---
            if (_anchors.TryGetValue(entity, out var existing) && existing != null)
            {
                existing.Kind = kind;
                return existing;
            }

            var go = new GameObject($"MinimapAnchor_{kind}_{entity.Index}");
            go.hideFlags = HideFlags.HideAndDontSave;
            go.transform.SetParent(_root, false);
            var anchor = go.AddComponent<MinimapBlipAnchor>();
            anchor.Kind = kind;
            anchor.SourceEntity = entity;
            _anchors[entity] = anchor;
            return anchor;
        }

        void DestroyAnchor(Entity entity)
        {
            // --- DestroyAnchor ---
            if (!_anchors.TryGetValue(entity, out var anchor))
                return;

            if (_localPlayer == anchor)
                _localPlayer = null;

            if (anchor != null)
                Destroy(anchor.gameObject);
            _anchors.Remove(entity);
        }

        void RebuildLists()
        {
            // --- Rebuild cache ---
            _ships.Clear();
            _planets.Clear();
            _homePlanets.Clear();
            _asteroids.Clear();
            _gemMoons.Clear();

            foreach (var anchor in _anchors.Values)
            {
                if (anchor == null)
                    continue;

                switch (anchor.Kind)
                {
                    case MinimapBlipKind.Ship:
                        _ships.Add(anchor);
                        break;
                    case MinimapBlipKind.Planet:
                        _planets.Add(anchor);
                        break;
                    case MinimapBlipKind.HomePlanet:
                        _homePlanets.Add(anchor);
                        break;
                    case MinimapBlipKind.Asteroid:
                        _asteroids.Add(anchor);
                        break;
                }
            }

            foreach (var moon in _gemMoonsByPlanetId.Values)
            {
                if (moon != null)
                    _gemMoons.Add(moon);
            }
        }
    }
}
