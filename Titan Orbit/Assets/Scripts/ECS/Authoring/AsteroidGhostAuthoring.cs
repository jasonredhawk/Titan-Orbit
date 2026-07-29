using TitanOrbit.ECS;
using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using UnityEngine;

namespace TitanOrbit.ECS.Authoring
{
    /// <summary>
    /// [UNITY] MonoBehaviour authoring on asteroid ghost prefabs. The nested Baker converts this
    /// GameObject into an ECS entity with <see cref="AsteroidTag"/>, <see cref="AsteroidState"/>,
    /// and a static sphere physics collider on <see cref="TitanOrbitPhysicsLayers.WorldStatic"/>.
    /// [PHYSICS] Asteroids block ship movement; gems do not collide with ships. Baked into SubScenes
    /// for NetCode ghost replication. Server map generation instantiates from <see cref="GamePrefabs.Asteroid"/>.
    /// </summary>
    public class AsteroidGhostAuthoring : MonoBehaviour
    {
        /// <summary>
        /// [ECS/DOTS] Nested Baker — Unity DOTS converts this MonoBehaviour hierarchy into entity
        /// components at SubScene bake time (not at runtime).
        /// </summary>
        class Baker : Baker<AsteroidGhostAuthoring>
        {
            /// <summary>
            /// [ECS/DOTS] Registers asteroid tag, mineable state, and static physics hull collider.
            /// </summary>
            public override void Bake(AsteroidGhostAuthoring authoring)
            {
                // --- Entity registration ---
                // [ECS/DOTS] Dynamic transform — position set at spawn by map generation ECB.
                var entity = GetEntity(TransformUsageFlags.Dynamic);
                AddComponent(entity, new AsteroidTag());
                AddComponent(entity, new AsteroidState());

                // --- Client hybrid visual queue ---
                // [NETCODE] MapBodyHybridVisualPending is GhostPrefabType.Client — stripped on server.
                // [TITAN-ORBIT] Instantiates already carries Pending so join can drain GO proxies
                // without ToEntityArray-all Instantiated asteroids (Windows Crash!!!).
                AddComponent(entity, new MapBodyHybridVisualPending());

                // --- Static physics collider ---
                // [PHYSICS] WorldStatic layer — collides with Ship layer only (see TitanOrbitPhysicsLayers).
                // [TITAN-ORBIT] Friction / restitution match AsteroidSettings defaults; runtime spawn
                // rebuilds from AsteroidSettingsCache so Inspector tweaks apply without rebake.
                var collider = AsteroidColliderMaterialLogic.CreateWorldStaticSphere(
                    AsteroidColliderMaterialLogic.DefaultFriction,
                    AsteroidColliderMaterialLogic.DefaultRestitution);
                AddBlobAsset(ref collider, out _);
                AddComponent(entity, new PhysicsCollider { Value = collider });
                AddSharedComponent(entity, new PhysicsWorldIndex(0));
            }
        }
    }
}
