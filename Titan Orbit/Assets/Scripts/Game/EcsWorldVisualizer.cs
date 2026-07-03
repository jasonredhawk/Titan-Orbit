using System.Collections.Generic;
using TitanOrbit.Audio;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Entities;
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
    /// Client-side primitive proxies so baked ghost entities are visible before Entities Graphics is wired.
    /// Ship proxies include weapon mount children so bullet direction uses weapon forward, not mouse aim.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public class EcsWorldVisualizer : MonoBehaviour
    {
        const string DefaultShipFamilyAssetPath = "Assets/Prefabs/Ships/AstroEagle/AstroEagleShipFamily.asset";
        const string DefaultHomePlanetPath = "Assets/Prefabs/HomePlanet.prefab";
        const string DefaultNeutralPlanetPath = "Assets/Prefabs/Planet.prefab";
        const string DefaultAsteroidPath = "Assets/Prefabs/Asteroid.prefab";
        const string DefaultGemPath = "Assets/Prefabs/Gem.prefab";
        const string DefaultPeopleTransportPath = "Assets/Prefabs/PeopleTransport.prefab";

        [Header("Ships")]
        [SerializeField] ShipFamilyDefinition shipFamily;
        [SerializeField] GameObject shipVisualPrefab;
        [SerializeField] float shipVisualScale = BodyCollisionMath.ShipPresentationScale;
        [SerializeField] float defaultMuzzleOffset = 2f;

        [Header("Planets & Bodies")]
        [SerializeField] GameObject homePlanetVisualPrefab;
        [SerializeField] GameObject neutralPlanetVisualPrefab;
        [SerializeField] GameObject asteroidVisualPrefab;
        [SerializeField] GameObject gemVisualPrefab;
        [SerializeField] GameObject peopleTransportVisualPrefab;
        [SerializeField] PlanetMaterialPool planetMaterialPool;

        [Header("Combat VFX")]
        [SerializeField] BulletVfxBank bulletVfxBank;
        [SerializeField] int defaultBulletBankIndex;
        [SerializeField] float defaultBulletScaleMultiplier = 1f;

        [Header("Ship Propulsion VFX")]
        [SerializeField] ShipPropulsionVisualApplier.Settings propulsionVfxSettings;

        readonly Dictionary<Entity, GameObject> _proxies = new Dictionary<Entity, GameObject>();
        readonly Dictionary<Entity, ClientBulletStretchVisual> _bulletStretchVisuals = new Dictionary<Entity, ClientBulletStretchVisual>();
        readonly Dictionary<Entity, int> _proxyNetworkIds = new Dictionary<Entity, int>();
        readonly Dictionary<Entity, int> _proxyShipLevels = new Dictionary<Entity, int>();
        readonly Dictionary<Entity, TeamId> _proxyTeams = new Dictionary<Entity, TeamId>();
        readonly Dictionary<Entity, PlanetVisualKey> _proxyPlanetVisuals = new Dictionary<Entity, PlanetVisualKey>();

        Vector3 _toroidalReference;
        bool _hasToroidalReference;

        struct PlanetVisualKey : System.IEquatable<PlanetVisualKey>
        {
            public bool IsHome;
            public TeamId Team;
            public int PlanetLevel;
            public int PlanetId;

            public bool Equals(PlanetVisualKey other) =>
                IsHome == other.IsHome && Team == other.Team && PlanetLevel == other.PlanetLevel && PlanetId == other.PlanetId;
        }

        void Awake()
        {
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

        void Update()
        {
            var world = PickVisualizationWorld();
            if (world == null || !world.IsCreated)
                return;

            EnsureShipProxies(world.EntityManager);
        }

        void LateUpdate()
        {
            var world = PickVisualizationWorld();
            if (world == null || !world.IsCreated)
                return;

            var em = world.EntityManager;
            var alive = new HashSet<Entity>();

            BeginToroidalFrame(em, alive);

            SyncShipProxyTransforms(em, alive);
            DrawPlanets(em, alive);
            DrawAsteroids(em, alive);
            DrawGems(em, alive);
            GemVisualDiameterRegistry.RemoveStale(alive);
            DrawPeopleTransports(em, alive);
            ProcessBulletHitEvents(em);
            DrawBullets(em, alive);

            var remove = new List<Entity>();
            foreach (var kv in _proxies)
            {
                if (!alive.Contains(kv.Key))
                    remove.Add(kv.Key);
            }

            foreach (var entity in remove)
                DestroyProxy(entity);
        }

        static World PickVisualizationWorld() => EcsGameBridge.GetVisualizationWorld();

        void BeginToroidalFrame(EntityManager em, HashSet<Entity> alive)
        {
            ToroidalDisplay.SyncMapSize(em);
            _hasToroidalReference = ToroidalDisplay.TryGetReferencePosition(out _toroidalReference);
            ToroidalDisplay.PruneStale(alive);
        }

        Vector3 GetVisualPosition(Entity entity, EntityManager em, float3 logicalPos, bool forceLogical = false)
        {
            if (forceLogical || ToroidalDisplay.IsLocalPlayerShip(em, entity))
                return logicalPos;

            if (!_hasToroidalReference && !ToroidalDisplay.TryGetReferencePosition(out _toroidalReference))
                return logicalPos;

            return ToroidalDisplay.ToDisplayPositionWithHysteresis(entity, logicalPos, _toroidalReference);
        }

        Vector3 GetVisualPosition(Entity entity, float3 logicalPos)
        {
            if (!_hasToroidalReference && !ToroidalDisplay.TryGetReferencePosition(out _toroidalReference))
                return logicalPos;

            return ToroidalDisplay.ToDisplayPositionWithHysteresis(entity, logicalPos, _toroidalReference);
        }

        void EnsureShipProxies(EntityManager em)
        {
            using var query = em.CreateEntityQuery(
                ComponentType.ReadOnly<ShipTag>(),
                ComponentType.ReadOnly<LocalTransform>());
            using var entities = query.ToEntityArray(Unity.Collections.Allocator.Temp);
            using var transforms = query.ToComponentDataArray<LocalTransform>(Unity.Collections.Allocator.Temp);

            for (int i = 0; i < entities.Length; i++)
            {
                var entity = entities[i];
                var lt = transforms[i];
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
                go.transform.position = GetVisualPosition(entity, em, lt.Position);
                go.transform.rotation = lt.Rotation;
                go.transform.localScale = Vector3.one * scale;
            }
        }

        void SyncShipProxyTransforms(EntityManager em, HashSet<Entity> alive)
        {
            using var query = em.CreateEntityQuery(
                ComponentType.ReadOnly<ShipTag>(),
                ComponentType.ReadOnly<LocalTransform>());
            using var entities = query.ToEntityArray(Unity.Collections.Allocator.Temp);
            using var transforms = query.ToComponentDataArray<LocalTransform>(Unity.Collections.Allocator.Temp);

            for (int i = 0; i < entities.Length; i++)
            {
                var entity = entities[i];
                alive.Add(entity);
                if (!_proxies.TryGetValue(entity, out var go) || go == null)
                    continue;

                var lt = transforms[i];
                float scale = Mathf.Max(0.25f, lt.Scale) * shipVisualScale;

                bool skipTransformSync = false;
                if (em.HasComponent<ShipMoonDockState>(entity))
                {
                    var moonDock = em.GetComponentData<ShipMoonDockState>(entity);
                    skipTransformSync = moonDock.MoonPlanetId != 0 && moonDock.LandingProgress > 0.001f;
                }

                if (!skipTransformSync)
                {
                    go.transform.position = GetVisualPosition(entity, em, lt.Position);
                    go.transform.rotation = lt.Rotation;
                    go.transform.localScale = Vector3.one * scale;
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
            if (!go.GetComponent<ShipHullColliderCache>())
                ShipHullColliderCollector.EnsureCacheOnHull(go.transform);

            if (networkId > 0)
            {
                ShipWeaponProxyRegistry.Register(networkId, go.transform);
                _proxyNetworkIds[entity] = networkId;
            }

            _proxyShipLevels[entity] = shipLevel;
            _proxyTeams[entity] = team;
            _proxies[entity] = go;

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

        static ShipFamilyDefinition LoadDefaultShipFamily()
        {
#if UNITY_EDITOR
            return UnityEditor.AssetDatabase.LoadAssetAtPath<ShipFamilyDefinition>(DefaultShipFamilyAssetPath);
#else
            return null;
#endif
        }

        static GameObject LoadDefaultPrefab(string assetPath)
        {
#if UNITY_EDITOR
            return UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
#else
            return null;
#endif
        }

        void DestroyProxy(Entity entity)
        {
            if (_proxies.TryGetValue(entity, out var go))
            {
                ToroidalDisplay.RemoveEntity(entity);
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

        void DrawPeopleTransports(EntityManager em, HashSet<Entity> alive)
        {
            using var query = em.CreateEntityQuery(
                ComponentType.ReadOnly<PeopleTransportTag>(),
                ComponentType.ReadOnly<PeopleTransportState>(),
                ComponentType.ReadOnly<LocalTransform>());
            using var entities = query.ToEntityArray(Unity.Collections.Allocator.Temp);
            using var states = query.ToComponentDataArray<PeopleTransportState>(Unity.Collections.Allocator.Temp);
            using var transforms = query.ToComponentDataArray<LocalTransform>(Unity.Collections.Allocator.Temp);

            for (int i = 0; i < entities.Length; i++)
            {
                var entity = entities[i];
                alive.Add(entity);
                var state = states[i];
                var lt = transforms[i];
                float scale = PeopleTransportVisualApplier.ComputeWorldScale(math.max(1f, state.Amount));
                var team = (TeamId)state.Team;

                if (!_proxies.TryGetValue(entity, out var go) || go == null)
                {
                    go = PeopleTransportVisualApplier.CreateVisual(peopleTransportVisualPrefab, state.Amount, team);
                    _proxies[entity] = go;
                }

                go.transform.position = GetVisualPosition(entity, lt.Position);
                go.transform.rotation = lt.Rotation;
                go.transform.localScale = Vector3.one * scale;
            }
        }

        static void ApplyTeamColorToVisual(GameObject go, TeamId team)
        {
            var color = TeamColor(team);
            foreach (var renderer in go.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null) continue;
                renderer.material = WorldBodyVisualApplier.CreateLitMaterial(color);
            }
        }

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

                    Vector3 spawnPos = GetVisualPosition(entity, tracer.SpawnPosition);
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

                go.transform.position = GetVisualPosition(entity, lt.Position);
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
