using System.Collections.Generic;
using TitanOrbit.Audio;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Entities;
using TitanOrbit.NetCode;
using TitanOrbit.Shared;
using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// GameObject proxies for ghost entities. Transforms come from <see cref="GhostPresentationTransformCache"/>
    /// (published in NetCode PresentationSystemGroup by <see cref="ShipVisualSyncSystem"/>), not raw sim ECS.
    /// Proxies are render shells only — no extra movement smoothing on the local owner.
    /// <para>
    /// [TITAN-ORBIT] Join load: GhostSpawn Instantiates → Pending drain → GameObject proxies
    /// (few per frame). Loading bar numerator is <see cref="MapLoadingProxyCount"/> /
    /// <c>MapSessionMetaCache.LoadingTotalSteps</c> — see <see cref="EcsGameBridge.TryGetJoinLoadProgress"/>.
    /// Drain runs during Settling too so GO Instantiates happen under the loading screen, not after 100%.
    /// </para>
    /// </summary>
    [DefaultExecutionOrder(66000)]
    public class EcsWorldVisualizer : MonoBehaviour
    {
        /// <summary>
        /// [HYBRID] Live visualizer for UI bridges (minimap) that must not <c>ToEntityArray</c>
        /// map bodies under <see cref="ClientJoinSettleCache.TransformQuarantine"/>.
        /// </summary>
        public static EcsWorldVisualizer Active { get; private set; }

        /// <summary>
        /// Max new world-body GameObject Instantiates per frame after join settle.
        /// Loading bar advances when these Instantiates succeed (proxy count / meta N).
        /// </summary>
        const int MaxNewWorldBodyProxiesPerFrame = 48;

        /// <summary>
        /// Cap while GhostSpawn Instantiates are still draining (Settling).
        /// Instantiates are 1/frame — keep GO create modest so Instantiates + GO do not flood one frame.
        /// Still drains every frame so the loading screen absorbs map-build cost.
        /// </summary>
        const int MaxNewWorldBodyProxiesWhileSettling = 8;
        const string DefaultShipFamilyAssetPath = "Assets/Prefabs/Ships/AstroEagle/AstroEagleShipFamily.asset";
        const string DefaultHomePlanetPath = "Assets/Prefabs/HomePlanet.prefab";
        const string DefaultNeutralPlanetPath = "Assets/Prefabs/Planet.prefab";
        const string DefaultAsteroidPath = "Assets/Prefabs/Asteroid.prefab";
        const string DefaultGemPath = "Assets/Prefabs/Gem.prefab";
        const string DefaultPeopleTransportPath = "Assets/Prefabs/PeopleTransport.prefab";

        // --- Inspector references (designer-tunable visual prefabs) ---

        [Header("Ships")]
        /// <summary>Ship family ScriptableObject — chassis prefabs per level and team materials.</summary>
        [SerializeField] ShipFamilyDefinition shipFamily;
        /// <summary>Optional single prefab override when shipFamily is unset.</summary>
        [SerializeField] GameObject shipVisualPrefab;
        /// <summary>Uniform scale multiplier applied on top of ECS <see cref="LocalTransform.Scale"/>.</summary>
        [SerializeField] float shipVisualScale = BodyCollisionMath.ShipPresentationScale;
        /// <summary>Fallback muzzle offset when ship entity lacks <see cref="ShipWeaponConfig"/>.</summary>
        [SerializeField] float defaultMuzzleOffset = 2f;

        [Header("Planets & Bodies")]
        [SerializeField] GameObject homePlanetVisualPrefab;
        [SerializeField] GameObject neutralPlanetVisualPrefab;
        [SerializeField] GameObject asteroidVisualPrefab;
        [SerializeField] GameObject gemVisualPrefab;
        [SerializeField] GameObject peopleTransportVisualPrefab;
        /// <summary>Team-tinted planet materials — shared with WorldBodyVisualApplier.</summary>
        [SerializeField] PlanetMaterialPool planetMaterialPool;

        [Header("Combat VFX")]
        [SerializeField] BulletVfxBank bulletVfxBank;
        [SerializeField] int defaultBulletBankIndex;
        [SerializeField] float defaultBulletScaleMultiplier = 1f;

        [Header("Ship Propulsion VFX")]
        [SerializeField] ShipPropulsionVisualApplier.Settings propulsionVfxSettings;

        // --- Runtime proxy registries (entity → GameObject) ---

        /// <summary>All active ECS entity → visual proxy instances.</summary>
        readonly Dictionary<Entity, GameObject> _proxies = new Dictionary<Entity, GameObject>();
        /// <summary>Bullet entities with stretch-trail cosmetic component attached.</summary>
        readonly Dictionary<Entity, ClientBulletStretchVisual> _bulletStretchVisuals = new Dictionary<Entity, ClientBulletStretchVisual>();
        /// <summary>Ship network id for <see cref="ShipWeaponProxyRegistry"/> weapon mount lookup.</summary>
        readonly Dictionary<Entity, int> _proxyNetworkIds = new Dictionary<Entity, int>();
        /// <summary>Last applied ship level — triggers proxy rebuild on upgrade.</summary>
        readonly Dictionary<Entity, int> _proxyShipLevels = new Dictionary<Entity, int>();
        /// <summary>Last applied team — triggers material swap on capture.</summary>
        readonly Dictionary<Entity, TeamId> _proxyTeams = new Dictionary<Entity, TeamId>();
        /// <summary>Planet visual identity — rebuild when home/team/level/id changes.</summary>
        readonly Dictionary<Entity, PlanetVisualKey> _proxyPlanetVisuals = new Dictionary<Entity, PlanetVisualKey>();

        /// <summary>Cached local ship entity for dedicated-client weapon VFX (avoids per-frame query).</summary>
        Entity _cachedDedicatedLocalShipEntity;
        /// <summary>Cached local ship entity for transform sync and camera pose feed.</summary>
        Entity _cachedLocalPlayerShipEntity;

        /// <summary>Guards VR / multi-camera double onBeforeRender in the same frame.</summary>
        int _lastVisualSyncFrame = -1;

        /// <summary>
        /// [TITAN-ORBIT] Local-ship (or camera) XZ used as toroidal display reference this frame.
        /// Remotes and world bodies unwrap toward this point so seams stay seamless.
        /// </summary>
        Vector3 _toroidalReference;

        /// <summary>True when <see cref="_toroidalReference"/> was resolved for the current sync.</summary>
        bool _hasToroidalReference;

        /// <summary>
        /// New planet/asteroid/gem proxies created this frame (reset in SyncAllProxies).
        /// Used with <see cref="ClientJoinSettleCache.Settling"/> to rate-limit Instantiates.
        /// </summary>
        int _newWorldBodyProxiesThisFrame;

        /// <summary>Local-player ship proxy on dedicated clients.</summary>
        public GameObject LocalPlayerShipProxy { get; private set; }

        /// <summary>
        /// Planet + asteroid + gem GameObject proxies currently alive.
        /// </summary>
        public static int WorldBodyProxyCount { get; private set; }

        /// <summary>
        /// Planet + asteroid GameObject proxies only — loading-bar numerator.
        /// Matches <c>MapSessionMetaCache.LoadingTotalSteps</c> (homes + neutrals + asteroids).
        /// Do not count gems/transports; those are not part of the map-build total.
        /// </summary>
        public static int MapLoadingProxyCount { get; private set; }

        /// <summary>Composite key for planet proxy rebuild — any field change forces new visual.</summary>
        struct PlanetVisualKey : System.IEquatable<PlanetVisualKey>
        {
            public bool IsHome;
            public TeamId Team;
            public int PlanetLevel;
            public int PlanetId;

            public bool Equals(PlanetVisualKey other) =>
                IsHome == other.IsHome && Team == other.Team && PlanetLevel == other.PlanetLevel && PlanetId == other.PlanetId;
        }

        /// <summary>
        /// [UNITY] Loads default prefabs and ScriptableObjects when inspector references are empty.
        /// Runs once at scene start before any LateUpdate proxy sync.
        /// </summary>
        void Awake()
        {
            // --- Resolve designer assets (editor paths; player builds use serialized refs) ---
            if (shipFamily == null)
                shipFamily = LoadDefaultShipFamily();
            if (planetMaterialPool == null)
                planetMaterialPool = WorldBodyVisualApplier.LoadDefaultMaterialPool();
            if (homePlanetVisualPrefab == null)
                homePlanetVisualPrefab = LoadDefaultPrefab(DefaultHomePlanetPath);
            if (neutralPlanetVisualPrefab == null)
                neutralPlanetVisualPrefab = LoadDefaultPrefab(DefaultNeutralPlanetPath);
            if (asteroidVisualPrefab == null)
                asteroidVisualPrefab = LoadDefaultPrefab(DefaultAsteroidPath);
            if (gemVisualPrefab == null)
                gemVisualPrefab = GemVisualApplier.LoadDefaultGemPrefab();
            if (peopleTransportVisualPrefab == null)
                peopleTransportVisualPrefab = PeopleTransportVisualApplier.LoadDefaultPrefab();
            if (peopleTransportVisualPrefab == null)
                peopleTransportVisualPrefab = LoadDefaultPrefab(DefaultPeopleTransportPath);
            if (bulletVfxBank == null)
                bulletVfxBank = BulletVfxBank.LoadDefault();
            if (propulsionVfxSettings.thrusterJetFlameBank == null ||
                propulsionVfxSettings.thrusterJetFlameBank.Count == 0)
            {
                propulsionVfxSettings = ShipPropulsionVisualApplier.LoadDefaultSettings();
            }
        }

        /// <summary>
        /// [HYBRID] Subscribe to render-phase sync — runs after NetCode PresentationSystemGroup
        /// and after LateUpdate, so GhostPresentationTransformCache is populated for this frame.
        /// </summary>
        void OnEnable()
        {
            // --- Singleton for quarantine-safe UI ---
            // [TITAN-ORBIT] Minimap walks this instance's proxy dictionary instead of ECS gathers.
            Active = this;
            Application.onBeforeRender += OnBeforeRenderSync;
        }

        /// <summary>[UNITY] Unsubscribe to avoid leaks when the visualizer is destroyed.</summary>
        void OnDisable()
        {
            Application.onBeforeRender -= OnBeforeRenderSync;
            if (Active == this)
                Active = null;
        }

        /// <summary>
        /// Copies entity keys of live hybrid GameObject proxies into <paramref name="dst"/>.
        /// Walks the managed dictionary only — never runs an ECS <c>ToEntityArray</c> over asteroids/planets.
        /// Safe under <see cref="ClientJoinSettleCache.TransformQuarantine"/> for minimap rebuilds.
        /// </summary>
        /// <param name="dst">Cleared then filled with entities that currently have a non-null proxy.</param>
        public void CopyLiveProxyEntities(List<Entity> dst)
        {
            // --- Managed registry walk (no Burst gather) ---
            if (dst == null)
                return;

            dst.Clear();
            foreach (var kv in _proxies)
            {
                // Skip destroyed GameObjects; entity may still exist briefly.
                if (kv.Value != null)
                    dst.Add(kv.Key);
            }
        }

        /// <summary>
        /// [HYBRID] Per-frame proxy sync — reads presentation transforms, spawns/destroys GameObjects, applies VFX.
        /// Invoked from Application.onBeforeRender (not LateUpdate) so presentation cache is ready.
        /// </summary>
        void OnBeforeRenderSync() => SyncAllProxies();

        /// <summary>Fallback when onBeforeRender does not fire (some batch/headless paths).</summary>
        void LateUpdate() => SyncAllProxies();

        void SyncAllProxies()
        {
            if (_lastVisualSyncFrame == Time.frameCount)
                return;
            _lastVisualSyncFrame = Time.frameCount;
            var world = PickVisualizationWorld();
            if (world == null || !world.IsCreated)
                return;

            var em = world.EntityManager;
            _newWorldBodyProxiesThisFrame = 0;
            var alive = new HashSet<Entity>();
            bool settling = ClientJoinSettleCache.Settling;

            // --- Toroidal display: unbounded local ship; each body picks its own tile ---
            ToroidalDisplay.BeginFrame();
            ToroidalDisplay.SyncMapSize(em);
            _hasToroidalReference = ToroidalDisplay.TryGetReferencePosition(out _toroidalReference);

            // --- Map bodies: drain baked Pending / existing SpawnRequest only ---
            // [TITAN-ORBIT] Player.log 2026-07-18 21:18: MarkSpawnRequestQuery over unqueued
            // asteroids → ArchetypeChunk.GetNativeArray(EntityTypeHandle) NRE → Crash!!!
            // Same failure as the disabled ECS mark system. Do NOT backfill by scanning all
            // asteroids. Visuals require baked MapBodyHybridVisualPending on ghost prefabs
            // (rebake SubScenes / EntityScenes for the Windows player).
            //
            // Drain during Settling (budgeted) so GameObject Instantiates run under the loading
            // bar. Skipping drain until Settling OFF dumped all GO lag after 100% / Join Team.
            SyncExistingWorldBodyProxyTransforms(em, alive);
            DrainPendingWorldBodyProxies(em, alive);

            // --- Ships ---
            // [TITAN-ORBIT] TransformQuarantine: TransformSystemGroup stays OFF (RE-ENABLE Crash!!!).
            // Entities Graphics needs Parent/LTW — use hybrid ship GO proxies instead.
            bool hybridShips = ClientJoinSettleCache.TransformQuarantine ||
                               !TitanOrbitPresentationConfig.UseEntitiesGraphicsForShips;
            if (hybridShips)
            {
                // [TITAN-ORBIT] EnsureShipProxies uses ToEntityArray on ships — fine when idle, but
                // skip during Settling (ship Instantiates window after Join Team).
                if (!settling)
                    EnsureShipProxies(em);
                SyncShipProxyTransforms(em, alive);
            }

            // --- People transports ---
            // Owned by PeopleTransportVfxDriver (MonoBehaviour Instantiates from VFX bridge).
            // Do not DrawPeopleTransports here — ECS presentation path was unreliable under quarantine.

            // --- Bullets: still quarantine-gated (broader gathers / hit buffers) ---
            // [TITAN-ORBIT] Settling OFF used to unlock ToEntityArray paths → Crash!!! (minimap + draws).
            if (!ClientJoinSettleCache.TransformQuarantine && !settling)
            {
                ProcessBulletHitEvents(em);
                DrawBullets(em, alive);

                var remove = new List<Entity>();
                foreach (var kv in _proxies)
                {
                    if (kv.Value == null || !em.Exists(kv.Key))
                        remove.Add(kv.Key);
                }

                foreach (var entity in remove)
                    DestroyProxy(entity);

                ToroidalDisplay.PruneStale(alive);
            }
            else
            {
                // Quarantine: prune destroyed proxies without world-wide entity gathers.
                // Also prune transport proxies whose presentation entities despawned mid-flight.
                var remove = new List<Entity>();
                foreach (var kv in _proxies)
                {
                    if (kv.Value == null || !em.Exists(kv.Key))
                        remove.Add(kv.Key);
                }

                foreach (var entity in remove)
                    DestroyProxy(entity);
            }

            WorldBodyProxyCount = CountWorldBodyProxies();
            MapLoadingProxyCount = CountMapLoadingProxies();
        }

        /// <summary>
        /// Updates poses for world-body proxies already in <see cref="_proxies"/> without scanning
        /// every asteroid entity (safe during GhostSpawn Instantiates).
        /// </summary>
        void SyncExistingWorldBodyProxyTransforms(EntityManager em, HashSet<Entity> alive)
        {
            foreach (var kv in _proxies)
            {
                Entity entity = kv.Key;
                GameObject go = kv.Value;
                if (go == null || !em.Exists(entity) || !em.HasComponent<LocalTransform>(entity))
                    continue;

                // Ships/bullets have their own sync paths after settle.
                bool isWorldBody = _proxyPlanetVisuals.ContainsKey(entity) ||
                                   go.name.IndexOf("Asteroid", System.StringComparison.Ordinal) >= 0 ||
                                   go.name.IndexOf("Gem", System.StringComparison.Ordinal) >= 0 ||
                                   go.name.IndexOf("Planet", System.StringComparison.Ordinal) >= 0;
                if (!isWorldBody)
                    continue;

                var lt = em.GetComponentData<LocalTransform>(entity);
                float scale = math.max(0.25f, lt.Scale);
                alive.Add(entity);
                go.transform.position = GetVisualPosition(entity, lt.Position);
                go.transform.rotation = lt.Rotation;
                go.transform.localScale = Vector3.one * scale;
            }
        }

        /// <summary>
        /// Instantiates GameObject proxies for entities tagged with baked
        /// <see cref="MapBodyHybridVisualPending"/> or runtime
        /// <see cref="MapBodyHybridVisualSpawnRequest"/>.
        /// Chunk iteration + per-frame budget — never gathers every asteroid.
        /// Runs during Settling (smaller budget) so the loading bar covers GO Instantiates cost.
        /// </summary>
        /// <returns>Number of new proxies created this call.</returns>
        int DrainPendingWorldBodyProxies(EntityManager em, HashSet<Entity> alive)
        {
            // --- Query: baked Pending OR runtime SpawnRequest ---
            // [TITAN-ORBIT] SpawnRequest is the Windows-player backfill path (non-ghost).
            var desc = new EntityQueryDesc
            {
                Any = new[]
                {
                    ComponentType.ReadOnly<MapBodyHybridVisualPending>(),
                    ComponentType.ReadOnly<MapBodyHybridVisualSpawnRequest>(),
                },
                All = new[] { ComponentType.ReadOnly<LocalTransform>() },
                None = new[] { ComponentType.ReadOnly<PendingSpawnPlaceholder>() },
            };
            using var query = em.CreateEntityQuery(desc);
            if (query.IsEmptyIgnoreFilter)
                return 0;

            // --- Collect up to this frame's budget, then mutate ---
            int frameBudget = GetWorldBodyProxyBudgetThisFrame();
            var entityTypeHandle = em.GetEntityTypeHandle();
            using var chunks = query.ToArchetypeChunkArray(Unity.Collections.Allocator.Temp);
            var batch = new List<Entity>(frameBudget);

            for (int c = 0; c < chunks.Length && batch.Count < frameBudget; c++)
            {
                var entities = chunks[c].GetNativeArray(entityTypeHandle);
                for (int i = 0; i < entities.Length && batch.Count < frameBudget; i++)
                    batch.Add(entities[i]);
            }

            int created = 0;
            for (int i = 0; i < batch.Count; i++)
            {
                if (!TryConsumeWorldBodyProxyBudget())
                    break;

                Entity entity = batch[i];
                if (!em.Exists(entity) || !em.HasComponent<LocalTransform>(entity))
                {
                    ClearVisualQueueTags(em, entity);
                    continue;
                }

                if (_proxies.TryGetValue(entity, out var existing) && existing != null)
                {
                    ClearVisualQueueTags(em, entity);
                    if (!em.HasComponent<MapBodyHybridVisualLinked>(entity))
                        em.AddComponentData(entity, new MapBodyHybridVisualLinked());
                    alive.Add(entity);
                    continue;
                }

                var lt = em.GetComponentData<LocalTransform>(entity);
                if (!TryCreateWorldBodyProxyForEntity(em, entity, lt, out _))
                    continue; // Leave queue tags for next frame.

                ClearVisualQueueTags(em, entity);
                if (!em.HasComponent<MapBodyHybridVisualLinked>(entity))
                    em.AddComponentData(entity, new MapBodyHybridVisualLinked());

                alive.Add(entity);
                created++;
            }

            return created;
        }

        /// <summary>Removes baked Pending and/or runtime SpawnRequest after a proxy is handled.</summary>
        static void ClearVisualQueueTags(EntityManager em, Entity entity)
        {
            if (!em.Exists(entity))
                return;
            if (em.HasComponent<MapBodyHybridVisualPending>(entity))
                em.RemoveComponent<MapBodyHybridVisualPending>(entity);
            if (em.HasComponent<MapBodyHybridVisualSpawnRequest>(entity))
                em.RemoveComponent<MapBodyHybridVisualSpawnRequest>(entity);
        }

        /// <summary>
        /// Creates the correct planet/asteroid/gem proxy for an Instantiated map entity.
        /// </summary>
        bool TryCreateWorldBodyProxyForEntity(EntityManager em, Entity entity, LocalTransform lt, out GameObject go)
        {
            go = null;
            float scale = math.max(0.25f, lt.Scale);

            if (em.HasComponent<PlanetState>(entity) && em.HasComponent<PlanetTag>(entity))
            {
                var state = em.GetComponentData<PlanetState>(entity);
                var key = new PlanetVisualKey
                {
                    IsHome = state.IsHomePlanet,
                    Team = state.Ownership,
                    PlanetLevel = state.PlanetLevel,
                    PlanetId = state.PlanetId,
                };

                if (!WorldBodyVisualApplier.TryCreatePlanetVisual(
                        homePlanetVisualPrefab,
                        neutralPlanetVisualPrefab,
                        planetMaterialPool,
                        state.IsHomePlanet,
                        state.Ownership,
                        state.PlanetLevel,
                        state.PlanetId,
                        scale,
                        out go))
                {
                    go = CreatePrimitivePlanetProxy(state.Ownership);
                }

                _proxies[entity] = go;
                _proxyPlanetVisuals[entity] = key;
                go.transform.SetPositionAndRotation(GetVisualPosition(entity, lt.Position), lt.Rotation);
                go.transform.localScale = Vector3.one * scale;
                return true;
            }

            if (em.HasComponent<AsteroidState>(entity) || em.HasComponent<AsteroidTag>(entity))
            {
                if (!WorldBodyVisualApplier.TryCreateAsteroidVisual(
                        asteroidVisualPrefab, lt.Position, scale, out go))
                {
                    go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    go.name = "AsteroidTagProxy";
                    var col = go.GetComponent<Collider>();
                    if (col != null) Destroy(col);
                    var renderer = go.GetComponent<Renderer>();
                    if (renderer != null)
                        renderer.material = WorldBodyVisualApplier.CreateLitMaterial(new Color(0.55f, 0.45f, 0.35f));
                    WorldBodyVisualApplier.EnsureAsteroidSpin(go, lt.Position);
                }

                _proxies[entity] = go;
                go.transform.position = GetVisualPosition(entity, lt.Position);
                go.transform.localScale = Vector3.one * scale;
                return true;
            }

            if (em.HasComponent<GemState>(entity) && em.HasComponent<GemTag>(entity))
            {
                var state = em.GetComponentData<GemState>(entity);
                scale = GemVisualApplier.ComputeVisualScale(math.max(0.25f, state.Value));
                if (!GemVisualApplier.TryCreateGemVisual(gemVisualPrefab, state.Value, out go))
                {
                    go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    go.name = "GemTagProxy";
                    var col = go.GetComponent<Collider>();
                    if (col != null) Destroy(col);
                    var renderer = go.GetComponent<Renderer>();
                    if (renderer != null)
                        renderer.material = WorldBodyVisualApplier.CreateLitMaterial(Color.yellow);
                }

                _proxies[entity] = go;
                go.transform.SetPositionAndRotation(GetVisualPosition(entity, lt.Position), lt.Rotation);
                go.transform.localScale = Vector3.one * scale;
                GemVisualDiameterRegistry.SetDiameter(entity, GemVisualApplier.ReadWorldDiameter(go, state.Value));
                return true;
            }

            return false;
        }

        /// <summary>Counts planet/asteroid/gem proxies (excludes ships/bullets).</summary>
        int CountWorldBodyProxies()
        {
            int n = 0;
            foreach (var kv in _proxies)
            {
                if (kv.Value == null)
                    continue;
                // Ship proxies are also in _proxies; world bodies use TagProxy names or planet keys.
                if (_proxyPlanetVisuals.ContainsKey(kv.Key) ||
                    kv.Value.name.IndexOf("Asteroid", System.StringComparison.Ordinal) >= 0 ||
                    kv.Value.name.IndexOf("Gem", System.StringComparison.Ordinal) >= 0 ||
                    kv.Value.name.IndexOf("Planet", System.StringComparison.Ordinal) >= 0 ||
                    kv.Value.name.IndexOf("PeopleTransport", System.StringComparison.Ordinal) >= 0)
                    n++;
            }

            return n;
        }

        /// <summary>
        /// Counts only planet + asteroid GameObjects for the loading bar.
        /// [TITAN-ORBIT] Progress = local GO Instantiates, not server packet / ECS Instantiates count.
        /// </summary>
        int CountMapLoadingProxies()
        {
            int n = 0;
            foreach (var kv in _proxies)
            {
                if (kv.Value == null)
                    continue;
                if (_proxyPlanetVisuals.ContainsKey(kv.Key) ||
                    kv.Value.name.IndexOf("Asteroid", System.StringComparison.Ordinal) >= 0 ||
                    kv.Value.name.IndexOf("Planet", System.StringComparison.Ordinal) >= 0)
                    n++;
            }

            return n;
        }

        /// <summary>Delegates world pick to <see cref="EcsGameBridge.GetVisualizationWorld"/>.</summary>
        static World PickVisualizationWorld() => EcsGameBridge.GetVisualizationWorld();

        /// <summary>
        /// [HYBRID] Prefer presentation cache from ShipVisualSyncSystem; fall back to raw LocalTransform
        /// only when the entity has never been published (spawn frame, world not ready).
        /// </summary>
        static bool TryGetPresentationTransform(Entity entity, EntityManager em, out LocalTransform lt)
        {
            lt = default;
            // [NETCODE] Use the cache whenever we have a snapshot — one-frame staleness is fine;
            // rejecting stale entries and falling back to sim LocalTransform caused pose fighting.
            if (GhostPresentationTransformCache.TryGetShip(entity, out var snap))
            {
                lt = LocalTransform.FromPositionRotationScale(snap.Position, snap.Rotation, snap.Scale);
                return true;
            }

            if (!em.HasComponent<LocalTransform>(entity))
                return false;

            lt = em.GetComponentData<LocalTransform>(entity);
            return true;
        }

        /// <summary>Presentation pose for people-transport ghosts (separate cache slot from ships).</summary>
        static bool TryGetPeopleTransportPresentationTransform(Entity entity, EntityManager em, out LocalTransform lt)
        {
            lt = default;
            if (GhostPresentationTransformCache.TryGetPeopleTransport(entity, out var snap))
            {
                lt = LocalTransform.FromPositionRotationScale(snap.Position, snap.Rotation, snap.Scale);
                return true;
            }

            if (!em.HasComponent<LocalTransform>(entity))
                return false;

            lt = em.GetComponentData<LocalTransform>(entity);
            return true;
        }

        /// <summary>Applies position, rotation, and uniform scale to a generic body proxy.</summary>
        static void ApplyProxyTransform(Vector3 target, Quaternion targetRot, Transform go, float scale)
        {
            go.SetPositionAndRotation(target, targetRot);
            go.localScale = Vector3.one * scale;
        }

        /// <summary>
        /// Applies NetCode presentation pose to the GameObject proxy. No extra lerp on the local owner —
        /// prediction + GhostPredictionSmoothing own sim feel; proxies are render shells only.
        /// Remotes use toroidal display unwrap so they appear near the local ship across a seam.
        /// </summary>
        void ApplyShipProxyTransform(
            Entity entity,
            EntityManager em,
            bool isLocalPlayerShip,
            in LocalTransform lt,
            Transform go,
            float scale)
        {
            // --- Local = logical wrapped; remote = hysteresis tile near logical ship ---
            Vector3 pos = isLocalPlayerShip
                ? (Vector3)lt.Position
                : GetVisualPosition(entity, em, lt.Position);
            Quaternion rot = lt.Rotation;
            go.SetPositionAndRotation(pos, rot);
            go.localScale = Vector3.one * scale;

            if (isLocalPlayerShip)
                ShipDisplayPose.SetLocalPose(pos, rot);
        }

        /// <summary>
        /// Resolves and caches local player ship entity — LocalPlayerShipTag first, then GhostOwner NetworkId.
        /// Returns false while team/rejoin flow suppresses control so map-load orphans do not drive camera.
        /// </summary>
        bool TryResolveLocalPlayerShipEntityCached(EntityManager em, out Entity localShipEntity)
        {
            localShipEntity = Entity.Null;

            // [TITAN-ORBIT] Match EcsGameBridge / ShipVisualSyncSystem — no "my ship" before Join Team.
            if (ClientTeamFlowState.ShouldSuppressLocalPlayerControl())
            {
                _cachedLocalPlayerShipEntity = Entity.Null;
                LocalPlayerShipProxy = null;
                return false;
            }

            if (_cachedLocalPlayerShipEntity != Entity.Null &&
                em.Exists(_cachedLocalPlayerShipEntity) &&
                em.HasComponent<ShipTag>(_cachedLocalPlayerShipEntity))
            {
                localShipEntity = _cachedLocalPlayerShipEntity;
                return true;
            }

            using var localQuery = em.CreateEntityQuery(
                ComponentType.ReadOnly<LocalPlayerShipTag>(),
                ComponentType.ReadOnly<ShipTag>());
            using var localEntities = localQuery.ToEntityArray(Unity.Collections.Allocator.Temp);
            if (localEntities.Length > 0)
            {
                localShipEntity = localEntities[0];
                _cachedLocalPlayerShipEntity = localShipEntity;
                return true;
            }

            int localId = EcsGameBridge.GetLocalNetworkId();
            if (localId > 0)
            {
                using var owned = em.CreateEntityQuery(typeof(ShipTag), typeof(GhostOwner));
                using var owners = owned.ToComponentDataArray<GhostOwner>(Unity.Collections.Allocator.Temp);
                using var entities = owned.ToEntityArray(Unity.Collections.Allocator.Temp);
                for (int i = 0; i < entities.Length; i++)
                {
                    if (owners[i].NetworkId != localId)
                        continue;
                    localShipEntity = entities[i];
                    _cachedLocalPlayerShipEntity = localShipEntity;
                    return true;
                }
            }

            return false;
        }

        /// <summary>True when entity matches cached local ship — feeds ShipDisplayPose and weapon VFX.</summary>
        static bool IsLocalPlayerShip(Entity entity, Entity localShipEntity) =>
            localShipEntity != Entity.Null && entity == localShipEntity;

        /// <summary>
        /// [TITAN-ORBIT] Local ship stays at its real (unbounded) pose. Every other body picks its
        /// own nearest map-tile copy relative to that ship, with per-entity hysteresis so planets
        /// and asteroids reposition individually — not as one global blink when crossing a seam.
        /// </summary>
        /// <param name="forceLogical">When true, skip display unwrap (rare debug / special cases).</param>
        Vector3 GetVisualPosition(Entity entity, EntityManager em, float3 logicalPos, bool forceLogical = false)
        {
            if (forceLogical || ToroidalDisplay.IsLocalPlayerShip(em, entity))
                return logicalPos;

            if (!_hasToroidalReference && !ToroidalDisplay.TryGetReferencePosition(out _toroidalReference))
                return logicalPos;

            _hasToroidalReference = true;
            return ToroidalDisplay.ToDisplayPositionWithHysteresis(entity, logicalPos, _toroidalReference);
        }

        /// <summary>Per-entity tile unwrap when EntityManager is not needed for local-ship checks.</summary>
        Vector3 GetVisualPosition(Entity entity, float3 logicalPos)
        {
            if (!_hasToroidalReference && !ToroidalDisplay.TryGetReferencePosition(out _toroidalReference))
                return logicalPos;

            _hasToroidalReference = true;
            return ToroidalDisplay.ToDisplayPositionWithHysteresis(entity, logicalPos, _toroidalReference);
        }

        /// <summary>
        /// Spawns missing ship proxies and rebuilds when team or ship level changes.
        /// Does not move transforms — SyncShipProxyTransforms handles per-frame pose.
        /// </summary>
        void EnsureShipProxies(EntityManager em)
        {
            TryResolveLocalPlayerShipEntityCached(em, out var localShipEntity);

            using var query = em.CreateEntityQuery(
                ComponentType.ReadOnly<ShipTag>(),
                ComponentType.ReadOnly<LocalTransform>());
            using var entities = query.ToEntityArray(Unity.Collections.Allocator.Temp);
            using var transforms = query.ToComponentDataArray<LocalTransform>(Unity.Collections.Allocator.Temp);

            int localNetworkId = EcsGameBridge.GetLocalNetworkId();
            bool suppressOwnedVisuals = ClientTeamFlowState.ShouldSuppressLocalPlayerControl();

            for (int i = 0; i < entities.Length; i++)
            {
                var entity = entities[i];
                var lt = transforms[i];
                bool isLocalPlayerShip = IsLocalPlayerShip(entity, localShipEntity);
                float scale = Mathf.Max(0.25f, lt.Scale) * shipVisualScale;

                int networkId = 0;
                if (em.HasComponent<GhostOwner>(entity))
                    networkId = em.GetComponentData<GhostOwner>(entity).NetworkId;

                // [TITAN-ORBIT] Do not spawn a hybrid hull for the local NetworkId until team confirm.
                if (suppressOwnedVisuals && localNetworkId > 0 && networkId == localNetworkId)
                {
                    if (_proxies.ContainsKey(entity))
                        DestroyProxy(entity);
                    continue;
                }

                TeamId team = TeamId.None;
                int shipLevel = 1;
                if (em.HasComponent<ShipState>(entity))
                {
                    var ship = em.GetComponentData<ShipState>(entity);
                    team = ship.Team;
                    shipLevel = Mathf.Max(1, ship.ShipLevel);
                }

                if (_proxies.TryGetValue(entity, out var existing) && existing != null)
                {
                    _proxyShipLevels.TryGetValue(entity, out int lastLevel);
                    _proxyTeams.TryGetValue(entity, out TeamId lastTeam);
                    if (lastLevel == shipLevel && lastTeam == team)
                        continue;

                    DestroyProxy(entity);
                }

                float muzzleOffset = defaultMuzzleOffset;
                if (em.HasComponent<ShipWeaponConfig>(entity))
                    muzzleOffset = em.GetComponentData<ShipWeaponConfig>(entity).MuzzleOffset;

                var go = CreateShipProxy(entity, networkId, team, shipLevel, scale, muzzleOffset);
                if (TryGetPresentationTransform(entity, em, out var presentLt))
                    ApplyShipProxyTransform(entity, em, isLocalPlayerShip, presentLt, go.transform, scale);
            }
        }

        /// <summary>
        /// Per-frame ship proxy pose sync from presentation cache. Skips transform during moon-dock cinematic.
        /// Registers weapon mounts with ShipWeaponProxyRegistry by network id.
        /// </summary>
        void SyncShipProxyTransforms(EntityManager em, HashSet<Entity> alive)
        {
            TryResolveLocalPlayerShipEntityCached(em, out var localShipEntity);
            int localNetworkId = EcsGameBridge.GetLocalNetworkId();
            bool suppressOwnedVisuals = ClientTeamFlowState.ShouldSuppressLocalPlayerControl();

            using var query = em.CreateEntityQuery(
                ComponentType.ReadOnly<ShipTag>(),
                ComponentType.ReadOnly<LocalTransform>());
            using var entities = query.ToEntityArray(Unity.Collections.Allocator.Temp);

            for (int i = 0; i < entities.Length; i++)
            {
                var entity = entities[i];
                alive.Add(entity);

                int networkId = 0;
                if (em.HasComponent<GhostOwner>(entity))
                    networkId = em.GetComponentData<GhostOwner>(entity).NetworkId;

                // [TITAN-ORBIT] Tear down leftover owned proxies while Join Team / rejoin is pending.
                if (suppressOwnedVisuals && localNetworkId > 0 && networkId == localNetworkId)
                {
                    if (_proxies.ContainsKey(entity))
                        DestroyProxy(entity);
                    continue;
                }

                if (!_proxies.TryGetValue(entity, out var go) || go == null)
                    continue;

                if (!TryGetPresentationTransform(entity, em, out var lt))
                    continue;

                float scale = Mathf.Max(0.25f, lt.Scale) * shipVisualScale;

                bool skipTransformSync = false;
                var moonDockVisual = go.GetComponent<ShipMoonDockVisualApplier>();
                if (moonDockVisual != null)
                    skipTransformSync = moonDockVisual.ShouldSkipTransformSync;
                else if (em.HasComponent<ShipMoonDockState>(entity))
                {
                    var moonDock = em.GetComponentData<ShipMoonDockState>(entity);
                    bool approachReady = moonDock.LandingApproachDelay + 0.0001f >= GemEconomyConstants.MoonLandingApproachDelaySeconds;
                    skipTransformSync = moonDock.MoonPlanetId != 0
                        && approachReady
                        && moonDock.LandingProgress > 0.001f;
                }

                bool isLocalPlayerShip = IsLocalPlayerShip(entity, localShipEntity);
                if (isLocalPlayerShip)
                    LocalPlayerShipProxy = go;

                if (!skipTransformSync)
                    ApplyShipProxyTransform(entity, em, isLocalPlayerShip, lt, go.transform, scale);
                else if (isLocalPlayerShip)
                {
                    ShipDisplayPose.SetLocalPose(go.transform.position, go.transform.rotation);
                }

                if (em.HasComponent<ShipState>(entity))
                {
                    var ship = em.GetComponentData<ShipState>(entity);
                    _proxyShipLevels[entity] = Mathf.Max(1, ship.ShipLevel);
                    _proxyTeams[entity] = ship.Team;
                    if (ship.IsDead)
                        go.SetActive(false);
                    else if (!go.activeSelf)
                        go.SetActive(true);
                }

                if (networkId > 0)
                {
                    _proxyNetworkIds.TryGetValue(entity, out int existingId);
                    if (existingId != networkId)
                    {
                        if (existingId > 0)
                            ShipWeaponProxyRegistry.Unregister(existingId, go.transform);
                        ShipWeaponProxyRegistry.Register(networkId, go.transform);
                        _proxyNetworkIds[entity] = networkId;
                    }
                }
            }
        }

        /// <summary>
        /// Instantiates ship visual hierarchy with bank, moon-dock, propulsion, and attribute-scale appliers bound to ECS entity.
        /// </summary>
        GameObject CreateShipProxy(Entity entity, int networkId, TeamId team, int shipLevel, float scale, float muzzleOffset)
        {
            GameObject go;
            if (ShipVisualApplier.TryCreateShipVisual(shipFamily, shipVisualPrefab, team, shipLevel, out go))
            {
                go.name = "ShipTagProxy";
            }
            else
            {
                go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                go.name = "ShipTagProxy";
                var col = go.GetComponent<Collider>();
                if (col != null) Destroy(col);
                var renderer = go.GetComponent<Renderer>();
                if (renderer != null)
                {
                    renderer.material = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard"));
                    renderer.material.color = team.ToColor();
                }
            }

            ShipWeaponMountCollector.EnsureWeaponMountsOnHierarchy(go.transform, muzzleOffset);
            ShipWingTractorBeamCollector.EnsureWingTractorBeamsOnHierarchy(go.transform);

            if (networkId > 0)
            {
                ShipWeaponProxyRegistry.Register(networkId, go.transform);
                _proxyNetworkIds[entity] = networkId;
            }

            _proxyShipLevels[entity] = shipLevel;
            _proxyTeams[entity] = team;
            _proxies[entity] = go;

            var bankVisual = go.GetComponent<ShipBankVisualApplier>();
            if (bankVisual == null)
                bankVisual = go.AddComponent<ShipBankVisualApplier>();
            bankVisual.Bind(entity);

            var moonDockVisual = go.GetComponent<ShipMoonDockVisualApplier>();
            if (moonDockVisual == null)
                moonDockVisual = go.AddComponent<ShipMoonDockVisualApplier>();
            moonDockVisual.Bind(entity, scale);

            var propulsionVisual = go.GetComponent<ShipPropulsionVisualApplier>();
            if (propulsionVisual == null)
                propulsionVisual = go.AddComponent<ShipPropulsionVisualApplier>();
            string familyPrefix = shipFamily != null ? shipFamily.familyId : "AstroEagle";
            propulsionVisual.Bind(entity, familyPrefix, propulsionVfxSettings);

            var attributeScaleVisual = go.GetComponent<ShipComponentAttributeScaleApplier>();
            if (attributeScaleVisual == null)
                attributeScaleVisual = go.AddComponent<ShipComponentAttributeScaleApplier>();
            attributeScaleVisual.Bind(entity, familyPrefix, shipFamily);

            return go;
        }

        /// <summary>[EDITOR] Default ship family asset when inspector field is empty.</summary>
        static ShipFamilyDefinition LoadDefaultShipFamily()
        {
#if UNITY_EDITOR
            return UnityEditor.AssetDatabase.LoadAssetAtPath<ShipFamilyDefinition>(DefaultShipFamilyAssetPath);
#else
            return null;
#endif
        }

        /// <summary>[EDITOR] Loads a GameObject prefab from project path for Awake defaults.</summary>
        static GameObject LoadDefaultPrefab(string assetPath)
        {
#if UNITY_EDITOR
            return UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
#else
            return null;
#endif
        }

        /// <summary>Tears down proxy GameObject and clears all per-entity registry entries.</summary>
        void DestroyProxy(Entity entity)
        {
            if (entity == _cachedDedicatedLocalShipEntity)
                _cachedDedicatedLocalShipEntity = Entity.Null;

            // --- Drop toroidal tile memory for this entity ---
            ToroidalDisplay.RemoveEntity(entity);

            if (_proxies.TryGetValue(entity, out var go))
            {
                if (_proxyNetworkIds.TryGetValue(entity, out int networkId) && go != null)
                    ShipWeaponProxyRegistry.Unregister(networkId, go.transform);
                if (go != null)
                    Destroy(go);
                _proxies.Remove(entity);
                _proxyNetworkIds.Remove(entity);
                _proxyShipLevels.Remove(entity);
                _proxyTeams.Remove(entity);
                _proxyPlanetVisuals.Remove(entity);
                _bulletStretchVisuals.Remove(entity);
            }
        }

        /// <summary>
        /// Consumes server-authoritative <see cref="BulletHitEventElement"/> buffer and spawns impact VFX.
        /// Clears buffer after processing — events are one-shot per sim tick batch.
        /// </summary>
        void ProcessBulletHitEvents(EntityManager em)
        {
            using var query = em.CreateEntityQuery(ComponentType.ReadOnly<ActiveBulletsTag>());
            if (query.CalculateEntityCount() == 0)
                return;

            var bulletEntity = query.GetSingletonEntity();
            if (!em.HasBuffer<BulletHitEventElement>(bulletEntity))
                return;

            var hits = em.GetBuffer<BulletHitEventElement>(bulletEntity);
            for (int i = 0; i < hits.Length; i++)
            {
                var hit = hits[i];
                var team = (TeamId)hit.OwnerTeam;
                int bankIndex = hit.BankIndex >= 0 ? hit.BankIndex : defaultBulletBankIndex;
                float scaleMul = hit.ScaleMultiplier > 0f ? hit.ScaleMultiplier : defaultBulletScaleMultiplier;
                // --- Impact VFX on nearest tile to local ship (classic display) ---
                Vector3 hitPos = hit.HitPosition;
                if (ToroidalDisplay.TryGetReferencePosition(out var reference))
                    hitPos = ToroidalDisplay.ToDisplayPosition(hitPos, reference);
                BulletVisualFactory.SpawnBulletImpactVfx(
                    hitPos,
                    bulletVfxBank,
                    bankIndex,
                    team,
                    hit.Damage,
                    scaleMul);
            }

            hits.Clear();
        }

        /// <summary>
        /// People-transport float proxies — client-local presentation entities (not ghosts).
        /// Runs under TransformQuarantine (session-long) just like hybrid ships; only skipped while Settling.
        /// </summary>
        void DrawPeopleTransports(EntityManager em, HashSet<Entity> alive)
        {
            using var presentationQuery = em.CreateEntityQuery(
                ComponentType.ReadOnly<PeopleTransportPresentationTag>(),
                ComponentType.ReadOnly<PeopleTransportPresentation>(),
                ComponentType.ReadOnly<LocalTransform>());
            if (presentationQuery.IsEmptyIgnoreFilter)
                return;

            using var entities = presentationQuery.ToEntityArray(Unity.Collections.Allocator.Temp);
            using var presentations = presentationQuery.ToComponentDataArray<PeopleTransportPresentation>(Unity.Collections.Allocator.Temp);

            for (int i = 0; i < entities.Length; i++)
            {
                var entity = entities[i];
                alive.Add(entity);
                var state = presentations[i];
                // Prefer presentation cache; fall back to entity LocalTransform (spawn frame).
                if (!TryGetPeopleTransportPresentationTransform(entity, em, out var lt))
                    continue;
                float scale = PeopleTransportVisualApplier.ComputeWorldScale(math.max(1f, state.Amount));
                var team = (TeamId)state.Team;

                if (!_proxies.TryGetValue(entity, out var go) || go == null)
                {
                    go = PeopleTransportVisualApplier.CreateVisual(peopleTransportVisualPrefab, state.Amount, team);
                    _proxies[entity] = go;
                }

                var pos = GetVisualPosition(entity, lt.Position);
                ApplyProxyTransform(pos, lt.Rotation, go.transform, scale);
            }
        }

        /// <summary>Applies team color materials to primitive fallback visuals.</summary>
        static void ApplyTeamColorToVisual(GameObject go, TeamId team)
        {
            var color = TeamColor(team);
            foreach (var renderer in go.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null) continue;
                renderer.material = WorldBodyVisualApplier.CreateLitMaterial(color);
            }
        }

        /// <summary>
        /// Bullet tracer display position — unwrap toward local ship so tracers crossing a seam
        /// stay near the combat they belong to.
        /// </summary>
        Vector3 GetBulletVisualPosition(Entity entity, EntityManager em, float3 logicalPos) =>
            GetVisualPosition(entity, em, logicalPos);

        Vector3 GetBulletVisualPosition(float3 logicalPos)
        {
            if (!_hasToroidalReference && !ToroidalDisplay.TryGetReferencePosition(out _toroidalReference))
                return logicalPos;
            _hasToroidalReference = true;
            return ToroidalDisplay.ToDisplayPosition(logicalPos, _toroidalReference);
        }

        /// <summary>
        /// Spawns bullet tracer GameObjects, muzzle VFX on first sighting, and stretch-trail progress each frame.
        /// </summary>
        void DrawBullets(EntityManager em, HashSet<Entity> alive)
        {
            using var query = em.CreateEntityQuery(
                ComponentType.ReadOnly<BulletTracerState>(),
                ComponentType.ReadOnly<LocalTransform>());
            using var entities = query.ToEntityArray(Unity.Collections.Allocator.Temp);
            using var tracers = query.ToComponentDataArray<BulletTracerState>(Unity.Collections.Allocator.Temp);
            using var transforms = query.ToComponentDataArray<LocalTransform>(Unity.Collections.Allocator.Temp);

            for (int i = 0; i < entities.Length; i++)
            {
                var entity = entities[i];
                alive.Add(entity);
                var tracer = tracers[i];
                var lt = transforms[i];
                var team = (TeamId)tracer.OwnerTeam;
                int bankIndex = tracer.BankIndex >= 0 ? tracer.BankIndex : defaultBulletBankIndex;
                float scaleMul = tracer.ScaleMultiplier > 0f ? tracer.ScaleMultiplier : defaultBulletScaleMultiplier;
                float3 velocity = tracer.Velocity;
                float bulletSpeed = math.length(velocity);

                if (!_proxies.TryGetValue(entity, out var go) || go == null)
                {
                    go = new GameObject("BulletTracer");
                    _proxies[entity] = go;

                    Vector3 spawnPos = GetBulletVisualPosition(entity, em, tracer.SpawnPosition);
                    Vector3 vel = velocity;
                    BulletVisualFactory.PlayMuzzleVfx(
                        spawnPos,
                        vel,
                        bulletVfxBank,
                        bankIndex,
                        team,
                        scaleMul,
                        bulletSpeed);
                    AudioManager.Instance?.PlayWeaponShootSound(
                        BulletVisualFactory.GetProjectileSoundPitchBySpeed(bulletSpeed));

                    GameObject visual = BulletVisualFactory.BuildVisual(
                        go.transform,
                        bulletVfxBank,
                        bankIndex,
                        team,
                        BulletShape.Sphere,
                        scaleMul,
                        bulletSpeed,
                        noTrail: false);

                    if (bulletVfxBank != null
                        && bulletVfxBank.TryGetProfile(bankIndex, out var profile)
                        && profile != null
                        && profile.TryGetStretchLengthFactors(out float startFactor, out float endFactor)
                        && ClientBulletStretchVisual.TryAttach(go.transform, visual, startFactor, endFactor))
                    {
                        _bulletStretchVisuals[entity] = go.GetComponent<ClientBulletStretchVisual>();
                    }
                }

                go.transform.position = GetBulletVisualPosition(entity, em, lt.Position);
                if (math.lengthsq(velocity) > 0.0001f)
                    go.transform.rotation = Quaternion.LookRotation(((Vector3)velocity).normalized, Vector3.up);

                if (_bulletStretchVisuals.TryGetValue(entity, out var stretch) && stretch != null)
                {
                    float travelled = math.distance(tracer.SpawnPosition, tracer.Position);
                    float progress = travelled / math.max(0.5f, tracer.MaxDistance);
                    stretch.ApplyTravelProgress(progress);
                }
            }
        }

        /// <summary>Planet proxies — rebuild when home/team/level/id key changes; else position-only update.</summary>
        void DrawPlanets(EntityManager em, HashSet<Entity> alive)
        {
            using var query = em.CreateEntityQuery(
                ComponentType.ReadOnly<PlanetTag>(),
                ComponentType.ReadOnly<PlanetState>(),
                ComponentType.ReadOnly<LocalTransform>());
            using var entities = query.ToEntityArray(Unity.Collections.Allocator.Temp);
            using var states = query.ToComponentDataArray<PlanetState>(Unity.Collections.Allocator.Temp);
            using var transforms = query.ToComponentDataArray<LocalTransform>(Unity.Collections.Allocator.Temp);

            for (int i = 0; i < entities.Length; i++)
            {
                var entity = entities[i];
                var state = states[i];
                var lt = transforms[i];
                float scale = math.max(0.25f, lt.Scale);

                var key = new PlanetVisualKey
                {
                    IsHome = state.IsHomePlanet,
                    Team = state.Ownership,
                    PlanetLevel = state.PlanetLevel,
                    PlanetId = state.PlanetId,
                };

                if (_proxies.TryGetValue(entity, out var go) && go != null)
                {
                    _proxyPlanetVisuals.TryGetValue(entity, out var existingKey);
                    if (existingKey.Equals(key))
                    {
                        // Existing proxy — always keep alive and update pose.
                        alive.Add(entity);
                        go.transform.position = GetVisualPosition(entity, lt.Position);
                        go.transform.rotation = lt.Rotation;
                        go.transform.localScale = Vector3.one * scale;
                        continue;
                    }

                    DestroyProxy(entity);
                }

                // --- Rate-limit new Instantiates during join settle ---
                // [TITAN-ORBIT] Skip create this frame; entity stays without a proxy until budget allows.
                // Do NOT add to alive until a proxy exists (teardown only destroys existing proxies).
                if (!TryConsumeWorldBodyProxyBudget())
                    continue;

                if (!WorldBodyVisualApplier.TryCreatePlanetVisual(
                        homePlanetVisualPrefab,
                        neutralPlanetVisualPrefab,
                        planetMaterialPool,
                        state.IsHomePlanet,
                        state.Ownership,
                        state.PlanetLevel,
                        state.PlanetId,
                        scale,
                        out go))
                {
                    go = CreatePrimitivePlanetProxy(state.Ownership);
                    _proxies[entity] = go;
                }
                else
                {
                    _proxies[entity] = go;
                }

                alive.Add(entity);
                _proxyPlanetVisuals[entity] = key;
                go.transform.position = GetVisualPosition(entity, lt.Position);
                go.transform.rotation = lt.Rotation;
                go.transform.localScale = Vector3.one * scale;
            }
        }

        /// <summary>Asteroid proxies — prefab or primitive sphere with spin visual helper.</summary>
        void DrawAsteroids(EntityManager em, HashSet<Entity> alive)
        {
            using var query = em.CreateEntityQuery(
                ComponentType.ReadOnly<AsteroidTag>(),
                ComponentType.ReadOnly<LocalTransform>());
            using var entities = query.ToEntityArray(Unity.Collections.Allocator.Temp);
            using var transforms = query.ToComponentDataArray<LocalTransform>(Unity.Collections.Allocator.Temp);

            for (int i = 0; i < entities.Length; i++)
            {
                var entity = entities[i];
                var lt = transforms[i];
                float scale = math.max(0.25f, lt.Scale);

                if (_proxies.TryGetValue(entity, out var go) && go != null)
                {
                    alive.Add(entity);
                    go.transform.position = GetVisualPosition(entity, lt.Position);
                    go.transform.localScale = Vector3.one * scale;
                    continue;
                }

                // --- Rate-limit new Instantiates during join settle ---
                if (!TryConsumeWorldBodyProxyBudget())
                    continue;

                if (!WorldBodyVisualApplier.TryCreateAsteroidVisual(
                        asteroidVisualPrefab,
                        lt.Position,
                        scale,
                        out go))
                {
                    go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    go.name = "AsteroidTagProxy";
                    var col = go.GetComponent<Collider>();
                    if (col != null) Destroy(col);
                    var renderer = go.GetComponent<Renderer>();
                    if (renderer != null)
                    {
                        renderer.material = WorldBodyVisualApplier.CreateLitMaterial(new Color(0.55f, 0.45f, 0.35f));
                    }

                    WorldBodyVisualApplier.EnsureAsteroidSpin(go, lt.Position);
                }

                _proxies[entity] = go;
                alive.Add(entity);
                go.transform.position = GetVisualPosition(entity, lt.Position);
                go.transform.localScale = Vector3.one * scale;
            }
        }

        /// <summary>Gem proxies — value-scaled via <see cref="GemVisualApplier"/>; registers diameter for tractor beam.</summary>
        void DrawGems(EntityManager em, HashSet<Entity> alive)
        {
            using var query = em.CreateEntityQuery(
                ComponentType.ReadOnly<GemTag>(),
                ComponentType.ReadOnly<GemState>(),
                ComponentType.ReadOnly<LocalTransform>());
            using var entities = query.ToEntityArray(Unity.Collections.Allocator.Temp);
            using var states = query.ToComponentDataArray<GemState>(Unity.Collections.Allocator.Temp);
            using var transforms = query.ToComponentDataArray<LocalTransform>(Unity.Collections.Allocator.Temp);

            for (int i = 0; i < entities.Length; i++)
            {
                var entity = entities[i];
                var state = states[i];
                var lt = transforms[i];
                float scale = GemVisualApplier.ComputeVisualScale(math.max(0.25f, state.Value));

                if (_proxies.TryGetValue(entity, out var go) && go != null)
                {
                    alive.Add(entity);
                    go.transform.position = GetVisualPosition(entity, lt.Position);
                    go.transform.rotation = lt.Rotation;
                    go.transform.localScale = Vector3.one * scale;
                    GemVisualDiameterRegistry.SetDiameter(entity, GemVisualApplier.ReadWorldDiameter(go, state.Value));
                    continue;
                }

                // --- Rate-limit new Instantiates during join settle ---
                if (!TryConsumeWorldBodyProxyBudget())
                    continue;

                if (!GemVisualApplier.TryCreateGemVisual(gemVisualPrefab, state.Value, out go))
                {
                    go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    go.name = "GemTagProxy";
                    var col = go.GetComponent<Collider>();
                    if (col != null) Destroy(col);
                    var renderer = go.GetComponent<Renderer>();
                    if (renderer != null)
                        renderer.material = WorldBodyVisualApplier.CreateLitMaterial(Color.yellow);
                }

                _proxies[entity] = go;
                alive.Add(entity);
                go.transform.position = GetVisualPosition(entity, lt.Position);
                go.transform.rotation = lt.Rotation;
                go.transform.localScale = Vector3.one * scale;
                GemVisualDiameterRegistry.SetDiameter(entity, GemVisualApplier.ReadWorldDiameter(go, state.Value));
            }
        }

        /// <summary>
        /// Per-frame GO Instantiates cap — tighter while Settling (GhostSpawn Instantiates 1/frame).
        /// </summary>
        static int GetWorldBodyProxyBudgetThisFrame()
        {
            return ClientJoinSettleCache.Settling
                ? MaxNewWorldBodyProxiesWhileSettling
                : MaxNewWorldBodyProxiesPerFrame;
        }

        /// <summary>
        /// Returns true when a new world-body proxy Instantiates is allowed this frame
        /// (shared Pending drain + post-settle Draw* catch-up).
        /// </summary>
        bool TryConsumeWorldBodyProxyBudget()
        {
            if (_newWorldBodyProxiesThisFrame >= GetWorldBodyProxyBudgetThisFrame())
                return false;

            _newWorldBodyProxiesThisFrame++;
            return true;
        }

        /// <summary>Colored primitive sphere fallback when planet prefab pipeline fails.</summary>
        static GameObject CreatePrimitivePlanetProxy(TeamId ownership)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "PlanetTagProxy";
            var col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);
            var renderer = go.GetComponent<Renderer>();
            if (renderer != null)
            {
                Color color = ownership == TeamId.None
                    ? new Color(0.35f, 0.55f, 1f)
                    : ownership.ToColor();
                renderer.material = WorldBodyVisualApplier.CreateLitMaterial(color);
            }

            return go;
        }

        /// <summary>Generic tagged-entity primitive drawer — legacy helper for simple debug proxies.</summary>
        void DrawTagged<T>(EntityManager em, HashSet<Entity> alive, PrimitiveType primitive, Color color, float scaleMul)
            where T : unmanaged, IComponentData
        {
            using var query = em.CreateEntityQuery(ComponentType.ReadOnly<T>(), ComponentType.ReadOnly<LocalTransform>());
            using var entities = query.ToEntityArray(Unity.Collections.Allocator.Temp);
            using var transforms = query.ToComponentDataArray<LocalTransform>(Unity.Collections.Allocator.Temp);

            for (int i = 0; i < entities.Length; i++)
            {
                var entity = entities[i];
                alive.Add(entity);
                var lt = transforms[i];
                float scale = Mathf.Max(0.25f, lt.Scale) * scaleMul;

                if (!_proxies.TryGetValue(entity, out var go) || go == null)
                {
                    go = GameObject.CreatePrimitive(primitive);
                    go.name = typeof(T).Name + "Proxy";
                    var col = go.GetComponent<Collider>();
                    if (col != null) Destroy(col);
                    var renderer = go.GetComponent<Renderer>();
                    if (renderer != null)
                    {
                        renderer.material = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard"));
                        renderer.material.color = color;
                    }
                    _proxies[entity] = go;
                }

                go.transform.position = GetVisualPosition(entity, lt.Position);
                go.transform.rotation = lt.Rotation;
                go.transform.localScale = Vector3.one * scale;
            }
        }

        /// <summary>Hard-coded team palette for primitive fallback renderers.</summary>
        static Color TeamColor(TeamId team)
        {
            switch (team)
            {
                case TeamId.TeamA: return new Color(1f, 0.35f, 0.35f);
                case TeamId.TeamB: return new Color(0.35f, 0.75f, 1f);
                case TeamId.TeamC: return new Color(0.45f, 1f, 0.45f);
                default: return Color.white;
            }
        }

        /// <summary>[UNITY] Unregisters weapon mounts and destroys all proxies on scene teardown.</summary>
        void OnDestroy()
        {
            foreach (var kv in _proxies)
            {
                if (_proxyNetworkIds.TryGetValue(kv.Key, out int networkId) && kv.Value != null)
                    ShipWeaponProxyRegistry.Unregister(networkId, kv.Value.transform);
                if (kv.Value != null)
                    Destroy(kv.Value);
            }
            _proxies.Clear();
            _proxyNetworkIds.Clear();
            _proxyShipLevels.Clear();
            _proxyTeams.Clear();
            _proxyPlanetVisuals.Clear();
        }
    }
}
