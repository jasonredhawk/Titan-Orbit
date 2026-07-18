using TitanOrbit.ECS;
using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using UnityEngine;

namespace TitanOrbit.ECS.Authoring
{
    /// <summary>
    /// [UNITY] MonoBehaviour authoring on planet ghost prefabs. Baker converts GameObject prefab into
    /// ECS entity with <see cref="PlanetTag"/>, <see cref="PlanetState"/>, growth/moon components,
    /// and a static sphere physics collider on <see cref="TitanOrbitPhysicsLayers.WorldStatic"/>.
    /// [PHYSICS] Ships collide with planets via Unity Physics; planets do not move. Baked into SubScenes
    /// for NetCode ghost replication.
    /// </summary>
    public class PlanetGhostAuthoring : MonoBehaviour
    {
        /// <summary>[ECS/DOTS] Nested Baker for planet ghost entity components.</summary>
        class Baker : Baker<PlanetGhostAuthoring>
        {
            /// <summary>
            /// [ECS/DOTS] Registers planet tag, state, growth/moon components, and static collider.
            /// </summary>
            public override void Bake(PlanetGhostAuthoring authoring)
            {
                // --- Core planet components ---
                var entity = GetEntity(TransformUsageFlags.Dynamic);
                AddComponent(entity, new PlanetTag());
                AddComponent(entity, new PlanetState());
                AddComponent(entity, new PlanetGrowthState());
                AddComponent(entity, new PlanetGemMoonState());

                // --- Client hybrid visual queue ---
                // [NETCODE] Pending is GhostPrefabType.Client only — see MapBodyHybridVisualPending.
                // [TITAN-ORBIT] Avoids join-time ToEntityArray mark scans (Windows Crash!!!).
                AddComponent(entity, new MapBodyHybridVisualPending());

                // --- Static physics collider ---
                // [UNITY] Geometry radius is unscaled mesh radius; LocalTransform.Scale scales world size.
                // [PHYSICS] WorldStatic layer — ships bounce, planets never integrate position.
                // [TITAN-ORBIT] Restitution ~0.5 for ship bounce off planet hulls.
                var material = Unity.Physics.Material.Default;
                material.Restitution = 0.5f;
                var collider = Unity.Physics.SphereCollider.Create(
                    new SphereGeometry { Center = float3.zero, Radius = BodyCollisionMath.PlanetMeshBaseRadius },
                    TitanOrbitPhysicsLayers.WorldStatic,
                    material);
                AddBlobAsset(ref collider, out _);
                AddComponent(entity, new PhysicsCollider { Value = collider });
                AddSharedComponent(entity, new PhysicsWorldIndex(0));
            }
        }
    }
}
