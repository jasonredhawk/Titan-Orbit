using TitanOrbit.ECS;
using Unity.Entities;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// [HYBRID] Marks a client asteroid as destroyed for collision the moment HitRpc
    /// hides the mesh — before a delayed DestroyRpc / Health write catches up.
    /// <para>
    /// Evidence (session 74383c): after hide, logs showed <c>hidden:true dead:false colliderOn:false</c>
    /// — Health still &gt;0 so toroidal resolve could treat the rock as solid. Delegates to
    /// <see cref="ClientLocalAsteroidCombatSync.CullPhysics"/> so presentation-thread hide and
    /// sim-group soft-destroy share one cull (LinkedEntityGroup + no-collide blob + scale squash).
    /// SimulationSystemGroup then removes PhysicsCollider; this presentation path must not.
    /// </para>
    /// </summary>
    public static class ClientAsteroidCollisionCull
    {
        /// <summary>
        /// Tags the entity as culled and forces a no-collide physics hull on the root and
        /// every LinkedEntityGroup child. Safe to call repeatedly.
        /// </summary>
        /// <param name="asteroidEntity">Asteroid entity from HitRpc lookup.</param>
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

            // Same cull as AsteroidDestroyedRpc / HitRpc sim apply — do not diverge.
            ClientLocalAsteroidCombatSync.CullPhysics(em, asteroidEntity);
            return em.HasComponent<AsteroidClientCulledTag>(asteroidEntity);
        }
    }
}
