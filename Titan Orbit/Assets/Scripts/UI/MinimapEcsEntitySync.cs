using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.ECS;
using TitanOrbit.Game;
using TitanOrbit.Generation;
using TitanOrbit.NetCode;
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
    /// <para>
    /// [TITAN-ORBIT] While Settling, this component does nothing (loading screen).
    /// After settle, <see cref="ClientJoinSettleCache.TransformQuarantine"/> stays on for the
    /// whole Windows in-game session — map-body <c>ToEntityArray</c> still Crash!!! then
    /// (Player.log 2026-07-18 14:24). Under quarantine we rebuild planet/asteroid blips from
    /// <see cref="EcsWorldVisualizer"/> hybrid proxies (managed dictionary walk). Ship queries
    /// stay small and match the visualizer's own ship <c>ToEntityArray</c> path.
    /// </para>
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

        /// <summary>
        /// Scratch list for quarantine-safe proxy entity keys from <see cref="EcsWorldVisualizer"/>.
        /// Reused to avoid per-rebuild List allocations on the hot path.
        /// </summary>
        readonly List<Entity> _proxyEntityScratch = new List<Entity>(256);

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
            // --- Join settle / ship Instantiates gate ---
            // [TITAN-ORBIT] During GhostSpawn Instantiates the loading screen owns the UI
            // (Settling). After Join Team, Settling stays OFF but GhostSpawnBacklog covers the
            // ship Instantiates window — SyncShips ToEntityArray then Crash!!! (2026-07-19).
            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
                return;

            // --- Per-frame refresh ---
            var world = EcsGameBridge.GetVisualizationWorld();
            if (world == null || !world.IsCreated)
                return;

            SyncMapSize(world.EntityManager);

            if (Time.time - _lastCacheRefreshTime >= EntityCacheRefreshInterval)
            {
                // --- Quarantine vs full ECS gather ---
                // [TITAN-ORBIT] TransformQuarantine stays true all in-game on Windows. Returning
                // early here hid the minimap forever (no local-player blip). Instead: ships via
                // small ToEntityArray; planets/asteroids via hybrid proxy registry (no map gather).
                if (ClientJoinSettleCache.TransformQuarantine)
                    RebuildAnchorsFromHybridProxies(world.EntityManager);
                else
                    RebuildAnchors(world.EntityManager);

                _lastCacheRefreshTime = Time.time;
            }
            else
            {
                // Position-only: iterates known anchors with Exists/GetComponentData — no gather.
                UpdateAnchorPositions(world.EntityManager);
            }
        }

        /// <summary>Local player blip for minimap centering and team tint.</summary>
        public bool TryGetLocalPlayer(out MinimapBlipAnchor anchor)
        {
            anchor = _localPlayer;
            return anchor != null;
        }

        /// <summary>
        /// Keeps <see cref="ToroidalMap"/> and <see cref="ToroidalMapEcs"/> on the rolled match size.
        /// Prefers MapStateSingleton, then <see cref="MapSessionMetaCache"/> (dedicated clients often
        /// never receive the singleton ghost — without this, size stays at the 1000 default).
        /// </summary>
        void SyncMapSize(EntityManager em)
        {
            // --- Resolve authoritative size (no CreateEntityQuery on the hot path) ---
            // [TITAN-ORBIT] Prefer MapSessionMetaCache — same as ToroidalDisplay.SyncMapSize.
            // Creating a MapStateSingleton query every LateUpdate while flying was wasted work:
            // we early-out on unchanged size only AFTER paying CreateEntityQuery.
            // Never invent a period — only apply when a real rolled size is available.
            float mapW = 0f;
            float mapH = 0f;
            bool haveSize = false;
            if (MapSessionMetaCache.HasMapSize)
            {
                mapW = MapSessionMetaCache.MapWidth;
                mapH = MapSessionMetaCache.MapHeight;
                haveSize = true;
            }
            else if (ToroidalMapEcs.TryGetMapSize(out mapW, out mapH))
            {
                haveSize = true;
            }
            else
            {
                using var mapQuery = em.CreateEntityQuery(typeof(MapStateSingleton));
                if (mapQuery.TryGetSingleton<MapStateSingleton>(out var map) &&
                    ToroidalMapEcs.IsValidMapSize(map.MapWidth, map.MapHeight))
                {
                    mapW = map.MapWidth;
                    mapH = map.MapHeight;
                    haveSize = true;
                }
            }

            if (!haveSize)
                return;

            if (mapW == _lastMapWidth && mapH == _lastMapHeight)
                return;

            _lastMapWidth = mapW;
            _lastMapHeight = mapH;
            // [TITAN-ORBIT] Both caches — display wrap used Ecs, minimap used ToroidalMap; they diverged.
            MapSessionMetaCache.ApplyMapSizeToToroidalHelpers(mapW, mapH);
        }

        /// <summary>
        /// Full rebuild using ECS queries (ships + planets + asteroids).
        /// Only safe when <see cref="ClientJoinSettleCache.TransformQuarantine"/> is false —
        /// Editor/MPPM paths without the Windows join quarantine.
        /// </summary>
        void RebuildAnchors(EntityManager em)
        {
            // --- Rebuild cache (full ECS gathers) ---
            var alive = new HashSet<Entity>();
            _localPlayer = null;

            SyncShips(em, alive);
            SyncPlanets(em, alive);
            SyncAsteroids(em, alive);

            PruneDeadAnchors(alive);
            RebuildLists();
        }

        /// <summary>
        /// Quarantine-safe rebuild: ships via small query; planets/asteroids from hybrid proxies.
        /// [TITAN-ORBIT] Must not call <see cref="SyncPlanets"/> / <see cref="SyncAsteroids"/> —
        /// those <c>ToEntityArray</c> map bodies while GhostSpawn Instantiates is in flight.
        /// </summary>
        void RebuildAnchorsFromHybridProxies(EntityManager em)
        {
            // --- Rebuild cache (proxy walk) ---
            var alive = new HashSet<Entity>();
            _localPlayer = null;

            // Ships stay few; same ToEntityArray shape as EcsWorldVisualizer.SyncShipProxyTransforms.
            SyncShips(em, alive);

            var visualizer = EcsWorldVisualizer.Active;
            if (visualizer != null)
            {
                visualizer.CopyLiveProxyEntities(_proxyEntityScratch);
                // [TITAN-ORBIT] Shared ServerTick moon clock — not Unity Time.timeAsDouble.
                double elapsed = PlanetGemMoonOrbitClock.TryGetElapsedSeconds(out double orbitElapsed, includeTickFraction: true)
                    ? orbitElapsed
                    : Time.timeAsDouble;

                for (int i = 0; i < _proxyEntityScratch.Count; i++)
                {
                    Entity entity = _proxyEntityScratch[i];
                    if (!em.Exists(entity) || !em.HasComponent<LocalTransform>(entity))
                        continue;

                    // Per-entity HasComponent — not GatherEntitiesWithoutFilter over all asteroids.
                    if (em.HasComponent<PlanetTag>(entity) && em.HasComponent<PlanetState>(entity))
                    {
                        SyncOnePlanet(em, entity, alive, elapsed);
                    }
                    else if (em.HasComponent<AsteroidTag>(entity) && em.HasComponent<AsteroidState>(entity))
                    {
                        SyncOneAsteroid(em, entity, alive);
                    }
                    // Gems / transports / bullets: no dedicated minimap blip kinds here.
                }
            }

            PruneDeadAnchors(alive);
            RebuildLists();
        }

        /// <summary>
        /// Destroys anchors (and gem-moon helpers) whose entities were not marked alive this rebuild.
        /// </summary>
        void PruneDeadAnchors(HashSet<Entity> alive)
        {
            // --- Drop despawned blips ---
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
        }

        void UpdateAnchorPositions(EntityManager em)
        {
            // --- Per-frame refresh ---
            // [TITAN-ORBIT] Shared ServerTick moon clock — not Unity Time.timeAsDouble.
            double elapsed = PlanetGemMoonOrbitClock.TryGetElapsedSeconds(out double orbitElapsed, includeTickFraction: true)
                ? orbitElapsed
                : Time.timeAsDouble;
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
                    ApplyShipAnchorPresentation(em, entity, anchor, ship, lt);
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
                    anchor.IsHomePlanet = planet.IsHomePlanet;
                    anchor.ShipFamilyConfigIndex = planet.ShipFamilyConfigIndex;
                    anchor.BodySize = math.max(0.25f, lt.Scale);
                    // Per-entity buffer read — not a map-body archetype gather (quarantine-safe).
                    anchor.DefenseTurretBuiltMask = ReadDefenseTurretBuiltMask(em, entity);
                    UpdateGemMoonAnchor(anchor, lt, planet, elapsed);
                }
                else if (anchor.Kind == MinimapBlipKind.Asteroid && em.HasComponent<AsteroidState>(entity))
                {
                    // --- Asteroid blip: destroyed flag + scale only (logical pose, not toroidal) ---
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
            // [TITAN-ORBIT] GhostOwner orphans must not become the minimap center before team pick.
            if (ClientTeamFlowState.ShouldSuppressLocalPlayerControl())
                return;

            // [TITAN-ORBIT] Suppress clear is not enough — ship ToEntityArray during Instantiates
            // Crash!!! (Player.log 2026-07-30 Confirm flush). Parent LateUpdate also gates, but
            // keep the helper self-contained so future callers cannot omit ShouldSkip.
            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
                return;

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
                ApplyShipAnchorPresentation(em, entity, anchor, state, lt);
                anchor.transform.position = lt.Position;
                anchor.transform.localScale = Vector3.one * anchor.BodySize;
                if (anchor.IsLocalPlayer)
                    _localPlayer = anchor;
            }
        }

        /// <summary>
        /// Copies ship ghost fields onto the minimap anchor for silhouette / cargo / badge rendering
        /// (including <see cref="MinimapBlipAnchor.IsMega"/> so MEGAs get a troop-fill triangle).
        /// Controller reads anchors only — no ECS walks there.
        /// </summary>
        static void ApplyShipAnchorPresentation(
            EntityManager em,
            Entity entity,
            MinimapBlipAnchor anchor,
            ShipState ship,
            LocalTransform lt)
        {
            // --- Core identity / team ---
            anchor.Team = ship.Team;
            anchor.IsDead = ship.IsDead;
            anchor.AwaitingTeamSelection = ship.AwaitingTeamSelection;
            // [TITAN-ORBIT] No local blip until Join Team / resume confirms.
            anchor.IsLocalPlayer = !ClientTeamFlowState.ShouldSuppressLocalPlayerControl() &&
                                   (em.HasComponent<LocalPlayerShipTag>(entity) ||
                                    em.HasComponent<GhostOwnerIsLocal>(entity));
            anchor.BodySize = math.max(0.25f, lt.Scale);

            // --- Chassis ladder (minimap scales the regular-ship Cross from ShipLevel; MEGA stays a triangle) ---
            anchor.ShipLevel = ship.ShipLevel;
            anchor.BranchIndex = ship.BranchIndex;
            anchor.ShipFamilyConfigIndex = ship.ShipFamilyConfigIndex;

            // --- MEGA hull flag (hex vs Cross on the minimap) ---
            // [NETCODE] MegaShipState is baked on StarshipGhost and ghosted, so late joiners
            // already see IsMega. Per-entity HasComponent matches ShipMatchStats below —
            // not a map-body gather, so this stays quarantine-safe.
            anchor.IsMega = em.HasComponent<MegaShipState>(entity)
                            && em.GetComponentData<MegaShipState>(entity).IsMega;

            // --- Live vitals / cargo (nameplates + minimap consumers) ---
            anchor.Health = ship.Health;
            anchor.MaxHealth = ship.MaxHealth;
            anchor.CurrentGems = ship.CurrentGems;
            anchor.GemCapacity = ship.GemCapacity;
            anchor.CurrentPeople = ship.CurrentPeople;
            anchor.PeopleCapacity = ship.PeopleCapacity;

            // --- Facing (optional consumers; ship blips stay axis-aligned) ---
            float3 forward = math.mul(lt.Rotation, new float3(0f, 0f, 1f));
            anchor.YawDegrees = math.degrees(math.atan2(forward.x, forward.z));

            // --- Owner id (role-dot tie-break) ---
            anchor.OwnerNetworkId = 0;
            if (em.HasComponent<GhostOwner>(entity))
                anchor.OwnerNetworkId = em.GetComponentData<GhostOwner>(entity).NetworkId;

            // --- Match-long scores for top killer / miner / transporter dots ---
            if (em.HasComponent<ShipMatchStats>(entity))
            {
                var stats = em.GetComponentData<ShipMatchStats>(entity);
                anchor.Kills = stats.Kills;
                anchor.GemsDeposited = stats.GemsDeposited;
                anchor.PeopleDelivered = stats.PeopleDelivered;
            }
            else
            {
                anchor.Kills = 0;
                anchor.GemsDeposited = 0;
                anchor.PeopleDelivered = 0;
            }
        }

        /// <summary>
        /// Full planet gather — forbidden under TransformQuarantine (see rebuild-from-proxies path).
        /// </summary>
        void SyncPlanets(EntityManager em, HashSet<Entity> alive)
        {
            // --- SyncPlanets (ECS gather — quarantine OFF only) ---
            using var query = em.CreateEntityQuery(typeof(PlanetTag), typeof(PlanetState), typeof(LocalTransform));
            using var entities = query.ToEntityArray(Allocator.Temp);
            using var states = query.ToComponentDataArray<PlanetState>(Allocator.Temp);
            using var transforms = query.ToComponentDataArray<LocalTransform>(Allocator.Temp);
            // [TITAN-ORBIT] Shared ServerTick moon clock — not Unity Time.timeAsDouble.
            double elapsed = PlanetGemMoonOrbitClock.TryGetElapsedSeconds(out double orbitElapsed, includeTickFraction: true)
                ? orbitElapsed
                : Time.timeAsDouble;

            for (int i = 0; i < entities.Length; i++)
                ApplyPlanetAnchor(em, entities[i], states[i], transforms[i], alive, elapsed);
        }

        /// <summary>
        /// Writes one planet blip from known entity components (no query gather).
        /// Used by the quarantine proxy walk and by <see cref="SyncPlanets"/>.
        /// </summary>
        void SyncOnePlanet(EntityManager em, Entity entity, HashSet<Entity> alive, double elapsed)
        {
            // --- Single planet from proxy entity ---
            var state = em.GetComponentData<PlanetState>(entity);
            var lt = em.GetComponentData<LocalTransform>(entity);
            ApplyPlanetAnchor(em, entity, state, lt, alive, elapsed);
        }

        /// <summary>Applies PlanetState + LocalTransform onto a minimap planet/home blip + gem moon.</summary>
        void ApplyPlanetAnchor(
            EntityManager em,
            Entity entity,
            PlanetState state,
            LocalTransform lt,
            HashSet<Entity> alive,
            double elapsed)
        {
            // --- Write planet blip fields ---
            alive.Add(entity);
            var kind = state.IsHomePlanet ? MinimapBlipKind.HomePlanet : MinimapBlipKind.Planet;
            var anchor = GetOrCreateAnchor(entity, kind);
            anchor.Team = state.Ownership;
            anchor.PlanetLevel = state.PlanetLevel;
            anchor.Population = state.Population;
            anchor.PlanetId = state.PlanetId;
            // Home + family index — world labels and the minimap hover tip resolve the name from these.
            anchor.IsHomePlanet = state.IsHomePlanet;
            anchor.ShipFamilyConfigIndex = state.ShipFamilyConfigIndex;
            anchor.BodySize = math.max(0.25f, lt.Scale);
            // Per-entity buffer read — not a map-body archetype gather (quarantine-safe).
            anchor.DefenseTurretBuiltMask = ReadDefenseTurretBuiltMask(em, entity);
            anchor.transform.position = lt.Position;
            anchor.transform.localScale = Vector3.one * anchor.BodySize;
            UpdateGemMoonAnchor(anchor, lt, state, elapsed);
        }

        /// <summary>
        /// Builds a bit mask of slots with active turrets from the ghosted defense buffer.
        /// Safe under TransformQuarantine: single-entity <c>HasBuffer</c>/<c>GetBuffer</c> only.
        /// </summary>
        static byte ReadDefenseTurretBuiltMask(EntityManager em, Entity entity)
        {
            if (entity == Entity.Null || !em.Exists(entity) ||
                !em.HasBuffer<PlanetaryDefenseSlotElement>(entity))
                return 0;

            var buffer = em.GetBuffer<PlanetaryDefenseSlotElement>(entity);
            byte mask = 0;
            for (int i = 0; i < buffer.Length; i++)
            {
                var slot = buffer[i];
                if (slot.TurretLevel <= 0)
                    continue;
                // Prefer SlotIndex so bit i matches minimap / world pad index.
                int bit = slot.SlotIndex < 8 ? slot.SlotIndex : i;
                if (bit >= 0 && bit < 8)
                    mask |= (byte)(1 << bit);
            }

            return mask;
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

        /// <summary>
        /// Full asteroid gather — forbidden under TransformQuarantine (see rebuild-from-proxies path).
        /// </summary>
        void SyncAsteroids(EntityManager em, HashSet<Entity> alive)
        {
            // --- SyncAsteroids (ECS gather — quarantine OFF only) ---
            using var query = em.CreateEntityQuery(typeof(AsteroidTag), typeof(AsteroidState), typeof(LocalTransform));
            using var entities = query.ToEntityArray(Allocator.Temp);
            using var states = query.ToComponentDataArray<AsteroidState>(Allocator.Temp);
            using var transforms = query.ToComponentDataArray<LocalTransform>(Allocator.Temp);

            for (int i = 0; i < entities.Length; i++)
                ApplyAsteroidAnchor(entities[i], states[i], transforms[i], alive);
        }

        /// <summary>
        /// Writes one asteroid blip from known entity components (no query gather).
        /// </summary>
        void SyncOneAsteroid(EntityManager em, Entity entity, HashSet<Entity> alive)
        {
            // --- Single asteroid from proxy entity ---
            var state = em.GetComponentData<AsteroidState>(entity);
            var lt = em.GetComponentData<LocalTransform>(entity);
            ApplyAsteroidAnchor(entity, state, lt, alive);
        }

        /// <summary>Applies AsteroidState + LocalTransform onto a minimap asteroid blip.</summary>
        void ApplyAsteroidAnchor(Entity entity, AsteroidState state, LocalTransform lt, HashSet<Entity> alive)
        {
            // --- Write asteroid blip fields ---
            alive.Add(entity);
            var anchor = GetOrCreateAnchor(entity, MinimapBlipKind.Asteroid);
            anchor.IsDestroyed = state.IsDestroyed;
            anchor.BodySize = math.max(0.25f, lt.Scale);
            anchor.transform.position = lt.Position;
            anchor.transform.localScale = Vector3.one * anchor.BodySize;
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
