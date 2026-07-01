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
    /// </summary>
    public class EcsWorldVisualizer : MonoBehaviour
    {
        readonly Dictionary<Entity, GameObject> _proxies = new Dictionary<Entity, GameObject>();

        void LateUpdate()
        {
            var world = PickVisualizationWorld();
            if (world == null || !world.IsCreated)
                return;

            var em = world.EntityManager;
            var alive = new HashSet<Entity>();

            DrawTagged<ShipTag>(em, alive, PrimitiveType.Capsule, Color.cyan, 1f);
            DrawTagged<PlanetTag>(em, alive, PrimitiveType.Sphere, new Color(0.35f, 0.55f, 1f), 1f);
            DrawTagged<AsteroidTag>(em, alive, PrimitiveType.Sphere, new Color(0.55f, 0.45f, 0.35f), 0.6f);
            DrawTagged<GemTag>(em, alive, PrimitiveType.Sphere, Color.yellow, 0.25f);

            var remove = new List<Entity>();
            foreach (var kv in _proxies)
            {
                if (!alive.Contains(kv.Key))
                    remove.Add(kv.Key);
            }

            foreach (var entity in remove)
            {
                if (_proxies.TryGetValue(entity, out var go) && go != null)
                    Destroy(go);
                _proxies.Remove(entity);
            }
        }

        static World PickVisualizationWorld()
        {
            if (EcsGameBridge.IsLocalHost() &&
                ClientServerBootstrap.ServerWorld != null &&
                ClientServerBootstrap.ServerWorld.IsCreated)
                return ClientServerBootstrap.ServerWorld;

            return ClientServerBootstrap.ClientWorld ?? ClientServerBootstrap.ServerWorld;
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

                if (typeof(T) == typeof(ShipTag) &&
                    em.HasComponent<ShipState>(entity))
                {
                    var ship = em.GetComponentData<ShipState>(entity);
                    color = TeamColor(ship.Team);
                }

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
                if (kv.Value != null)
                    Destroy(kv.Value);
            }
            _proxies.Clear();
        }
    }
}
