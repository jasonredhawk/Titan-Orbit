using System.Collections.Generic;
using Stopwatch = System.Diagnostics.Stopwatch;
using TitanOrbit;
using TitanOrbit.Audio;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Entities;
using TitanOrbit.Generation;
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
        /// <summary>
        /// Level-1 presentation multiplier on top of ECS <see cref="LocalTransform.Scale"/>.
        /// [TITAN-ORBIT] Tier growth is on <c>LocalTransform.Scale</c> (+10%/ship level via
        /// <see cref="BodyCollisionMath.GetShipTierScale"/>) — final draw scale is Scale × this.
        /// </summary>
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
        /// <summary>
        /// Last applied upgrade-tree branch — must rebuild when branch changes at the same level
        /// (debug free-tree / hull swap), not only when ShipLevel changes.
        /// </summary>
        readonly Dictionary<Entity, int> _proxyBranchIndices = new Dictionary<Entity, int>();
        /// <summary>
        /// Last applied chassis id string — exact hull identity for hybrid proxies under TransformQuarantine.
        /// </summary>
        readonly Dictionary<Entity, string> _proxyChassisIds = new Dictionary<Entity, string>();
        /// <summary>Last applied team — triggers material swap on capture.</summary>
        readonly Dictionary<Entity, TeamId> _proxyTeams = new Dictionary<Entity, TeamId>();
        /// <summary>Planet visual identity — rebuild when home/team/level/id changes.</summary>
        readonly Dictionary<Entity, PlanetVisualKey> _proxyPlanetVisuals = new Dictionary<Entity, PlanetVisualKey>();

        /// <summary>
        /// Last applied display tint team per asteroid proxy (after overlap → prefer-local resolve).
        /// Skip GetComponent/tint work every SyncAllProxies when unchanged.
        /// </summary>
        readonly Dictionary<Entity, TeamId> _proxyAsteroidTerritory = new Dictionary<Entity, TeamId>();

        /// <summary>
        /// [TITAN-ORBIT] Client topology revision last used for asteroid tint PIT.
        /// Full PIT for every rock on every PublishClient was ~2+ ms — revision + budget instead.
        /// </summary>
        int _lastAsteroidTintGraphRevision = int.MinValue;

        /// <summary>Viewer team latched with <see cref="_lastAsteroidTintGraphRevision"/>.</summary>
        TeamId _lastAsteroidTintViewerTeam = TeamId.None;

        /// <summary>Max asteroid territory PIT evaluations per visual sync frame.</summary>
        const int MaxAsteroidTerritoryTintsPerFrame = 24;

        /// <summary>Cached local ship entity for dedicated-client weapon VFX (avoids per-frame query).</summary>
        Entity _cachedDedicatedLocalShipEntity;
        /// <summary>Cached local ship entity for transform sync and camera pose feed.</summary>
        Entity _cachedLocalPlayerShipEntity;

        /// <summary>Guards VR / multi-camera double onBeforeRender in the same frame.</summary>
        int _lastVisualSyncFrame = -1;

        /// <summary>
        /// [TITAN-ORBIT] Process-wide latch so duplicate EcsWorldVisualizer instances cannot both
        /// SyncAllProxies the same frame (duplicate instances were interleaving work).
        /// </summary>
        static int s_GlobalVisualSyncFrame = -1;

        /// <summary>
        /// [TITAN-ORBIT] Local-ship (or camera) XZ used as toroidal display reference this frame.
        /// Remotes and world bodies unwrap toward this point so seams stay seamless.
        /// </summary>
        Vector3 _toroidalReference;

        /// <summary>True when <see cref="_toroidalReference"/> was resolved for the current sync.</summary>
        bool _hasToroidalReference;

        /// <summary>
        /// Planet id the local ship is orbiting or moon-docked on this frame (0 = none).
        /// That planet uses force-nearest display tiles so the ring does not lag across seams.
        /// </summary>
        int _forceNearestPlanetId;

        /// <summary>
        /// New planet/asteroid/gem proxies created this frame (reset in SyncAllProxies).
        /// Used with <see cref="ClientJoinSettleCache.Settling"/> to rate-limit Instantiates.
        /// </summary>
        int _newWorldBodyProxiesThisFrame;

        /// <summary>
        /// Asteroid entities already handled for kill hide (HitRpc or IsDestroyed detect).
        /// Prevents double-hide work. Gem visuals come from networked gem Instantiates only.
        /// </summary>
        readonly HashSet<Entity> _asteroidBurstFired = new HashSet<Entity>();

        /// <summary>
        /// Last known asteroid pose/value so we can burst even if the ghost despawns before
        /// the client observes <see cref="AsteroidState.IsDestroyed"/>.
        /// </summary>
        readonly Dictionary<Entity, AsteroidBurstCache> _asteroidLastKnown = new Dictionary<Entity, AsteroidBurstCache>();

        /// <summary>
        /// Cached visual kind per proxy — avoids per-frame <c>string.IndexOf</c> over ~300 names
        /// (was a major SyncAllProxies cost in debug session 74383c).
        /// </summary>
        readonly Dictionary<Entity, ProxyVisualKind> _proxyKinds = new Dictionary<Entity, ProxyVisualKind>();

        /// <summary>Asteroid proxy keys only — DetectAsteroidGemBursts must not walk ships/planets/gems.</summary>
        readonly HashSet<Entity> _asteroidProxyEntities = new HashSet<Entity>();

        /// <summary>Reused each sync — allocating a 300+ entity HashSet every frame caused GC hitch.</summary>
        readonly HashSet<Entity> _aliveScratch = new HashSet<Entity>();

        /// <summary>Reused prune list for dead proxy entities.</summary>
        readonly List<Entity> _removeScratch = new List<Entity>(64);

        /// <summary>Incremental world-body count (planet/asteroid/gem/transport) — no full recount/frame.</summary>
        int _worldBodyProxyCountCached;

        /// <summary>Incremental planet+asteroid count for the loading bar.</summary>
        int _mapLoadingProxyCountCached;

        /// <summary>Proxy category for hot-path sync / counts / destroy.</summary>
        enum ProxyVisualKind : byte
        {
            Other = 0,
            Planet = 1,
            Asteroid = 2,
            Gem = 3,
            Ship = 4,
            PeopleTransport = 5,
        }

        struct AsteroidBurstCache
        {
            public float3 Position;
            public float RemainingGems;
        }

        /// <summary>Local-player ship proxy on dedicated clients / host ClientWorld viz.</summary>
        public GameObject LocalPlayerShipProxy { get; private set; }

        /// <summary>
        /// [HYBRID] Live visual hull Transform for local muzzle VFX (updated in LateUpdate before
        /// <see cref="ClientLocalBulletVfxBridge"/> / <see cref="BulletVfxDriver"/>).
        /// </summary>
        public static Transform LocalPlayerShipVisualRoot { get; private set; }

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
            // [TITAN-ORBIT] Gem visuals rent from a pool — prewarm so destroy bursts do not Instantiates.
            GemVisualPool.EnsurePrefab(gemVisualPrefab);
            GemVisualPool.Prewarm(GemVisualPool.DefaultPrewarmCount);
            if (peopleTransportVisualPrefab == null)
                peopleTransportVisualPrefab = PeopleTransportVisualApplier.LoadDefaultPrefab();
            if (peopleTransportVisualPrefab == null)
                peopleTransportVisualPrefab = LoadDefaultPrefab(DefaultPeopleTransportPath);
            if (bulletVfxBank == null)
                bulletVfxBank = BulletVfxBank.LoadDefault();
            if (bulletVfxBank != null)
                BulletVisualScale.ActiveUpgradeVisualScaleMultiplier =
                    bulletVfxBank.UpgradeVisualScaleMultiplier;
            // --- Propulsion jet flames (player builds need Resources) ---
            // [TITAN-ORBIT] SampleScene often serializes an empty thrusterJetFlameBank. Awake must
            // load ModularJetFlame2 via Resources (Windows) — AssetDatabase-only defaults left
            // thrusters dark in player builds while Editor still showed flames.
            // Do not require engineVfxPrefab: defaults intentionally leave it null so only
            // Thruster_* mounts get aft-oriented flames.
            if (propulsionVfxSettings.thrusterJetFlameBank == null ||
                propulsionVfxSettings.thrusterJetFlameBank.Count == 0)
            {
                propulsionVfxSettings = ShipPropulsionVisualApplier.LoadDefaultSettings();
            }
            else
            {
                // [TITAN-ORBIT] Scene may serialize useThrusterVfxForAcceleration:0 / zeroed
                // transition knobs. Thrusters are always input-driven in code now; still repair
                // zeroed blend speed so flames do not pop or stick.
                propulsionVfxSettings.useThrusterVfxForAcceleration = true;
                if (propulsionVfxSettings.thrusterVfxTransitionSpeed <= 0.01f)
                    propulsionVfxSettings.thrusterVfxTransitionSpeed = 3f;
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

            // --- Territory triangle world drawer (Shapes) ---
            // [HYBRID] Reads PlanetConnectionGraphCache — no map-body ECS gathers.
            PlanetConnectionShapesVisual.EnsureExists();
        }

        /// <summary>[UNITY] Unsubscribe to avoid leaks when the visualizer is destroyed.</summary>
        void OnDisable()
        {
            Application.onBeforeRender -= OnBeforeRenderSync;
            if (Active == this)
                Active = null;

            // Force full asteroid tint recompute next enable (viewer team / graph may change).
            _lastAsteroidTintGraphRevision = int.MinValue;
            _lastAsteroidTintViewerTeam = TeamId.None;
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
        /// Copies asteroid hybrid-proxy entity keys into <paramref name="dst"/> (clears first).
        /// Used by phantom-collision debug scans — never a full ECS asteroid gather.
        /// </summary>
        public void CopyAsteroidProxyEntitiesTo(List<Entity> dst)
        {
            if (dst == null)
                return;
            dst.Clear();
            foreach (Entity e in _asteroidProxyEntities)
                dst.Add(e);
        }

        /// <summary>
        /// [HYBRID] Looks up the GameObject proxy for one entity (gem diameter, anchors, etc.).
        /// Dictionary only — no ECS gathers.
        /// </summary>
        public bool TryGetProxy(Entity entity, out GameObject proxy)
        {
            proxy = null;
            if (!_proxies.TryGetValue(entity, out proxy) || proxy == null)
            {
                proxy = null;
                return false;
            }

            return true;
        }

        /// <summary>
        /// Hides an asteroid hybrid proxy when <see cref="BulletHitRpc"/> reports Health after = 0.
        /// <para>
        /// [TITAN-ORBIT] Server destroys the rock the same tick it sets IsDestroyed; clients often
        /// never observe Health≤0 / IsDestroyed before ghost despawn. HitRpc is the reliable kill
        /// signal. Gem visuals wait for networked gem Instantiates — server owns pose/velocity;
        /// the client does not invent a local burst.
        /// </para>
        /// </summary>
        /// <param name="entity">Asteroid ghost entity whose proxy should hide.</param>
        /// <returns>True when a proxy was found and hidden (or already inactive).</returns>
        public bool TryHideAsteroidProxyFromHitRpc(Entity entity)
        {
            if (entity == Entity.Null || !_proxies.TryGetValue(entity, out var go) || go == null)
                return false;

            // Mark kill handled so DetectAsteroidGemBursts does not re-process this rock.
            _asteroidBurstFired.Add(entity);

            if (go.activeSelf)
                go.SetActive(false);
            // [TITAN-ORBIT] Cull ECS PhysicsCollider now — visual hide alone left a phantom hull
            // until NetCode despawn (ship bounce on empty space / pose step with stable FPS).
            ClientAsteroidCollisionCull.TryDisablePhysicsCollider(entity);

            // Gem GOs appear only after gem ghosts Instantiates (server GemKinematics / LocalTransform).
            return true;
        }

        /// <summary>
        /// Quarantine-safe planet lookup by <see cref="PlanetState.PlanetId"/>.
        /// Walks hybrid proxy keys only, then per-entity <c>HasComponent</c>/<c>GetComponentData</c> —
        /// never <c>ToEntityArray</c> / full planet archetype gathers (Crash!!! after Settling OFF).
        /// </summary>
        public bool TryGetPlanetPoseByPlanetId(
            EntityManager em,
            int planetId,
            out float3 position,
            out float scale,
            out PlanetState state)
        {
            position = default;
            scale = 1f;
            state = default;
            if (planetId == 0)
                return false;

            // Prefer the planet-visual registry (smaller) then fall back to all proxies.
            foreach (var kv in _proxyPlanetVisuals)
            {
                if (kv.Value.PlanetId != planetId)
                    continue;
                if (!TryReadPlanetPose(em, kv.Key, planetId, out position, out scale, out state))
                    continue;
                return true;
            }

            foreach (var kv in _proxies)
            {
                if (kv.Value == null)
                    continue;
                if (!TryReadPlanetPose(em, kv.Key, planetId, out position, out scale, out state))
                    continue;
                return true;
            }

            return false;
        }

        /// <summary>Per-entity planet read — safe under TransformQuarantine.</summary>
        static bool TryReadPlanetPose(
            EntityManager em,
            Entity entity,
            int planetId,
            out float3 position,
            out float scale,
            out PlanetState state)
        {
            position = default;
            scale = 1f;
            state = default;
            if (!em.Exists(entity) ||
                !em.HasComponent<PlanetTag>(entity) ||
                !em.HasComponent<PlanetState>(entity) ||
                !em.HasComponent<LocalTransform>(entity))
                return false;

            state = em.GetComponentData<PlanetState>(entity);
            if (state.PlanetId != planetId)
                return false;

            var lt = em.GetComponentData<LocalTransform>(entity);
            position = lt.Position;
            scale = math.max(0.25f, lt.Scale);
            return true;
        }

        /// <summary>
        /// [HYBRID] Per-frame proxy sync — reads presentation transforms, spawns/destroys GameObjects, applies VFX.
        /// Invoked from Application.onBeforeRender (not LateUpdate) so presentation cache is ready.
        /// </summary>
        void OnBeforeRenderSync()
        {
            TryBecomeActiveIfPreferred();
            if (Active != this)
                return;
            SyncAllProxies();
        }

        /// <summary>Fallback when onBeforeRender does not fire (some batch/headless paths).</summary>
        void LateUpdate()
        {
            TryBecomeActiveIfPreferred();
            if (Active != this)
                return;
            SyncAllProxies();
        }

        /// <summary>
        /// If Active is missing or empty while this instance owns hybrids, take Active.
        /// Prevents a leftover empty visualizer from starving the real map proxy owner.
        /// </summary>
        void TryBecomeActiveIfPreferred()
        {
            if (Active == this)
                return;
            if (Active == null || Active._proxies.Count == 0)
            {
                if (_proxies.Count > 0 || ClientJoinSettleCache.Settling)
                    Active = this;
            }
        }

        void SyncAllProxies()
        {
            // --- One sync per frame across all visualizer instances ---
            if (s_GlobalVisualSyncFrame == Time.frameCount)
                return;
            if (_lastVisualSyncFrame == Time.frameCount)
                return;
            s_GlobalVisualSyncFrame = Time.frameCount;
            _lastVisualSyncFrame = Time.frameCount;

            var world = PickVisualizationWorld();
            if (world == null || !world.IsCreated)
                return;

            var em = world.EntityManager;
            _newWorldBodyProxiesThisFrame = 0;
            // [TITAN-ORBIT] Reuse scratch set — SyncAllProxies hitched with ~320 proxies when
            // allocating a fresh HashSet every frame + string name scans.
            var alive = _aliveScratch;
            alive.Clear();
            bool settling = ClientJoinSettleCache.Settling;

            // --- Toroidal display: unbounded local ship; each body picks its own tile ---
            ToroidalDisplay.BeginFrame();
            ToroidalDisplay.SyncMapSize(em);
            _hasToroidalReference = ToroidalDisplay.TryGetReferencePosition(out _toroidalReference);
            // Planet the local ship is glued to (orbit ring / moon dock) — force-nearest tile.
            _forceNearestPlanetId = ResolveForceNearestPlanetId(em);

            // --- Map bodies: drain baked Pending / existing SpawnRequest only ---
            // [TITAN-ORBIT] Player.log 2026-07-18 21:18: MarkSpawnRequestQuery over unqueued
            // asteroids → ArchetypeChunk.GetNativeArray(EntityTypeHandle) NRE → Crash!!!
            // Same failure as the disabled ECS mark system. Do NOT backfill by scanning all
            // asteroids. Visuals require baked MapBodyHybridVisualPending on ghost prefabs
            // (rebake SubScenes / EntityScenes for the Windows player).
            //
            // Drain during Settling (budgeted) so GameObject Instantiates run under the loading
            // bar. Skipping drain until Settling OFF dumped all GO lag after 100% / Join Team.
            // Flush Instantiates-hook SpawnRequest queue first (Windows EntityScenes lack Pending).
            MapBodyHybridVisualInstantiateHook.FlushPending(em);
            // [TITAN-ORBIT] Gems Instantiated this frame — create GO immediately (bypass asteroid budget).
            DrainUrgentGemProxies(em, alive);
            // Immediate local gem explosion when client sees IsDestroyed (do not wait Instantiates).
            // Bullet kills already burst via HitRpc — this walks asteroid keys only (not all proxies).
            DetectAsteroidGemBursts(em);
            SyncExistingWorldBodyProxyTransforms(em, alive);
            DrainPendingWorldBodyProxies(em, alive);

            // --- Ships ---
            // [TITAN-ORBIT] TransformQuarantine: TransformSystemGroup stays OFF (RE-ENABLE Crash!!!).
            // Entities Graphics needs Parent/LTW — use hybrid ship GO proxies instead.
            bool hybridShips = ClientJoinSettleCache.TransformQuarantine ||
                               !TitanOrbitPresentationConfig.UseEntitiesGraphicsForShips;
            if (hybridShips)
            {
                // [TITAN-ORBIT] Both EnsureShipProxies AND SyncShipProxyTransforms use ship
                // ToEntityArray — Sync was previously ungated and Crash!!!'d after TeamChoice
                // (Player.log 2026-07-20) while Ensure alone was skipped. Gate both.
                if (!ClientJoinSettleCache.ShouldSkipShipEntityQueries)
                {
                    EnsureShipProxies(em);
                    SyncShipProxyTransforms(em, alive);
                }
                else
                {
                    // --- Local hull only during Instantiates backlog (no ship gather) ---
                    // [TITAN-ORBIT] After Join Team, map Instantiates can keep GhostSpawnBacklog
                    // true for a long time. Seeded local ship Instantiates must still get a hybrid
                    // GO + pose or the player sees a white fallback / stuck "Spawning your ship...".
                    EnsureAndSyncLocalSeededShipProxy(em, alive);
                }
            }

            // --- People transports ---
            // Owned by PeopleTransportVfxDriver (MonoBehaviour Instantiates from VFX bridge).
            // Do not DrawPeopleTransports here — ECS presentation path was unreliable under quarantine.

            // --- Bullets ---
            // [LEGACY] DrawBullets / ProcessBulletHitEvents retired — owned by BulletVfxDriver
            // (BulletSpawnRpc / BulletHitRpc + BulletVfxBridge). Do not re-enable ECS tracer draws
            // under TransformQuarantine (map gathers / Instantiates risk).

            // --- Proxy prune (quarantine-safe: dictionary only, no map-body ToEntityArray) ---
            {
                _removeScratch.Clear();
                foreach (var kv in _proxies)
                {
                    if (kv.Value == null || !em.Exists(kv.Key))
                        _removeScratch.Add(kv.Key);
                }

                for (int i = 0; i < _removeScratch.Count; i++)
                    DestroyProxy(_removeScratch[i]);

                if (!ClientJoinSettleCache.TransformQuarantine && !settling)
                    ToroidalDisplay.PruneStale(alive);
            }

            // Incremental counts maintained in RegisterProxyKind / DestroyProxy.
            WorldBodyProxyCount = _worldBodyProxyCountCached;
            MapLoadingProxyCount = _mapLoadingProxyCountCached;

        }

        /// <summary>
        /// Updates poses for world-body proxies already in <see cref="_proxies"/> without scanning
        /// every asteroid entity (safe during GhostSpawn Instantiates).
        /// Gems use ghosted <see cref="GemKinematics"/> for a short presentation extrapolate + lerp
        /// so glide looks continuous between NetCode snapshot samples.
        /// <para>
        /// [TITAN-ORBIT] Toroidal display positions are absolute and only change on tile switch
        /// (or when the logical ghost moves). Writing <c>transform.position</c> every frame while
        /// flying still dirties ~200+ Transforms and showed up as ~25 ms wall-clock hitches with
        /// unchanged tile switches. Skip unchanged writes.
        /// </para>
        /// </summary>
        void SyncExistingWorldBodyProxyTransforms(EntityManager em, HashSet<Entity> alive)
        {
            // --- Asteroid territory tint: only when topology / viewer team changes ---
            // [TITAN-ORBIT] Presentation triangles use planet centers (not moons), so revision
            // is stable between graph publishes. Running PIT for every rock every frame cost
            // ~2.3 ms with ~236 asteroids — budget + revision invalidate instead.
            TeamId viewerTeam = TeamId.None;
            if (TryResolveLocalPlayerShipEntityCached(em, out var localShip) &&
                localShip != Entity.Null &&
                em.Exists(localShip) &&
                em.HasComponent<ShipState>(localShip))
            {
                viewerTeam = em.GetComponentData<ShipState>(localShip).Team;
            }

            int graphRevision = PlanetConnectionGraphCache.ClientPublishRevision;
            if (graphRevision != _lastAsteroidTintGraphRevision ||
                viewerTeam != _lastAsteroidTintViewerTeam)
            {
                _lastAsteroidTintGraphRevision = graphRevision;
                _lastAsteroidTintViewerTeam = viewerTeam;
                // Invalidate applied tints so rocks re-enter the budgeted uncached queue.
                _proxyAsteroidTerritory.Clear();
            }

            int tintBudget = MaxAsteroidTerritoryTintsPerFrame;

            foreach (var kv in _proxies)
            {
                Entity entity = kv.Key;
                GameObject go = kv.Value;
                if (go == null || !em.Exists(entity) || !em.HasComponent<LocalTransform>(entity))
                    continue;

                // Ships/bullets have their own sync paths after settle.
                // [TITAN-ORBIT] Kind is cached at create — do not string-scan GO names every frame.
                if (!_proxyKinds.TryGetValue(entity, out var kind))
                    kind = ProxyVisualKind.Other;
                bool isGem = kind == ProxyVisualKind.Gem;
                bool isWorldBody = kind == ProxyVisualKind.Planet ||
                                   kind == ProxyVisualKind.Asteroid ||
                                   isGem ||
                                   kind == ProxyVisualKind.PeopleTransport;
                if (!isWorldBody)
                    continue;

                var lt = em.GetComponentData<LocalTransform>(entity);
                float scale = math.max(0.25f, lt.Scale);
                float gemValue = 0f;
                if (isGem && em.HasComponent<GemState>(entity))
                {
                    // --- Gem value scale + end-of-life shrink (original Gem.shrinkDuration) ---
                    var gemState = em.GetComponentData<GemState>(entity);
                    gemValue = gemState.Value;
                    // [TITAN-ORBIT] ServerTick clock — World.Time diverges on late-join (moon orbit rule).
                    float now = PlanetGemMoonOrbitClock.TryGetElapsedSeconds(out double tickNow, includeTickFraction: true)
                        ? (float)tickNow
                        : (float)Time.timeAsDouble;
                    scale = GemVisualApplier.ComputeLifetimeVisualScale(
                        gemValue, gemState.SpawnServerTime, now);
                }

                alive.Add(entity);

                // --- World-body / gem pose ---
                // Gems: GemClientMotionApplier owns position from ghosted LocalTransform + GemKinematics.
                // Other bodies: snap to toroidal display of LocalTransform.
                if (isGem)
                {
                    // Scale / diameter only — do not overwrite pose (that caused “frozen until ship moves”).
                    Vector3 gemScale = Vector3.one * scale;
                    if ((go.transform.localScale - gemScale).sqrMagnitude > 0.0001f)
                        go.transform.localScale = gemScale;
                    if (gemValue > 0f)
                        GemVisualDiameterRegistry.SetDiameter(entity, GemVisualApplier.ReadWorldDiameter(go, gemValue));
                    continue;
                }

                // --- Skip identical Transform writes (tile stable while ship flies) ---
                // [UNITY] Assigning the same position still marks the Transform dirty and cascades
                // into culling/rendering cost on the next frame.
                Vector3 displayPos = GetVisualPosition(entity, lt.Position);
                Transform t = go.transform;
                if ((t.position - displayPos).sqrMagnitude > 0.0001f)
                    t.position = displayPos;

                // --- Rotation: asteroids must NOT be overwritten ---
                // [TITAN-ORBIT] AsteroidSpinVisualProxy tumbles a child pivot (SgtPlanet migrated
                // under it). Writing ECS LocalTransform.Rotation onto the root is unnecessary and
                // used to fight root-level spin before the pivot migration. Planets also spin a
                // child pivot (PlanetSpinVisualProxy) — root rot can stay from ECS.
                if (kind != ProxyVisualKind.Asteroid)
                {
                    Quaternion displayRot = lt.Rotation;
                    if (Quaternion.Angle(t.rotation, displayRot) > 0.05f)
                        t.rotation = displayRot;
                }

                Vector3 displayScale = Vector3.one * scale;
                if ((t.localScale - displayScale).sqrMagnitude > 0.0001f)
                    t.localScale = displayScale;

                // --- Planet level / ownership visuals (rings, moon, materials) ---
                // [TITAN-ORBIT] DrawPlanets used to Destroy+recreate when PlanetVisualKey changed,
                // but SyncAllProxies never calls it under TransformQuarantine. Rings were stuck at
                // Instantiates-time Configure. Refresh in place from ghosted PlanetState — per known
                // proxy only (no planet ToEntityArray / map gather).
                if (kind == ProxyVisualKind.Planet)
                    RefreshPlanetProxyAppearanceIfChanged(em, entity, go, scale);

                // --- Asteroid territory tint (budgeted PIT) ---
                // Untinted / invalidated rocks only; MaxAsteroidTerritoryTintsPerFrame per sync.
                if (kind == ProxyVisualKind.Asteroid &&
                    !_proxyAsteroidTerritory.ContainsKey(entity) &&
                    tintBudget > 0)
                {
                    RefreshAsteroidTerritoryTintIfChanged(em, entity, go, lt.Position, viewerTeam);
                    tintBudget--;
                }
            }
        }

        /// <summary>
        /// Applies team territory highlight so rock colour matches the drawn territory fill.
        /// Uses <see cref="PlanetConnectionPresentationTriangles"/> (same Client graph + moon
        /// verts as <see cref="PlanetConnectionShapesVisual"/>). Overlap prefers the local
        /// player's team. Per known proxy only — no asteroid archetype gather.
        /// </summary>
        /// <param name="em">Client EntityManager.</param>
        /// <param name="entity">Asteroid ghost already in <see cref="_proxies"/>.</param>
        /// <param name="go">Hybrid asteroid GameObject root.</param>
        /// <param name="logicalPos">Ghost <see cref="LocalTransform.Position"/> (pre-display retile).</param>
        /// <param name="viewerTeam">Local player team (resolved once per sync by caller).</param>
        void RefreshAsteroidTerritoryTintIfChanged(
            EntityManager em, Entity entity, GameObject go, float3 logicalPos, TeamId viewerTeam)
        {
            if (go == null || !em.Exists(entity))
                return;

            // --- Canonical XZ (same space as moon verts / drawn fill topology) ---
            // [TITAN-ORBIT] Do not use display-retiled proxy position — wrap copies would
            // fail PIT against canonical triangle verts.
            Vector3 wrapped = ToroidalMap.WrapPosition(new Vector3(logicalPos.x, 0f, logicalPos.z));
            float3 canonical = new float3(wrapped.x, 0f, wrapped.z);

            PlanetConnectionPresentationTriangles.GetOwnershipAtPosition(
                canonical, out byte mask, out TeamId primary);

            TeamId displayTeam = PlanetConnectionGraphLogic.ResolveAsteroidTintTeam(
                mask, primary, viewerTeam);

            // --- Skip before GetComponentInChildren / material writes ---
            if (_proxyAsteroidTerritory.TryGetValue(entity, out var applied) && applied == displayTeam)
                return;

            // [TITAN-ORBIT] Only latch when SgtPlanet actually applied — missing material
            // used to freeze "applied" forever and leave the rock untinted.
            if (WorldBodyVisualApplier.ApplyAsteroidTerritoryTint(go, displayTeam))
                _proxyAsteroidTerritory[entity] = displayTeam;
        }

        /// <summary>
        /// When ghosted <see cref="PlanetState"/> level/team/home diverges from the cached
        /// <see cref="PlanetVisualKey"/>, reconfigure the existing proxy (Saturn rings, moon, materials)
        /// without Destroy+Instantiate.
        /// </summary>
        /// <param name="em">Client EntityManager — per-entity reads only.</param>
        /// <param name="entity">Planet ghost entity already in <see cref="_proxies"/>.</param>
        /// <param name="go">Hybrid planet GameObject proxy root.</param>
        /// <param name="scale">Current ECS world scale applied to the proxy.</param>
        void RefreshPlanetProxyAppearanceIfChanged(EntityManager em, Entity entity, GameObject go, float scale)
        {
            // --- Guard: must be a live planet ghost ---
            if (go == null || !em.Exists(entity) || !em.HasComponent<PlanetState>(entity))
                return;

            var state = em.GetComponentData<PlanetState>(entity);
            var key = new PlanetVisualKey
            {
                IsHome = state.IsHomePlanet,
                Team = state.Ownership,
                PlanetLevel = state.PlanetLevel,
                PlanetId = state.PlanetId,
            };

            // --- Skip when identity matches Instantiates-time / last refresh ---
            bool hadKey = _proxyPlanetVisuals.TryGetValue(entity, out var existingKey);
            if (hadKey && existingKey.Equals(key))
                return;

            // --- In-place refresh (materials only when home/team/id identity changed) ---
            // Level-only gem deposits keep the same surface; capture retints home/team mats.
            bool materialsChanged = !hadKey
                || existingKey.IsHome != key.IsHome
                || existingKey.Team != key.Team
                || existingKey.PlanetId != key.PlanetId;

            WorldBodyVisualApplier.RefreshPlanetVisualAppearance(
                go,
                planetMaterialPool,
                key.IsHome,
                key.Team,
                key.PlanetLevel,
                key.PlanetId,
                scale,
                materialsChanged,
                state.ShipFamilyConfigIndex);

            _proxyPlanetVisuals[entity] = key;
        }

        /// <summary>
        /// When a client asteroid ghost reports <see cref="AsteroidState.IsDestroyed"/>, hide the
        /// rock immediately. Gem visuals wait for networked gem Instantiates (server pose/velocity).
        /// Also refreshes <see cref="_asteroidLastKnown"/> for despawn-without-flag cases.
        /// Per-entity HasComponent only — no full asteroid ToEntityArray.
        /// </summary>
        void DetectAsteroidGemBursts(EntityManager em)
        {
            if (ClientJoinSettleCache.Settling)
                return;

            // Walk asteroid proxy keys only (not ships/planets/gems). Bullet kills already hide
            // via TryHideAsteroidProxyFromHitRpc — this is the ram / missed-RPC fallback.
            foreach (Entity entity in _asteroidProxyEntities)
            {
                if (!_proxies.TryGetValue(entity, out var go) || go == null || !em.Exists(entity))
                    continue;
                if (!em.HasComponent<AsteroidTag>(entity) || !em.HasComponent<AsteroidState>(entity))
                    continue;
                if (!em.HasComponent<LocalTransform>(entity))
                    continue;

                var asteroid = em.GetComponentData<AsteroidState>(entity);
                var lt = em.GetComponentData<LocalTransform>(entity);

                // Cache only while alive — needed if RemainingGems is zeroed on the destroy frame.
                // [TITAN-ORBIT] Health<=0 also means dead (bullet kill) even if IsDestroyed lags
                // behind on a low MaxSendRate asteroid ghost.
                bool dead = asteroid.IsDestroyed || asteroid.Health <= 0f;
                if (!dead)
                {
                    _asteroidLastKnown[entity] = new AsteroidBurstCache
                    {
                        Position = lt.Position,
                        RemainingGems = asteroid.RemainingGems,
                    };
                    continue;
                }

                if (!_asteroidBurstFired.Add(entity))
                    continue;

                // Hide the asteroid proxy immediately — the ghost may linger a few frames while
                // gems Instantiates. Keeping a dead rock visible under a hitch made the blink worse.
                if (go != null && go.activeSelf)
                    go.SetActive(false);
                // Same phantom-hull cull as HitRpc hide (ram / missed-RPC destroy path).
                ClientAsteroidCollisionCull.TryDisablePhysicsCollider(entity);

                // No client-invented gem VFX — wait for server gem ghosts + GemClientMotionApplier.
            }
        }

        /// <summary>
        /// Instantiates GameObject proxies for entities tagged with baked
        /// <see cref="MapBodyHybridVisualPending"/> or runtime
        /// <see cref="MapBodyHybridVisualSpawnRequest"/>.
        /// Per-frame budget — never gathers every asteroid (only the Pending/SpawnRequest queue).
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
            // [TITAN-ORBIT] Prefer GemTag first so destroy/mining pickups appear before leftover
            // asteroid proxies fill the same budget (Instantiates stays 1/frame — unchanged).
            //
            // Player.log 2026-07-22: EntityManager.GetEntityTypeHandle() + ToArchetypeChunkArray
            // → ArchetypeChunk.GetNativeArray NRE every LateUpdate (~2500×) → zero proxy climb →
            // loading bar stuck (meta N latched, proxies never rise). Same stale-handle class as
            // the forbidden ISystem mark path — do NOT chunk-walk with EntityTypeHandle here.
            // ToEntityArray on this Pending/SpawnRequest queue only is join-safe (not all asteroids).
            int frameBudget = GetWorldBodyProxyBudgetThisFrame();
            using var entities = query.ToEntityArray(Unity.Collections.Allocator.Temp);
            var batch = new List<Entity>(frameBudget);

            // Pass 1: gems only
            for (int i = 0; i < entities.Length && batch.Count < frameBudget; i++)
            {
                if (em.HasComponent<GemTag>(entities[i]))
                    batch.Add(entities[i]);
            }

            // Pass 2: remaining world bodies
            for (int i = 0; i < entities.Length && batch.Count < frameBudget; i++)
            {
                if (!em.HasComponent<GemTag>(entities[i]))
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
                        out go,
                        state.ShipFamilyConfigIndex))
                {
                    go = CreatePrimitivePlanetProxy(state.Ownership);
                }

                _proxies[entity] = go;
                RegisterProxyKind(entity, ProxyVisualKind.Planet);
                _proxyPlanetVisuals[entity] = key;
                // [TITAN-ORBIT] Backup if Instantiates hook missed Pending early-return — orbit motor
                // CollectFromClientRegistry needs this entity under TransformQuarantine.
                PlanetClientEntityRegistry.NotifyInstantiated(entity);
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
                RegisterProxyKind(entity, ProxyVisualKind.Asteroid);
                go.transform.position = GetVisualPosition(entity, lt.Position);
                go.transform.localScale = Vector3.one * scale;
                return true;
            }

            if (em.HasComponent<GemState>(entity) && em.HasComponent<GemTag>(entity))
            {
                var state = em.GetComponentData<GemState>(entity);
                scale = GemVisualApplier.ComputeVisualScale(math.max(0.25f, state.Value));
                Vector3 displayPos = GetVisualPosition(entity, lt.Position);

                // --- Network gem proxy from Instantiates ghost only ---
                // [TITAN-ORBIT] No ClientGemBurstPresenter invent. Count/pose/velocity come from
                // server gem ghosts; GemClientMotionApplier presents ghosted LocalTransform +
                // GemKinematics between snapshots.
                if (!GemVisualApplier.TryCreateGemVisual(
                        gemVisualPrefab, state.Value, state.IsBonusGem, out go))
                {
                    go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    go.name = "GemTagProxy";
                    var col = go.GetComponent<Collider>();
                    if (col != null) Destroy(col);
                    var renderer = go.GetComponent<Renderer>();
                    if (renderer != null)
                        renderer.material = WorldBodyVisualApplier.CreateLitMaterial(
                            state.IsBonusGem ? new Color(1f, 0.9f, 0.15f) : Color.red);
                }
                else
                {
                    go.name = "GemTagProxy";
                }

                _proxies[entity] = go;
                RegisterProxyKind(entity, ProxyVisualKind.Gem);
                go.transform.localScale = Vector3.one * scale;
                GemVisualDiameterRegistry.SetDiameter(entity, GemVisualApplier.ReadWorldDiameter(go, state.Value));
                go.transform.SetPositionAndRotation(displayPos, lt.Rotation);

                var motion = go.GetComponent<GemClientMotionApplier>();
                if (motion == null)
                    motion = go.AddComponent<GemClientMotionApplier>();
                motion.Bind(entity, lt.Position);

                // Seed from server kinematics so the GO coasts immediately if LT snapshots lag.
                if (em.HasComponent<GemKinematics>(entity))
                {
                    var kin = em.GetComponentData<GemKinematics>(entity);
                    motion.SeedVelocity(kin.Velocity, kin.AngularVelocity);
                }

                return true;
            }

            return false;
        }

        /// <summary>
        /// Records proxy kind + incremental loading/world-body counts.
        /// Call whenever a proxy is first inserted into <see cref="_proxies"/>.
        /// </summary>
        void RegisterProxyKind(Entity entity, ProxyVisualKind kind)
        {
            if (_proxyKinds.TryGetValue(entity, out var prev))
            {
                if (prev == kind)
                    return;
                UnregisterProxyKindCounts(prev);
                if (prev == ProxyVisualKind.Asteroid)
                    _asteroidProxyEntities.Remove(entity);
            }

            _proxyKinds[entity] = kind;
            if (kind == ProxyVisualKind.Asteroid)
                _asteroidProxyEntities.Add(entity);

            switch (kind)
            {
                case ProxyVisualKind.Planet:
                case ProxyVisualKind.Asteroid:
                    _mapLoadingProxyCountCached++;
                    _worldBodyProxyCountCached++;
                    break;
                case ProxyVisualKind.Gem:
                case ProxyVisualKind.PeopleTransport:
                    _worldBodyProxyCountCached++;
                    break;
            }
        }

        /// <summary>Decrements incremental counts when a kind is cleared or replaced.</summary>
        void UnregisterProxyKindCounts(ProxyVisualKind kind)
        {
            switch (kind)
            {
                case ProxyVisualKind.Planet:
                case ProxyVisualKind.Asteroid:
                    _mapLoadingProxyCountCached = math.max(0, _mapLoadingProxyCountCached - 1);
                    _worldBodyProxyCountCached = math.max(0, _worldBodyProxyCountCached - 1);
                    break;
                case ProxyVisualKind.Gem:
                case ProxyVisualKind.PeopleTransport:
                    _worldBodyProxyCountCached = math.max(0, _worldBodyProxyCountCached - 1);
                    break;
            }
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
            // --- Local = unbounded sim; remote = hysteresis tile near local ship ---
            // [TITAN-ORBIT] Do not Wrap the local hull. Continuum re-unwrap lives only in the
            // moon-dock takeoff cinematic (ShipMoonDockVisualApplier) — running it every frame
            // fought soft-track and added presentation lag.
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
        /// Planet id the local ship should keep force-nearest-tiled (active orbit or moon dock).
        /// </summary>
        /// <param name="em">Visualization-world EntityManager.</param>
        /// <returns>Planet id, or 0 when the local ship is free-flying.</returns>
        int ResolveForceNearestPlanetId(EntityManager em)
        {
            // --- No local ship yet (loading / team select) ---
            if (!TryResolveLocalPlayerShipEntityCached(em, out Entity localShip) ||
                localShip == Entity.Null ||
                !em.Exists(localShip))
            {
                return 0;
            }

            // --- Moon dock wins (ship is glued to that planet's moon) ---
            if (em.HasComponent<ShipMoonDockState>(localShip))
            {
                int dockPlanetId = em.GetComponentData<ShipMoonDockState>(localShip).MoonPlanetId;
                if (dockPlanetId != 0)
                    return dockPlanetId;
            }

            // --- Passive orbit ring — keep planet + ring + moon on the ship's tile ---
            if (em.HasComponent<ShipOrbitState>(localShip))
            {
                var orbit = em.GetComponentData<ShipOrbitState>(localShip);
                if (orbit.InOrbitRing && orbit.OrbitPlanetId != 0)
                    return orbit.OrbitPlanetId;
            }

            return 0;
        }

        /// <summary>
        /// Resolves and caches local player ship entity — LocalPlayerShipTag first, then GhostOwner NetworkId.
        /// Returns false while team/rejoin flow suppresses control so map-load orphans do not drive camera.
        /// While <see cref="ClientJoinSettleCache.ShouldSkipShipEntityQueries"/>, never runs ship
        /// <c>ToEntityArray</c> / <c>ToComponentDataArray</c> — only Instantiates-hook seed or cache
        /// (asteroid tint calls this from map-body sync after Join Team).
        /// </summary>
        bool TryResolveLocalPlayerShipEntityCached(EntityManager em, out Entity localShipEntity)
        {
            localShipEntity = Entity.Null;

            // [TITAN-ORBIT] Match EcsGameBridge / ShipVisualSyncSystem — no "my ship" before Join Team.
            if (ClientTeamFlowState.ShouldSuppressLocalPlayerControl())
            {
                _cachedLocalPlayerShipEntity = Entity.Null;
                LocalPlayerShipProxy = null;
                LocalPlayerShipVisualRoot = null;
                return false;
            }

            // --- Instantiates / post–TeamChoice hold: no ship archetype gather ---
            // [TITAN-ORBIT] Player.log 2026-07-27: TeamChoiceResult → SyncExistingWorldBodyProxyTransforms
            // → RefreshAsteroidTerritoryTintIfChanged → this method → ToComponentDataArray<GhostOwner>
            // → GatherComponentDataWithoutFilter → Crash!!!. EnsureShipProxies was already gated, but
            // map-body sync still called this every asteroid. Prefer seed (no gather) or bail.
            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
            {
                // Seeded local ship is a known Entity — Exists/HasComponent are safe; ToEntityArray is not.
                if (LocalShipEntitySeed.TryGetSeededShip(em, out var seeded) &&
                    seeded != Entity.Null &&
                    em.Exists(seeded) &&
                    em.HasComponent<ShipTag>(seeded))
                {
                    localShipEntity = seeded;
                    _cachedLocalPlayerShipEntity = seeded;
                    return true;
                }

                // Cache hit only — never fall through to CreateEntityQuery + ToEntityArray below.
                if (_cachedLocalPlayerShipEntity != Entity.Null &&
                    em.Exists(_cachedLocalPlayerShipEntity) &&
                    em.HasComponent<ShipTag>(_cachedLocalPlayerShipEntity))
                {
                    localShipEntity = _cachedLocalPlayerShipEntity;
                    return true;
                }

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
        /// The planet the local ship is orbiting / moon-docked on uses a tight hysteresis margin
        /// so the ring follows across seams without ForceNearest midpoint flicker.
        /// </summary>
        /// <param name="forceLogical">When true, skip display unwrap (rare debug / special cases).</param>
        Vector3 GetVisualPosition(Entity entity, EntityManager em, float3 logicalPos, bool forceLogical = false)
        {
            if (forceLogical || ToroidalDisplay.IsLocalPlayerShip(em, entity))
                return logicalPos;

            if (!_hasToroidalReference && !ToroidalDisplay.TryGetReferencePosition(out _toroidalReference))
                return logicalPos;

            _hasToroidalReference = true;
            if (ShouldForceNearestPlanetTile(em, entity))
            {
                int planetId = em.HasComponent<PlanetState>(entity)
                    ? em.GetComponentData<PlanetState>(entity).PlanetId
                    : 0;
                return ToroidalDisplay.ToDisplayPositionForOrbitPlanet(
                    entity, planetId, logicalPos, _toroidalReference);
            }

            return ToroidalDisplay.ToDisplayPositionWithHysteresis(entity, logicalPos, _toroidalReference);
        }

        /// <summary>Per-entity tile unwrap when EntityManager is not needed for local-ship checks.</summary>
        Vector3 GetVisualPosition(Entity entity, float3 logicalPos)
        {
            if (!_hasToroidalReference && !ToroidalDisplay.TryGetReferencePosition(out _toroidalReference))
                return logicalPos;

            _hasToroidalReference = true;

            // --- Orbit / dock planet: tight hysteresis via cached planet visual key ---
            if (_forceNearestPlanetId != 0 &&
                _proxyPlanetVisuals.TryGetValue(entity, out var planetKey) &&
                planetKey.PlanetId == _forceNearestPlanetId)
            {
                return ToroidalDisplay.ToDisplayPositionForOrbitPlanet(
                    entity, _forceNearestPlanetId, logicalPos, _toroidalReference);
            }

            return ToroidalDisplay.ToDisplayPositionWithHysteresis(entity, logicalPos, _toroidalReference);
        }

        /// <summary>
        /// True when this entity is the planet the local ship is orbiting or moon-docked on.
        /// </summary>
        bool ShouldForceNearestPlanetTile(EntityManager em, Entity entity)
        {
            if (_forceNearestPlanetId == 0 || !em.HasComponent<PlanetState>(entity))
                return false;
            return em.GetComponentData<PlanetState>(entity).PlanetId == _forceNearestPlanetId;
        }

        /// <summary>
        /// During <see cref="ClientJoinSettleCache.GhostSpawnBacklog"/>, create/sync only the
        /// Instantiates-hook seeded local ship — never <c>ToEntityArray</c> all ships.
        /// Remotes wait until the Instantiates queue drains.
        /// </summary>
        /// <param name="em">Client EntityManager.</param>
        /// <param name="alive">Entities that still need a proxy this frame (prune set).</param>
        void EnsureAndSyncLocalSeededShipProxy(EntityManager em, HashSet<Entity> alive)
        {
            // --- Team suppress: no owned hull until Join Team confirms ---
            if (ClientTeamFlowState.ShouldSuppressLocalPlayerControl())
                return;

            // --- Seed from Instantiates hook (no ship gather); recover when Instantiates idle ---
            if (!LocalShipEntitySeed.TryGetSeededShip(em, out var shipEntity))
                LocalShipEntitySeed.TryRecoverOwnedShip(em);

            if (!LocalShipEntitySeed.TryGetSeededShip(em, out shipEntity) ||
                shipEntity == Entity.Null ||
                !em.Exists(shipEntity) ||
                !em.HasComponent<LocalTransform>(shipEntity))
                return;

            // --- Warm local-ship cache without archetype gather ---
            // [TITAN-ORBIT] TryResolveLocalPlayerShipEntityCached skips ToEntityArray during
            // ShouldSkipShipEntityQueries; seeding the cache here lets tint / force-nearest reuse
            // the known entity once Instantiates-hook fires.
            _cachedLocalPlayerShipEntity = shipEntity;

            alive.Add(shipEntity);

            int networkId = 0;
            if (em.HasComponent<GhostOwner>(shipEntity))
                networkId = em.GetComponentData<GhostOwner>(shipEntity).NetworkId;

            TeamId team = TeamId.None;
            int shipLevel = 1;
            int branchIndex = 0;
            int shipFamilyConfigIndex = PlanetShipFamilyAssignment.HomeFamilyConfigIndex;
            if (em.HasComponent<ShipState>(shipEntity))
            {
                var ship = em.GetComponentData<ShipState>(shipEntity);
                team = ship.Team;
                shipLevel = Mathf.Max(1, ship.ShipLevel);
                branchIndex = Mathf.Max(0, ship.BranchIndex);
                shipFamilyConfigIndex = ship.ShipFamilyConfigIndex;
            }

            string chassisId = null;
            if (team != TeamId.None)
            {
                ShipStatApplyLogic.TryResolveChassisId(
                    team,
                    shipLevel,
                    branchIndex,
                    out chassisId,
                    allowFallback: true,
                    shipFamilyConfigIndex: shipFamilyConfigIndex);
            }

            var lt = em.GetComponentData<LocalTransform>(shipEntity);
            float scale = Mathf.Max(0.25f, lt.Scale) * shipVisualScale;

            // --- Create hybrid GO if missing (or rebuild on chassis/team change) ---
            bool needCreate = true;
            if (_proxies.TryGetValue(shipEntity, out var existing) && existing != null)
            {
                _proxyShipLevels.TryGetValue(shipEntity, out int lastLevel);
                _proxyBranchIndices.TryGetValue(shipEntity, out int lastBranch);
                _proxyTeams.TryGetValue(shipEntity, out TeamId lastTeam);
                _proxyChassisIds.TryGetValue(shipEntity, out string lastChassis);
                bool sameHull = lastLevel == shipLevel
                    && lastBranch == branchIndex
                    && lastTeam == team
                    && string.Equals(lastChassis, chassisId, System.StringComparison.Ordinal);
                needCreate = !sameHull;
                if (needCreate)
                    DestroyProxy(shipEntity);
            }

            if (needCreate)
            {
                float muzzleOffset = defaultMuzzleOffset;
                if (em.HasComponent<ShipWeaponConfig>(shipEntity))
                    muzzleOffset = em.GetComponentData<ShipWeaponConfig>(shipEntity).MuzzleOffset;

                var go = CreateShipProxy(
                    shipEntity, networkId, team, shipLevel, branchIndex, chassisId, scale, muzzleOffset);
                if (TryGetPresentationTransform(shipEntity, em, out var presentLt))
                    ApplyShipProxyTransform(shipEntity, em, true, presentLt, go.transform, scale);
            }

            // --- Pose sync for the one known entity (no WithEntityAccess / ToEntityArray) ---
            if (!_proxies.TryGetValue(shipEntity, out var proxyGo) || proxyGo == null)
                return;

            if (!TryGetPresentationTransform(shipEntity, em, out var poseLt))
                return;

            float poseScale = Mathf.Max(0.25f, poseLt.Scale) * shipVisualScale;
            LocalPlayerShipProxy = proxyGo;
            LocalPlayerShipVisualRoot = proxyGo.transform;
            ApplyShipProxyTransform(shipEntity, em, true, poseLt, proxyGo.transform, poseScale);

            if (em.HasComponent<ShipState>(shipEntity))
            {
                var ship = em.GetComponentData<ShipState>(shipEntity);
                _proxyShipLevels[shipEntity] = Mathf.Max(1, ship.ShipLevel);
                _proxyTeams[shipEntity] = ship.Team;
                _proxyBranchIndices[shipEntity] = Mathf.Max(0, ship.BranchIndex);
                if (ship.IsDead)
                    proxyGo.SetActive(false);
                else if (!proxyGo.activeSelf)
                    proxyGo.SetActive(true);
            }
        }

        /// <summary>
        /// Spawns missing ship proxies and rebuilds when team, ship level, branch, or chassis id changes.
        /// Does not move transforms — SyncShipProxyTransforms handles per-frame pose.
        /// <para>
        /// [TITAN-ORBIT] Under TransformQuarantine, Entities Graphics ship meshes are skipped — this hybrid
        /// path is the only hull the player sees. Branch/chassis must be tracked or moon-menu ship picks
        /// look unlocked but keep showing the wrong prefab.
        /// </para>
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

                // --- Resolve live chassis identity (level + branch → chassis id) ---
                // [NETCODE] ShipState.BranchIndex is ghosted with ShipLevel — required for correct hull.
                TeamId team = TeamId.None;
                int shipLevel = 1;
                int branchIndex = 0;
                int shipFamilyConfigIndex = PlanetShipFamilyAssignment.HomeFamilyConfigIndex;
                if (em.HasComponent<ShipState>(entity))
                {
                    var ship = em.GetComponentData<ShipState>(entity);
                    team = ship.Team;
                    shipLevel = Mathf.Max(1, ship.ShipLevel);
                    branchIndex = Mathf.Max(0, ship.BranchIndex);
                    shipFamilyConfigIndex = ship.ShipFamilyConfigIndex;
                }

                string chassisId = null;
                if (team != TeamId.None)
                {
                    ShipStatApplyLogic.TryResolveChassisId(
                        team,
                        shipLevel,
                        branchIndex,
                        out chassisId,
                        allowFallback: true,
                        shipFamilyConfigIndex: shipFamilyConfigIndex);
                }

                // [TITAN-ORBIT] Chassis swap while moon-docked: keep the old hull's spinning
                // surface contact so the new ship appears glued to the same moon pose.
                Vector3 preservedMoonSurfaceDir = default;
                bool havePreservedMoonSurfaceDir = false;

                if (_proxies.TryGetValue(entity, out var existing) && existing != null)
                {
                    _proxyShipLevels.TryGetValue(entity, out int lastLevel);
                    _proxyBranchIndices.TryGetValue(entity, out int lastBranch);
                    _proxyTeams.TryGetValue(entity, out TeamId lastTeam);
                    _proxyChassisIds.TryGetValue(entity, out string lastChassis);

                    // Same level + different branch (or chassis) still needs a new hull prefab.
                    bool sameHull = lastLevel == shipLevel
                        && lastBranch == branchIndex
                        && lastTeam == team
                        && string.Equals(lastChassis, chassisId, System.StringComparison.Ordinal);
                    if (sameHull)
                        continue;

                    // Capture contact dir before DestroyProxy clears the old applier.
                    var oldMoonDockVisual = existing.GetComponent<ShipMoonDockVisualApplier>();
                    if (oldMoonDockVisual != null && oldMoonDockVisual.IsDrivingMoonDockPresentation)
                    {
                        preservedMoonSurfaceDir = oldMoonDockVisual.LandingSurfaceDir;
                        havePreservedMoonSurfaceDir = preservedMoonSurfaceDir.sqrMagnitude > 0.0001f;
                    }

                    DestroyProxy(entity);
                }

                float muzzleOffset = defaultMuzzleOffset;
                if (em.HasComponent<ShipWeaponConfig>(entity))
                    muzzleOffset = em.GetComponentData<ShipWeaponConfig>(entity).MuzzleOffset;

                var go = CreateShipProxy(
                    entity, networkId, team, shipLevel, branchIndex, chassisId, scale, muzzleOffset);
                if (TryGetPresentationTransform(entity, em, out var presentLt))
                    ApplyShipProxyTransform(entity, em, isLocalPlayerShip, presentLt, go.transform, scale);

                // After pose apply: if still fully moon-docked, snap cinematic to landed (chassis swap).
                // Prefer preserved surface dir so purchase keeps the spinning moon pose.
                var moonDockVisual = go.GetComponent<ShipMoonDockVisualApplier>();
                if (moonDockVisual != null)
                {
                    if (havePreservedMoonSurfaceDir)
                        moonDockVisual.SeedFullyLandedPresentation(em, preservedMoonSurfaceDir);
                    else
                        moonDockVisual.SeedFullyLandedPresentation(em);
                }
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
                    // [TITAN-ORBIT] Same gate as ShipMoonDockVisualApplier — fully landed stays skipped
                    // even if approach delay briefly replicates as 0.
                    bool approachReady = moonDock.LandingApproachDelay + 0.0001f >= GemEconomyConstants.MoonLandingApproachDelaySeconds;
                    bool fullyLanded = moonDock.LandingProgress + 0.0001f >= GemEconomyConstants.MoonLandingCompleteThreshold;
                    skipTransformSync = moonDock.MoonPlanetId != 0
                        && moonDock.LandingProgress > 0.001f
                        && (approachReady || fullyLanded);
                }

                bool isLocalPlayerShip = IsLocalPlayerShip(entity, localShipEntity);
                if (isLocalPlayerShip)
                {
                    LocalPlayerShipProxy = go;
                    LocalPlayerShipVisualRoot = go != null ? go.transform : null;
                }

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

                // Keep branch/chassis bookkeeping fresh for EnsureShipProxies rebuild checks.
                // [NETCODE] Must match EnsureShipProxies (ShipState.BranchIndex) — loadout used to
                // thrash DestroyProxy/CreateShipProxy every frame after upgrade-tree purchases and
                // reset the moon-dock cinematic (ship looked ejected from orbit).
                if (em.HasComponent<ShipState>(entity))
                    _proxyBranchIndices[entity] = Mathf.Max(0, em.GetComponentData<ShipState>(entity).BranchIndex);

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
        /// Prefab comes from the exact chassis id (level + branch) so upgrade-tree picks match the hull on screen.
        /// </summary>
        GameObject CreateShipProxy(
            Entity entity,
            int networkId,
            TeamId team,
            int shipLevel,
            int branchIndex,
            string chassisId,
            float scale,
            float muzzleOffset)
        {
            // --- Instantiate chassis-specific hull ---
            GameObject go;
            if (ShipVisualApplier.TryCreateShipVisualForChassis(
                    shipFamily, shipVisualPrefab, team, shipLevel, chassisId, out go))
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

            // --- Bookkeeping for rebuild detection ---
            _proxyShipLevels[entity] = shipLevel;
            _proxyBranchIndices[entity] = branchIndex;
            _proxyChassisIds[entity] = chassisId ?? string.Empty;
            _proxyTeams[entity] = team;
            _proxies[entity] = go;
            RegisterProxyKind(entity, ProxyVisualKind.Ship);

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

            // Family prefix from chassis id (AstroEagle_T2 → AstroEagle) when available.
            ShipFamilyDefinition bindFamily = shipFamily;
            string familyPrefix = shipFamily != null ? shipFamily.familyId : "AstroEagle";
            if (!string.IsNullOrEmpty(chassisId)
                && ShipStatApplyLogic.TryResolveFamilyForChassisId(chassisId, out ShipFamilyDefinition resolved)
                && resolved != null)
            {
                bindFamily = resolved;
                familyPrefix = resolved.familyId;
            }

            propulsionVisual.Bind(entity, familyPrefix, propulsionVfxSettings, bindFamily);

            var attributeScaleVisual = go.GetComponent<ShipComponentAttributeScaleApplier>();
            if (attributeScaleVisual == null)
                attributeScaleVisual = go.AddComponent<ShipComponentAttributeScaleApplier>();
            attributeScaleVisual.Bind(entity, familyPrefix, bindFamily);

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

        /// <summary>
        /// Max networked gem GO proxies per frame from the urgent Instantiates queue.
        /// GhostSpawn Instantiates stays 1/frame (join-crash invariant). Once a gem ghost exists,
        /// we may Rent several pool GOs per frame so a small destroy burst becomes visible
        /// within a split second — still server-driven (no local invent).
        /// </summary>
        const int MaxUrgentGemProxiesPerFrame = 4;

        /// <summary>
        /// Creates GameObject proxies for gems that Instantiated this frame (from
        /// <see cref="GemClientEntityRegistry"/>). Bypasses the shared world-body budget so
        /// destroy bursts are not stuck behind asteroid Pending drain — but still rate-limits
        /// Instantiates per frame (see <see cref="MaxUrgentGemProxiesPerFrame"/>).
        /// </summary>
        void DrainUrgentGemProxies(EntityManager em, HashSet<Entity> alive)
        {
            // --- Skip during map Instantiates storm ---
            // [TITAN-ORBIT] Urgent gem Instantiates during Settling pile hitch cost onto join;
            // map load already drains Pending/SpawnRequest under the loading screen.
            if (ClientJoinSettleCache.Settling)
                return;

            var urgent = new List<Entity>(8);
            int remainingAfter = GemClientEntityRegistry.DrainUrgentVisualQueue(urgent, MaxUrgentGemProxiesPerFrame);
            if (urgent.Count == 0)
                return;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            int created = 0;

            for (int i = 0; i < urgent.Count; i++)
            {
                Entity entity = urgent[i];
                if (!em.Exists(entity) || !em.HasComponent<LocalTransform>(entity))
                {
                    GemClientEntityRegistry.NotifyDestroyed(entity);
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
                    continue;

                ClearVisualQueueTags(em, entity);
                if (!em.HasComponent<MapBodyHybridVisualLinked>(entity))
                    em.AddComponentData(entity, new MapBodyHybridVisualLinked());
                alive.Add(entity);
                // Do not consume world-body asteroid budget — gems use their own per-frame cap.
                _newWorldBodyProxiesThisFrame++;
                created++;
            }

            sw.Stop();
            if (TitanOrbitDebugFlags.LogAsteroidDestroyPerf)
            {
                Debug.Log(
                    $"[AsteroidDestroy] Urgent gem proxies created={created}/{urgent.Count} " +
                    $"queueLeft={remainingAfter} ms={sw.Elapsed.TotalMilliseconds:F2} " +
                    $"frameDtMs={Time.deltaTime * 1000f:F1}");
            }
        }

        /// <summary>Tears down proxy GameObject and clears all per-entity registry entries.</summary>
        void DestroyProxy(Entity entity)
        {
            if (entity == _cachedDedicatedLocalShipEntity)
                _cachedDedicatedLocalShipEntity = Entity.Null;

            // --- Drop toroidal tile memory for this entity ---
            ToroidalDisplay.RemoveEntity(entity);
            GemClientEntityRegistry.NotifyDestroyed(entity);
            PlanetClientEntityRegistry.NotifyDestroyed(entity);
            _asteroidBurstFired.Remove(entity);
            _asteroidLastKnown.Remove(entity);

            if (_proxies.TryGetValue(entity, out var go))
            {
                if (_proxyNetworkIds.TryGetValue(entity, out int networkId) && go != null)
                    ShipWeaponProxyRegistry.Unregister(networkId, go.transform);
                if (go != null)
                {
                    // [TITAN-ORBIT] Gem visuals recycle via GemVisualPool — Destroy only non-pooled proxies.
                    if (!GemVisualPool.TryReturn(go))
                        Destroy(go);
                }

                if (_proxyKinds.TryGetValue(entity, out var registeredKind))
                {
                    UnregisterProxyKindCounts(registeredKind);
                    _proxyKinds.Remove(entity);
                }

                _asteroidProxyEntities.Remove(entity);
                _proxies.Remove(entity);
                _proxyNetworkIds.Remove(entity);
                _proxyShipLevels.Remove(entity);
                _proxyBranchIndices.Remove(entity);
                _proxyChassisIds.Remove(entity);
                _proxyTeams.Remove(entity);
                _proxyPlanetVisuals.Remove(entity);
                _proxyAsteroidTerritory.Remove(entity);
                _bulletStretchVisuals.Remove(entity);
            }
        }

        /// <summary>
        /// [LEGACY] Formerly consumed <see cref="BulletHitEventElement"/>. Impact VFX is now owned by
        /// <see cref="BulletVfxDriver"/> via <see cref="BulletHitRpc"/>. Kept for reference; not called.
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
                    RegisterProxyKind(entity, ProxyVisualKind.PeopleTransport);
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
        /// [LEGACY] Formerly drew ECS <see cref="BulletTracerState"/> proxies. Muzzle/tracer/impact
        /// are now owned by <see cref="BulletVfxDriver"/>. Kept for reference; not called.
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
                        out go,
                        state.ShipFamilyConfigIndex))
                {
                    go = CreatePrimitivePlanetProxy(state.Ownership);
                    _proxies[entity] = go;
                }
                else
                {
                    _proxies[entity] = go;
                }

                RegisterProxyKind(entity, ProxyVisualKind.Planet);
                alive.Add(entity);
                _proxyPlanetVisuals[entity] = key;
                // [TITAN-ORBIT] Backup Instantiates registry for quarantine-safe orbit Collect.
                PlanetClientEntityRegistry.NotifyInstantiated(entity);
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
                RegisterProxyKind(entity, ProxyVisualKind.Asteroid);
                alive.Add(entity);
                go.transform.position = GetVisualPosition(entity, lt.Position);
                go.transform.localScale = Vector3.one * scale;
            }
        }

        /// <summary>
        /// Gem proxies — value-scaled via <see cref="GemVisualApplier"/> with end-of-life shrink;
        /// registers diameter for tractor beam.
        /// <para>
        /// [LEGACY] Not called from <see cref="SyncAllProxies"/> under TransformQuarantine
        /// (Pending/SpawnRequest + urgent Instantiates own gem creates). Kept for catch-up
        /// tooling — must match the urgent path: <see cref="GemClientMotionApplier"/> owns pose.
        /// </para>
        /// </summary>
        void DrawGems(EntityManager em, HashSet<Entity> alive)
        {
            using var query = em.CreateEntityQuery(
                ComponentType.ReadOnly<GemTag>(),
                ComponentType.ReadOnly<GemState>(),
                ComponentType.ReadOnly<LocalTransform>());
            using var entities = query.ToEntityArray(Unity.Collections.Allocator.Temp);
            using var states = query.ToComponentDataArray<GemState>(Unity.Collections.Allocator.Temp);
            using var transforms = query.ToComponentDataArray<LocalTransform>(Unity.Collections.Allocator.Temp);

            // --- Lifetime clock (ServerTick — same as SpawnServerTime stamp) ---
            float now = PlanetGemMoonOrbitClock.TryGetElapsedSeconds(out double tickNow, includeTickFraction: true)
                ? (float)tickNow
                : (float)Time.timeAsDouble;

            for (int i = 0; i < entities.Length; i++)
            {
                var entity = entities[i];
                var state = states[i];
                var lt = transforms[i];
                float scale = GemVisualApplier.ComputeLifetimeVisualScale(
                    state.Value, state.SpawnServerTime, now);

                if (_proxies.TryGetValue(entity, out var go) && go != null)
                {
                    alive.Add(entity);
                    // Scale / diameter only — GemClientMotionApplier owns display pose (wrap tiles).
                    go.transform.localScale = Vector3.one * scale;
                    GemVisualDiameterRegistry.SetDiameter(entity, GemVisualApplier.ReadWorldDiameter(go, state.Value));
                    continue;
                }

                // --- Rate-limit new Instantiates during join settle ---
                if (!TryConsumeWorldBodyProxyBudget())
                    continue;

                // DrawGems catch-up — same pool Rent + motion applier as urgent Instantiates path.
                GemVisualPool.EnsurePrefab(gemVisualPrefab);
                if (!GemVisualApplier.TryCreateGemVisual(
                        gemVisualPrefab, state.Value, state.IsBonusGem, out go))
                {
                    go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    go.name = "GemTagProxy";
                    var col = go.GetComponent<Collider>();
                    if (col != null) Destroy(col);
                    var renderer = go.GetComponent<Renderer>();
                    if (renderer != null)
                        renderer.material = WorldBodyVisualApplier.CreateLitMaterial(
                            state.IsBonusGem ? new Color(1f, 0.9f, 0.15f) : Color.red);
                }

                _proxies[entity] = go;
                RegisterProxyKind(entity, ProxyVisualKind.Gem);
                alive.Add(entity);
                Vector3 displayPos = GetVisualPosition(entity, lt.Position);
                go.transform.SetPositionAndRotation(displayPos, lt.Rotation);
                go.transform.localScale = Vector3.one * scale;
                GemVisualDiameterRegistry.SetDiameter(entity, GemVisualApplier.ReadWorldDiameter(go, state.Value));

                var motion = go.GetComponent<GemClientMotionApplier>();
                if (motion == null)
                    motion = go.AddComponent<GemClientMotionApplier>();
                motion.Bind(entity, lt.Position);
                if (em.HasComponent<GemKinematics>(entity))
                {
                    var kin = em.GetComponentData<GemKinematics>(entity);
                    motion.SeedVelocity(kin.Velocity, kin.AngularVelocity);
                }
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
            _proxyBranchIndices.Clear();
            _proxyChassisIds.Clear();
            _proxyTeams.Clear();
            _proxyPlanetVisuals.Clear();
            _proxyAsteroidTerritory.Clear();
        }
    }
}
