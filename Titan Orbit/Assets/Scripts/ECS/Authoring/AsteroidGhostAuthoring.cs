using TitanOrbit.ECS;
using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using UnityEngine;

namespace TitanOrbit.ECS.Authoring
{
    /// <summary>
    /// Baker for asteroid ghost prefabs. Adds mineable <see cref="AsteroidState"/> and a static
    /// sphere collider on the World physics layer. Asteroids block ship movement but gems do not.
    /// </summary>
    public class AsteroidGhostAuthoring : MonoBehaviour
    {
        class Baker : Baker<AsteroidGhostAuthoring>
        {
            public override void Bake(AsteroidGhostAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.Dynamic);
                AddComponent(entity, new AsteroidTag());
                AddComponent(entity, new AsteroidState());

                // --- Static physics collider (see PlanetGhostAuthoring for layer notes) ---
                var material = Unity.Physics.Material.Default;
                material.Restitution = 0.5f;
                var collider = Unity.Physics.SphereCollider.Create(
                    new SphereGeometry { Center = float3.zero, Radius = BodyCollisionMath.AsteroidMeshBaseRadius },
                    TitanOrbitPhysicsLayers.WorldStatic,
                    material);
                AddBlobAsset(ref collider, out _);
                AddComponent(entity, new PhysicsCollider { Value = collider });
                AddSharedComponent(entity, new PhysicsWorldIndex(0));
            }
        }
    }
}
