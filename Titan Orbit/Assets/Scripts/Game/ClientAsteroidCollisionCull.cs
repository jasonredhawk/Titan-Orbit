using TitanOrbit.ECS;
using Unity.Entities;
using Unity.Physics;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// [HYBRID] Marks a client asteroid ghost as destroyed for collision the moment HitRpc
    /// hides the mesh — before NetCode despawn / Health snapshot catches up.
    /// <para>
    /// Evidence (session 74383c): after hide, logs showed <c>hidden:true dead:false colliderOn:false</c>
    /// — ghost Health still &gt;0 so toroidal resolve could treat the rock as solid. We add
    /// <see cref="AsteroidClientCulledTag"/> (not ghosted) and swap <see cref="PhysicsCollider"/>
    /// to a shared zero-filter blob (never mutate bake-shared spheres).
    /// </para>
    /// </summary>
    public static class ClientAsteroidCollisionCull
    {
        /// <summary>
        /// Tags the entity as culled and forces a no-collide physics hull.
        /// Safe to call repeatedly.
        /// </summary>
        /// <param name="asteroidEntity">Asteroid ghost from HitRpc lookup.</param>
        /// <returns>True when cull state was applied or already present.</returns>
        public static bool TryDisablePhysicsCollider(Entity asteroidEntity)
        {
            if (asteroidEntity == Entity.Null)
                return false;

            var world = EcsGameBridge.ClientWorld;
            if (world == null || !world.IsCreated)
                return false;

            var em = world.EntityManager;
            if (!em.Exists(asteroidEntity) || !em.HasComponent<AsteroidTag>(asteroidEntity))
                return false;

            bool changed = false;

            // --- Non-ghost tag (Health snapshots cannot clear this) ---
            if (!em.HasComponent<AsteroidClientCulledTag>(asteroidEntity))
            {
                em.AddComponent<AsteroidClientCulledTag>(asteroidEntity);
                changed = true;
            }

            // --- Zero-filter collider (do not mutate shared bake blobs) ---
            var noCollide = AsteroidClientCullPhysicsSystem.NoCollideCollider;
            if (em.HasComponent<PhysicsCollider>(asteroidEntity))
            {
                var pc = em.GetComponentData<PhysicsCollider>(asteroidEntity);
                if (pc.Value != noCollide)
                {
                    em.SetComponentData(asteroidEntity, new PhysicsCollider { Value = noCollide });
                    changed = true;
                }
            }

            return changed || em.HasComponent<AsteroidClientCulledTag>(asteroidEntity);
        }
    }
}
