using TitanOrbit.Generation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Physics;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Creates four static PhysX wall boxes at the map edges so ships stay in the arena
    /// while wrap is off. Also patches baked world-body filters so ships collide with
    /// planets / asteroids after <see cref="TitanOrbitPhysicsLayers.WorldStatic"/> gained Ships.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    public partial struct MapEdgeWallEnsureSystem : ISystem
    {
        const float WallThickness = 8f;
        const float WallHeight = 40f;

        bool _wallsCreated;
        bool _filtersPatched;

        /// <summary>
        /// Spawns walls once, then one-shot filter patch (client waits on map-body settle).
        /// Disables when both are done.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            if (!TryResolveMapSize(out float mapW, out float mapH))
                return;

            if (!_wallsCreated)
                CreateWalls(ref state, mapW, mapH);

            if (!_filtersPatched)
            {
                if (state.World.IsClient() && ClientJoinSettleCache.ShouldSkipMapBodyQueries)
                    return;
                PatchWorldFilters(ref state);
            }

            if (_wallsCreated && _filtersPatched)
                state.Enabled = false;
        }

        bool TryResolveMapSize(out float mapW, out float mapH)
        {
            mapW = 0f;
            mapH = 0f;
            if (SystemAPI.TryGetSingleton(out MapStateSingleton map) &&
                ToroidalMapEcs.IsValidMapSize(map.MapWidth, map.MapHeight))
            {
                mapW = map.MapWidth;
                mapH = map.MapHeight;
                return true;
            }

            return ToroidalMapEcs.TryGetMapSize(out mapW, out mapH);
        }

        void CreateWalls(ref SystemState state, float mapW, float mapH)
        {
            var em = state.EntityManager;
            int existing = 0;
            foreach (var _ in SystemAPI.Query<RefRO<MapEdgeWallTag>>())
                existing++;
            if (existing >= 4)
            {
                _wallsCreated = true;
                return;
            }

            float halfW = mapW * 0.5f;
            float halfH = mapH * 0.5f;
            float t = WallThickness;

            // +X / −X / +Z / −Z. Extra length covers corners so ships cannot slip the seam.
            SpawnWall(em, new float3(halfW + t * 0.5f, 0f, 0f), new float3(t, WallHeight, mapH + t * 2f));
            SpawnWall(em, new float3(-halfW - t * 0.5f, 0f, 0f), new float3(t, WallHeight, mapH + t * 2f));
            SpawnWall(em, new float3(0f, 0f, halfH + t * 0.5f), new float3(mapW + t * 2f, WallHeight, t));
            SpawnWall(em, new float3(0f, 0f, -halfH - t * 0.5f), new float3(mapW + t * 2f, WallHeight, t));
            _wallsCreated = true;
        }

        static void SpawnWall(EntityManager em, float3 center, float3 size)
        {
            var material = Unity.Physics.Material.Default;
            material.CollisionResponse = CollisionResponsePolicy.Collide;
            material.Restitution = 0.5f;
            material.Friction = 0.15f;

            var blob = Unity.Physics.BoxCollider.Create(
                new BoxGeometry
                {
                    Center = float3.zero,
                    Size = size,
                    Orientation = quaternion.identity,
                    BevelRadius = 0.05f,
                },
                TitanOrbitPhysicsLayers.WorldStatic,
                material);

            Entity e = em.CreateEntity();
            em.AddComponentData(e, new MapEdgeWallTag());
            em.AddComponentData(e, LocalTransform.FromPosition(center));
            em.AddComponentData(e, new PhysicsCollider { Value = blob });
            em.AddSharedComponent(e, new PhysicsWorldIndex(0));
        }

        void PatchWorldFilters(ref SystemState state)
        {
            // Baked blobs still have the old WorldStatic (no Ships). Both filter sides must agree.
            // Shared bake blobs: one SetCollisionFilter updates every instance of that prefab.
            int patched = 0;
            foreach (var collider in SystemAPI.Query<RefRW<PhysicsCollider>>().WithAll<PlanetTag>())
            {
                SetWorldFilter(ref collider.ValueRW);
                patched++;
            }

            foreach (var collider in SystemAPI.Query<RefRW<PhysicsCollider>>().WithAll<AsteroidTag>())
            {
                SetWorldFilter(ref collider.ValueRW);
                patched++;
            }

            foreach (var collider in SystemAPI.Query<RefRW<PhysicsCollider>>().WithAll<PlanetGemMoonColliderTag>())
            {
                SetWorldFilter(ref collider.ValueRW);
                patched++;
            }

            if (patched > 0)
                _filtersPatched = true;
        }

        static void SetWorldFilter(ref PhysicsCollider collider)
        {
            if (!collider.Value.IsCreated)
                return;
            collider.Value.Value.SetCollisionFilter(TitanOrbitPhysicsLayers.WorldStatic);
        }
    }
}
