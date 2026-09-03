using Unity.Entities;
using Unity.Physics;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Shared kill-frame physics teardown for asteroids (server and client).
    /// <para>
    /// [TITAN-ORBIT] Seed-hydrate means clients hide rocks from HitRpc / DestroyRpc while the
    /// server still owns the authoritative body. If the server keeps a <see cref="PhysicsCollider"/>
    /// after Health hits 0, Unity Physics (and ship reconcile) treat empty space as solid — the
    /// mesh is gone on the client, but the hull still rams.
    /// </para>
    /// <para>
    /// [NETCODE] Server asteroids are Instantiated from a ghost prefab even though they are not
    /// relevant. <c>DestroyEntity</c> on a ghost leaves a <c>GhostCleanup</c> zombie until despawn
    /// acks. Unity Physics static worlds can keep that hull. We therefore strip colliders and
    /// squash scale <b>before</b> destroy, and we strip ghost identity at spawn so new rocks
    /// are not ghosts at all (DestroyEntity then fully deletes them).
    /// </para>
    /// Used by ram / bullet kill, <see cref="AsteroidDestructionSystem"/>, and the server
    /// pre-physics strip pass.
    /// </summary>
    public static class AsteroidDeathPhysics
    {
        /// <summary>
        /// Tiny LocalTransform.Scale written on kill. [PHYSICS] A leftover static sphere at the
        /// old radius would still block the ship; 0.01 makes any stale broadphase hull harmless.
        /// </summary>
        public const float CulledTransformScale = 0.01f;

        /// <summary>
        /// Records no-collide collider + scale squash on <paramref name="asteroid"/> and every
        /// <see cref="LinkedEntityGroup"/> child. Playback the ECB after the current query.
        /// Safe during SystemAPI foreach (no immediate structural changes).
        /// Use this on the destroy tick after the original scale has been copied for DestroyRpc.
        /// </summary>
        /// <param name="ecb">Command buffer played back after the gather / damage loop.</param>
        /// <param name="em">EntityManager used only to read current components.</param>
        /// <param name="asteroid">Asteroid root (or any member — children are walked from the root).</param>
        public static void QueueStripAndDisable(
            EntityCommandBuffer ecb,
            EntityManager em,
            Entity asteroid)
        {
            QueueStrip(ecb, em, asteroid, squashScale: true);
        }

        /// <summary>
        /// Swaps <see cref="PhysicsCollider"/> to the shared no-collide blob — keeps LocalTransform.Scale so
        /// <see cref="AsteroidDestructionSystem"/> can still copy the real radius onto DestroyRpc.
        /// Call from ram / bullet kill the same tick Health hits 0.
        /// </summary>
        public static void QueueStripColliders(
            EntityCommandBuffer ecb,
            EntityManager em,
            Entity asteroid)
        {
            QueueStrip(ecb, em, asteroid, squashScale: false);
        }

        /// <summary>
        /// Walks the root and LinkedEntityGroup, recording strip commands.
        /// </summary>
        static void QueueStrip(
            EntityCommandBuffer ecb,
            EntityManager em,
            Entity asteroid,
            bool squashScale)
        {
            if (asteroid == Entity.Null || !em.Exists(asteroid))
                return;

            QueueStripOnEntity(ecb, em, asteroid, squashScale);

            // --- Child colliders (ghost prefab LinkedEntityGroup) ---
            // [ECS/DOTS] We only read the buffer and record ECB ops — no structural mutation here.
            if (!em.HasBuffer<LinkedEntityGroup>(asteroid))
                return;

            var group = em.GetBuffer<LinkedEntityGroup>(asteroid);
            for (int i = 0; i < group.Length; i++)
            {
                Entity member = group[i].Value;
                if (member == asteroid || !em.Exists(member))
                    continue;
                QueueStripOnEntity(ecb, em, member, squashScale);
            }
        }

        /// <summary>
        /// Squashes scale (optional) and removes <see cref="PhysicsCollider"/> on one entity.
        /// </summary>
        static void QueueStripOnEntity(
            EntityCommandBuffer ecb,
            EntityManager em,
            Entity entity,
            bool squashScale)
        {
            if (squashScale && em.HasComponent<LocalTransform>(entity))
            {
                var lt = em.GetComponentData<LocalTransform>(entity);
                if (lt.Scale > CulledTransformScale + 0.001f)
                {
                    lt.Scale = CulledTransformScale;
                    ecb.SetComponent(entity, lt);
                }
            }

            if (em.HasComponent<PhysicsCollider>(entity))
            {
                // Shared no-collide blob — incremental static broadphase updates one leaf.
                // RemoveComponent rebuilt the entire static world (104ms BuildPhysicsWorld).
                ecb.SetComponent(entity, new PhysicsCollider
                {
                    Value = AsteroidClientCullPhysicsSystem.NoCollideCollider,
                });
            }
        }
    }
}
