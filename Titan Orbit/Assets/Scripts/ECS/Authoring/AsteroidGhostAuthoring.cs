using TitanOrbit.ECS;
using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using UnityEngine;

namespace TitanOrbit.ECS.Authoring
{
    public class AsteroidGhostAuthoring : MonoBehaviour
    {
        class Baker : Baker<AsteroidGhostAuthoring>
        {
            public override void Bake(AsteroidGhostAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.Dynamic);
                AddComponent(entity, new AsteroidTag());
                AddComponent(entity, new AsteroidState());

                // Static Unity Physics body. Geometry radius is the unscaled mesh radius;
                // Unity Physics applies LocalTransform.Scale at runtime for the world radius.
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
