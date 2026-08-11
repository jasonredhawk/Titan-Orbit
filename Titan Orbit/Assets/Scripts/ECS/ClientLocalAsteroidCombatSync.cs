using System.Collections.Generic;
using TitanOrbit.Generation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [TITAN-ORBIT] Client-side combat sync for seed-hydrated asteroids (not ghost-relevant).
    /// Server destroys rocks via <see cref="AsteroidDestroyedRpc"/> / HitRpc HP; clients must
    /// remove collision + the hybrid GO or the player sees a phantom rock (server already
    /// gone → no more damage HitRpcs, solid collider → ship stuck / tunnels).
    /// <para>
    /// Kill frames use a <b>soft destroy</b> (mark dead, no-collide collider, queue GO teardown)
    /// — not <c>EntityManager.DestroyEntity</c>. Immediate structural teardown of Instantiates
    /// <see cref="LinkedEntityGroup"/> hierarchies mid-combat corrupted the client physics /
    /// prediction world and froze the local ship. Hard <c>DestroyEntity</c> runs only when a
    /// respawn RPC is about to Instantiates a replacement at the same pose.
    /// </para>
    /// </summary>
    public static class ClientLocalAsteroidCombatSync
    {
        /// <summary>Scratch for registry walks (quarantine-safe — never asteroid ToEntityArray).</summary>
        static readonly List<Entity> RegistryScratch = new List<Entity>(512);

        /// <summary>
        /// Entities whose hybrid GO must be torn down on the Game/presentation thread.
        /// ECS cannot reference <c>EcsWorldVisualizer</c> — visualizer drains this each sync.
        /// </summary>
        static readonly List<Entity> PendingProxyDestroy = new List<Entity>(32);

        /// <summary>
        /// Fallback match radius when scale is unknown (small rocks). Prefer
        /// <see cref="MatchRadiusForScale"/>.
        /// </summary>
        public const float MatchRadius = 2.5f;

        /// <summary>Hard cap so a bad Scale on the wire cannot match the whole map.</summary>
        const float MaxMatchRadius = 48f;

        /// <summary>
        /// Toroidal match radius from asteroid scale — HitRpc hits land on the surface, not the
        /// center. A fixed 2.5u radius missed large rocks → cull/hide never ran → desync phantom.
        /// </summary>
        public static float MatchRadiusForScale(float scale)
        {
            float hitRadius = BulletCollision.AsteroidHitRadius(math.max(0.01f, scale));
            // Slack for network/display jitter + slightly oversized meshes.
            return math.min(MaxMatchRadius, hitRadius + 1.25f);
        }

        /// <summary>Queues a hybrid proxy teardown for the visualizer (idempotent).</summary>
        public static void QueueProxyDestroy(Entity entity)
        {
            if (entity == Entity.Null)
                return;
            if (!PendingProxyDestroy.Contains(entity))
                PendingProxyDestroy.Add(entity);
        }

        /// <summary>
        /// Copies pending proxy-destroy entities into <paramref name="dst"/> and clears the queue.
        /// Call from <c>EcsWorldVisualizer</c> only.
        /// </summary>
        public static void DrainPendingProxyDestroys(List<Entity> dst)
        {
            if (dst == null)
                return;
            dst.Clear();
            if (PendingProxyDestroy.Count == 0)
                return;
            dst.AddRange(PendingProxyDestroy);
            PendingProxyDestroy.Clear();
        }

        /// <summary>
        /// Writes authoritative Health / IsDestroyed onto the nearest live local asteroid at
        /// <paramref name="hitPosition"/>. On kill, soft-destroys (cull + hide) — no DestroyEntity.
        /// </summary>
        public static Entity ApplyHitAtPosition(
            EntityManager em,
            float3 hitPosition,
            float asteroidHealthAfter)
        {
            if (asteroidHealthAfter < 0f || !em.World.IsCreated)
                return Entity.Null;

            if (!TryFindNearestAsteroid(em, hitPosition, liveOnly: true, out Entity asteroid, out _))
                return Entity.Null;

            ApplyAuthoritativeHealth(em, asteroid, asteroidHealthAfter);

            if (asteroidHealthAfter <= 0.01f)
                SoftDestroyLocalAsteroidEntity(em, asteroid);

            return asteroid;
        }

        /// <summary>
        /// Sets <see cref="AsteroidState.Health"/> from the server HitRpc value and flags destroy
        /// when Health reaches 0. Culls collision on kill frames.
        /// </summary>
        public static void ApplyAuthoritativeHealth(
            EntityManager em,
            Entity asteroid,
            float asteroidHealthAfter)
        {
            if (!em.Exists(asteroid) || !em.HasComponent<AsteroidState>(asteroid))
                return;

            var state = em.GetComponentData<AsteroidState>(asteroid);
            float hp = math.max(0f, asteroidHealthAfter);
            state.Health = hp;
            if (hp <= 0.01f)
            {
                state.IsDestroyed = true;
                state.Health = 0f;
            }

            em.SetComponentData(asteroid, state);

            if (state.IsDestroyed)
                CullPhysics(em, asteroid);
        }

        /// <summary>
        /// Soft-destroys every local asteroid near <paramref name="position"/> (toroidal surface
        /// radius). Used by <see cref="AsteroidDestroyedRpc"/> — keeps the ECS entity alive as a
        /// culled zombie until respawn hard-destroys it.
        /// </summary>
        public static int SoftDestroyLocalAsteroidsNear(
            EntityManager em,
            float3 position,
            float scaleHint = 0f)
        {
            return ForEachNear(em, position, scaleHint, SoftDestroyLocalAsteroidEntity);
        }

        /// <summary>
        /// Hard-destroys every local asteroid near <paramref name="position"/>. Call only from
        /// respawn apply — right before Instantiates a replacement — so zombies cannot stack.
        /// </summary>
        public static int DestroyLocalAsteroidsNear(EntityManager em, float3 position, float scaleHint = 0f)
        {
            return ForEachNear(em, position, scaleHint, DestroyLocalAsteroidEntity);
        }

        /// <summary>
        /// Walks the Instantiates registry and invokes <paramref name="action"/> on each matching
        /// seed-hydrated asteroid root within surface match radius.
        /// </summary>
        static int ForEachNear(
            EntityManager em,
            float3 position,
            float scaleHint,
            System.Action<EntityManager, Entity> action)
        {
            if (!em.World.IsCreated)
                return 0;
            // Registry walk only (not asteroid ToEntityArray) — still skip during join Instantiates.
            if (ClientJoinSettleCache.ShouldSkipMapBodyQueries)
                return 0;

            AsteroidClientEntityRegistry.CopyLive(RegistryScratch);
            float mapW = 0f;
            float mapH = 0f;
            bool haveMap = ToroidalMapEcs.TryGetMapSize(out mapW, out mapH);
            position.y = 0f;

            float hintRadius = scaleHint > 0.01f ? MatchRadiusForScale(scaleHint) : 0f;

            int count = 0;
            var matched = new NativeList<Entity>(8, Allocator.Temp);
            for (int i = 0; i < RegistryScratch.Count; i++)
            {
                Entity e = RegistryScratch[i];
                if (!em.Exists(e) || !em.HasComponent<LocalTransform>(e))
                    continue;
                // [TITAN-ORBIT] Only seed-hydrated locals — never touch unrelated entities.
                if (!em.HasComponent<AsteroidTag>(e) ||
                    !em.HasComponent<ClientSeedHydratedMapBody>(e))
                    continue;

                var lt = em.GetComponentData<LocalTransform>(e);
                float3 pos = lt.Position;
                pos.y = 0f;
                float dist = haveMap
                    ? ToroidalMapEcs.ToroidalDistance(position, pos, mapW, mapH)
                    : math.distance(position, pos);

                float radius = MatchRadiusForScale(lt.Scale);
                if (hintRadius > radius)
                    radius = hintRadius;

                if (dist > radius)
                    continue;

                matched.Add(e);
            }

            for (int i = 0; i < matched.Length; i++)
            {
                action(em, matched[i]);
                count++;
            }

            matched.Dispose();
            return count;
        }

        /// <summary>
        /// Finds the nearest asteroid within its own surface match radius of
        /// <paramref name="worldPos"/>.
        /// </summary>
        /// <param name="liveOnly">When true, skip IsDestroyed / culled rocks.</param>
        public static bool TryFindNearestAsteroid(
            EntityManager em,
            float3 worldPos,
            bool liveOnly,
            out Entity asteroid,
            out float matchedScale)
        {
            asteroid = Entity.Null;
            matchedScale = 0f;
            if (ClientJoinSettleCache.ShouldSkipMapBodyQueries)
                return false;

            AsteroidClientEntityRegistry.CopyLive(RegistryScratch);
            if (RegistryScratch.Count == 0)
                return false;

            float mapW = 0f;
            float mapH = 0f;
            bool haveMap = ToroidalMapEcs.TryGetMapSize(out mapW, out mapH);
            worldPos.y = 0f;

            float best = float.MaxValue;
            Entity bestEntity = Entity.Null;
            float bestScale = 0f;

            for (int i = 0; i < RegistryScratch.Count; i++)
            {
                Entity e = RegistryScratch[i];
                if (!em.Exists(e) ||
                    !em.HasComponent<AsteroidTag>(e) ||
                    !em.HasComponent<ClientSeedHydratedMapBody>(e) ||
                    !em.HasComponent<AsteroidState>(e) ||
                    !em.HasComponent<LocalTransform>(e))
                    continue;

                var state = em.GetComponentData<AsteroidState>(e);
                if (liveOnly)
                {
                    if (state.IsDestroyed || state.Health <= 0f)
                        continue;
                    if (em.HasComponent<AsteroidClientCulledTag>(e))
                        continue;
                }

                var lt = em.GetComponentData<LocalTransform>(e);
                float3 pos = lt.Position;
                pos.y = 0f;
                float dist = haveMap
                    ? ToroidalMapEcs.ToroidalDistance(worldPos, pos, mapW, mapH)
                    : math.distance(worldPos, pos);
                float radius = MatchRadiusForScale(lt.Scale);
                if (dist > radius || dist >= best)
                    continue;

                best = dist;
                bestEntity = e;
                bestScale = lt.Scale;
            }

            if (bestEntity == Entity.Null)
                return false;

            asteroid = bestEntity;
            matchedScale = bestScale;
            return true;
        }

        /// <summary>
        /// Kill-frame teardown without DestroyEntity: mark dead, strip collision, queue hybrid GO
        /// destroy. Safe to call from SimulationSystemGroup during combat.
        /// </summary>
        public static void SoftDestroyLocalAsteroidEntity(EntityManager em, Entity asteroid)
        {
            if (asteroid == Entity.Null)
                return;

            QueueProxyDestroy(asteroid);

            if (!em.Exists(asteroid))
            {
                AsteroidClientEntityRegistry.NotifyDestroyed(asteroid);
                return;
            }

            // Only seed-hydrated asteroid roots (defensive against wrong entity ids).
            if (!em.HasComponent<AsteroidTag>(asteroid) ||
                !em.HasComponent<ClientSeedHydratedMapBody>(asteroid))
                return;

            // --- Authoritative dead state ---
            if (em.HasComponent<AsteroidState>(asteroid))
            {
                var state = em.GetComponentData<AsteroidState>(asteroid);
                state.IsDestroyed = true;
                state.Health = 0f;
                em.SetComponentData(asteroid, state);
            }

            CullPhysics(em, asteroid);
            // Keep registry entry until hard destroy — respawn matching still needs the pose.
        }

        /// <summary>
        /// Hard-destroys a local asteroid root (LinkedEntityGroup cascades). Respawn path only.
        /// </summary>
        public static void DestroyLocalAsteroidEntity(EntityManager em, Entity asteroid)
        {
            if (asteroid == Entity.Null)
                return;

            QueueProxyDestroy(asteroid);

            if (!em.Exists(asteroid))
            {
                AsteroidClientEntityRegistry.NotifyDestroyed(asteroid);
                return;
            }

            // Only seed-hydrated asteroid roots (defensive against wrong entity ids).
            if (!em.HasComponent<AsteroidTag>(asteroid) ||
                !em.HasComponent<ClientSeedHydratedMapBody>(asteroid))
                return;

            // [TITAN-ORBIT] Prefab assets must never be destroyed from this path.
            if (em.HasComponent<Prefab>(asteroid))
                return;

            CullPhysics(em, asteroid);

            // --- Destroy root only ---
            // [ECS/DOTS] DestroyEntity on a LinkedEntityGroup root destroys the whole group.
            // Walking members and DestroyEntity each child mid-group was Crash!!!-adjacent and
            // froze predicted ship movement after combat kills.
            AsteroidClientEntityRegistry.NotifyDestroyed(asteroid);
            em.DestroyEntity(asteroid);
        }

        /// <summary>Marks culled + swaps to the shared no-collide PhysicsCollider blob.</summary>
        static void CullPhysics(EntityManager em, Entity asteroid)
        {
            if (!em.Exists(asteroid))
                return;

            if (!em.HasComponent<AsteroidClientCulledTag>(asteroid))
                em.AddComponent<AsteroidClientCulledTag>(asteroid);

            if (!em.HasComponent<PhysicsCollider>(asteroid))
                return;

            var noCollide = AsteroidClientCullPhysicsSystem.NoCollideCollider;
            var pc = em.GetComponentData<PhysicsCollider>(asteroid);
            if (pc.Value != noCollide)
                em.SetComponentData(asteroid, new PhysicsCollider { Value = noCollide });
        }
    }
}
