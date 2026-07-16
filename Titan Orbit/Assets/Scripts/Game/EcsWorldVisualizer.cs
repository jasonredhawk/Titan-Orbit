using System.Collections.Generic;
using TitanOrbit.Audio;
using TitanOrbit.Core;
using TitanOrbit.Data;
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
    /// </summary>
    [DefaultExecutionOrder(66000)]
    public class EcsWorldVisualizer : MonoBehaviour
    {
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

        /// <summary>Local-player ship proxy on dedicated clients.</summary>
        public GameObject LocalPlayerShipProxy { get; private set; }

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
            Application.onBeforeRender += OnBeforeRenderSync;
        }

        /// <summary>[UNITY] Unsubscribe to avoid leaks when the visualizer is destroyed.</summary>
        void OnDisable()
        {
            Application.onBeforeRender -= OnBeforeRenderSync;
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

            var alive = new HashSet<Entity>();

            // --- Ship proxies (hybrid path only — Entities Graphics owns ship visuals when enabled) ---
            if (!TitanOrbitPresentationConfig.UseEntitiesGraphicsForShips)
            {
                EnsureShipProxies(em);
                SyncShipProxyTransforms(em, alive);
            }

            // --- World body proxies ---
            DrawPlanets(em, alive);
            DrawAsteroids(em, alive);
            DrawGems(em, alive);
            GemVisualDiameterRegistry.RemoveStale(alive);
            DrawPeopleTransports(em, alive);

            // --- Combat presentation ---
            ProcessBulletHitEvents(em);
            DrawBullets(em, alive);

            // --- Tear down ghosts that despawned this frame ---
            var remove = new List<Entity>();
            foreach (var kv in _proxies)
            {
                if (!alive.Contains(kv.Key))
                    remove.Add(kv.Key);
            }

            foreach (var entity in remove)
                DestroyProxy(entity);
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
        /// </summary>
        static void ApplyShipProxyTransform(bool isLocalPlayerShip, in LocalTransform lt, Transform go, float scale)
        {
            Vector3 pos = lt.Position;
            Quaternion rot = lt.Rotation;
            go.SetPositionAndRotation(pos, rot);
            go.localScale = Vector3.one * scale;

            if (isLocalPlayerShip)
                ShipDisplayPose.SetLocalPose(pos, rot);
        }

        /// <summary>
        /// Resolves and caches local player ship entity — LocalPlayerShipTag first, then GhostOwner NetworkId.
        /// </summary>
        bool TryResolveLocalPlayerShipEntityCached(EntityManager em, out Entity localShipEntity)
        {
            localShipEntity = Entity.Null;

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

        /// <summary>[TITAN-ORBIT] Toroidal wrap disabled for visuals — logical position equals visual position.</summary>
        Vector3 GetVisualPosition(Entity entity, EntityManager em, float3 logicalPos) => logicalPos;

        Vector3 GetVisualPosition(Entity entity, float3 logicalPos) => logicalPos;

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

            for (int i = 0; i < entities.Length; i++)
            {
                var entity = entities[i];
                var lt = transforms[i];
                bool isLocalPlayerShip = IsLocalPlayerShip(entity, localShipEntity);
                float scale = Mathf.Max(0.25f, lt.Scale) * shipVisualScale;

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

                int networkId = 0;
                if (em.HasComponent<GhostOwner>(entity))
                    networkId = em.GetComponentData<GhostOwner>(entity).NetworkId;

                var go = CreateShipProxy(entity, networkId, team, shipLevel, scale, muzzleOffset);
                if (TryGetPresentationTransform(entity, em, out var presentLt))
                    ApplyShipProxyTransform(isLocalPlayerShip, presentLt, go.transform, scale);
            }
        }

        /// <summary>
        /// Per-frame ship proxy pose sync from presentation cache. Skips transform during moon-dock cinematic.
        /// Registers weapon mounts with ShipWeaponProxyRegistry by network id.
        /// </summary>
        void SyncShipProxyTransforms(EntityManager em, HashSet<Entity> alive)
        {
            TryResolveLocalPlayerShipEntityCached(em, out var localShipEntity);

            using var query = em.CreateEntityQuery(
                ComponentType.ReadOnly<ShipTag>(),
                ComponentType.ReadOnly<LocalTransform>());
            using var entities = query.ToEntityArray(Unity.Collections.Allocator.Temp);

            for (int i = 0; i < entities.Length; i++)
            {
                var entity = entities[i];
                alive.Add(entity);
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
                    ApplyShipProxyTransform(isLocalPlayerShip, lt, go.transform, scale);
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

                int networkId = 0;
                if (em.HasComponent<GhostOwner>(entity))
                    networkId = em.GetComponentData<GhostOwner>(entity).NetworkId;
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
                Vector3 hitPos = hit.HitPosition;
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

        /// <summary>People-transport gem-style proxies — scale by carried amount, tint by team.</summary>
        void DrawPeopleTransports(EntityManager em, HashSet<Entity> alive)
        {
            using var query = em.CreateEntityQuery(
                ComponentType.ReadOnly<PeopleTransportTag>(),
                ComponentType.ReadOnly<PeopleTransportState>(),
                ComponentType.ReadOnly<LocalTransform>());
            using var entities = query.ToEntityArray(Unity.Collections.Allocator.Temp);
            using var states = query.ToComponentDataArray<PeopleTransportState>(Unity.Collections.Allocator.Temp);

            for (int i = 0; i < entities.Length; i++)
            {
                var entity = entities[i];
                alive.Add(entity);
                var state = states[i];
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

        /// <summary>Bullet tracer world position — toroidal wrap not applied on presentation path.</summary>
        Vector3 GetBulletVisualPosition(Entity entity, EntityManager em, float3 logicalPos) => logicalPos;

        Vector3 GetBulletVisualPosition(float3 logicalPos) => logicalPos;

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
                alive.Add(entity);
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
                        go.transform.position = GetVisualPosition(entity, lt.Position);
                        go.transform.rotation = lt.Rotation;
                        go.transform.localScale = Vector3.one * scale;
                        continue;
                    }

                    DestroyProxy(entity);
                }

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
                alive.Add(entity);
                var lt = transforms[i];
                float scale = math.max(0.25f, lt.Scale);

                if (!_proxies.TryGetValue(entity, out var go) || go == null)
                {
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
                }

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
                alive.Add(entity);
                var state = states[i];
                var lt = transforms[i];
                float scale = GemVisualApplier.ComputeVisualScale(math.max(0.25f, state.Value));

                if (!_proxies.TryGetValue(entity, out var go) || go == null)
                {
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
                }

                go.transform.position = GetVisualPosition(entity, lt.Position);
                go.transform.rotation = lt.Rotation;
                go.transform.localScale = Vector3.one * scale;
                GemVisualDiameterRegistry.SetDiameter(entity, GemVisualApplier.ReadWorldDiameter(go, state.Value));
            }
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
