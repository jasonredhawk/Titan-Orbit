using TitanOrbit.ECS;
using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using UnityEngine;

namespace TitanOrbit.ECS.Authoring
{
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

                // Static Unity Physics body. Geometry radius is the unscaled mesh radius;
                // Unity Physics applies LocalTransform.Scale at runtime for the world radius.
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
