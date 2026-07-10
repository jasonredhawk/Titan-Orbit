using TitanOrbit.ECS;
using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using UnityEngine;

namespace TitanOrbit.ECS.Authoring
{
    /// <summary>
    /// Baker for planet ghost prefabs. Converts GameObject prefab into ECS entity with
    /// <see cref="PlanetState"/>, growth/moon components, and a static sphere physics collider
    /// on <see cref="TitanOrbitPhysicsLayers.WorldStatic"/>. Ships collide with planets via Unity Physics.
    /// </summary>
    public class PlanetGhostAuthoring : MonoBehaviour
    {
        class Baker : Baker<PlanetGhostAuthoring>
        {
            public override void Bake(PlanetGhostAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.Dynamic);
                AddComponent(entity, new PlanetTag());
                AddComponent(entity, new PlanetState());
                AddComponent(entity, new PlanetGrowthState());
                AddComponent(entity, new PlanetGemMoonState());

                // --- Static physics collider ---
                // [UNITY] Geometry radius is unscaled mesh radius; LocalTransform.Scale scales world size.
                // [TITAN-ORBIT] Planets are static bodies — ships bounce, planets do not move.
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
