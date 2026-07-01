using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.ECS;
using Unity.Entities;
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
        [SerializeField] GameObject shipVisualPrefab;
        [SerializeField] float defaultMuzzleOffset = 2f;

        readonly Dictionary<Entity, GameObject> _proxies = new Dictionary<Entity, GameObject>();
        readonly Dictionary<Entity, int> _proxyNetworkIds = new Dictionary<Entity, int>();

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

            SyncShipProxyTransforms(em, alive);
            DrawTagged<PlanetTag>(em, alive, PrimitiveType.Sphere, new Color(0.35f, 0.55f, 1f), 1f);
            DrawTagged<AsteroidTag>(em, alive, PrimitiveType.Sphere, new Color(0.55f, 0.45f, 0.35f), 0.6f);
            DrawTagged<GemTag>(em, alive, PrimitiveType.Sphere, Color.yellow, 0.25f);
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

        static World PickVisualizationWorld()
        {
            if (EcsGameBridge.IsLocalHost() &&
                ClientServerBootstrap.ServerWorld != null &&
                ClientServerBootstrap.ServerWorld.IsCreated)
                return ClientServerBootstrap.ServerWorld;

            return ClientServerBootstrap.ClientWorld ?? ClientServerBootstrap.ServerWorld;
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
                if (_proxies.TryGetValue(entity, out var existing) && existing != null)
                    continue;

                var lt = transforms[i];
                float scale = Mathf.Max(0.25f, lt.Scale);

                Color color = Color.cyan;
                if (em.HasComponent<ShipState>(entity))
                {
                    var ship = em.GetComponentData<ShipState>(entity);
                    color = TeamColor(ship.Team);
                }

                float muzzleOffset = defaultMuzzleOffset;
                if (em.HasComponent<ShipWeaponConfig>(entity))
                    muzzleOffset = em.GetComponentData<ShipWeaponConfig>(entity).MuzzleOffset;

                int networkId = 0;
                if (em.HasComponent<GhostOwner>(entity))
                    networkId = em.GetComponentData<GhostOwner>(entity).NetworkId;

                var go = CreateShipProxy(entity, networkId, color, scale, muzzleOffset);
                go.transform.position = lt.Position;
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
                float scale = Mathf.Max(0.25f, lt.Scale);
                go.transform.position = lt.Position;
                go.transform.rotation = lt.Rotation;
                go.transform.localScale = Vector3.one * scale;

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

        GameObject CreateShipProxy(Entity entity, int networkId, Color color, float scale, float muzzleOffset)
        {
            GameObject go;
            if (shipVisualPrefab != null)
            {
                go = Instantiate(shipVisualPrefab);
                go.name = "ShipTagProxy";
                StripPhysicsAndNetworking(go);
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
                    renderer.material.color = color;
                }
            }

            ShipWeaponMountCollector.EnsureWeaponMountsOnHierarchy(go.transform, muzzleOffset);

            if (networkId > 0)
            {
                ShipWeaponProxyRegistry.Register(networkId, go.transform);
                _proxyNetworkIds[entity] = networkId;
            }

            _proxies[entity] = go;
            return go;
        }

        static void StripPhysicsAndNetworking(GameObject root)
        {
            foreach (var col in root.GetComponentsInChildren<Collider>(true))
                Destroy(col);
            foreach (var rb in root.GetComponentsInChildren<Rigidbody>(true))
                Destroy(rb);
            foreach (var net in root.GetComponentsInChildren<Component>(true))
            {
                if (net == null)
                    continue;
                var typeName = net.GetType().Name;
                if (typeName.Contains("Network") || typeName.Contains("Netcode") || typeName.Contains("ClientNetwork"))
                    Destroy(net);
            }
        }

        void DestroyProxy(Entity entity)
        {
            if (_proxies.TryGetValue(entity, out var go))
            {
                if (_proxyNetworkIds.TryGetValue(entity, out int networkId) && go != null)
                    ShipWeaponProxyRegistry.Unregister(networkId, go.transform);
                if (go != null)
                    Destroy(go);
                _proxies.Remove(entity);
                _proxyNetworkIds.Remove(entity);
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
                float scale = Mathf.Max(0.1f, tracer.Scale > 0f ? tracer.Scale : lt.Scale);
                var color = TeamColor((TeamId)tracer.OwnerTeam);
                if (tracer.OwnerTeam == 0)
                    color = new Color(1f, 0.9f, 0.35f);

                if (!_proxies.TryGetValue(entity, out var go) || go == null)
                {
                    go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    go.name = "BulletTracerProxy";
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

                go.transform.position = lt.Position;
                go.transform.rotation = lt.Rotation;
                go.transform.localScale = Vector3.one * scale;
            }
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

                go.transform.position = lt.Position;
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
        }
    }
}
