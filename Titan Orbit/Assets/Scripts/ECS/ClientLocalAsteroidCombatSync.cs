using System.Collections.Generic;
using TitanOrbit.Generation;
using TitanOrbit.Simulation;
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
    /// <para>
    /// Phantom hulls after a visible kill are a sync miss: the hybrid mesh hid, but the ECS
    /// <see cref="PhysicsCollider"/> (or a stray ghost leftover) stayed solid. Bounce / grind
    /// then still fire. Soft-destroy therefore culls the whole LinkedEntityGroup, squashes
    /// scale so a stale static PhysX hull cannot keep the old radius, and SimulationSystemGroup
    /// apply paths <b>remove</b> <see cref="PhysicsCollider"/> the same frame so Unity Physics
    /// drops the body (blob-swap alone left a phantom hull that still stopped the ship).
    /// DestroyRpc matches the <b>single nearest</b> rock at the pose — a radius wipe hid
    /// neighbors in a dense belt while the server still simulated them as solid.
    /// Respawn wipes only culled zombies at that exact slot (not live neighbors) and retries
    /// if the RPC arrives during join skip — a missed respawn is an invisible server rock
    /// that still rams the hull.
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
        /// DestroyRpc poses that matched zero local rocks (join skip, registry lag, or a
        /// stray ghost). Retried each client sim tick until a cull lands or attempts expire.
        /// </summary>
        static readonly List<PendingDestroyPose> UnmatchedDestroys = new List<PendingDestroyPose>(16);

        /// <summary>
        /// RespawnRpc payloads that could not spawn yet (join skip, missing prefab). Retried
        /// each client sim tick — a dropped respawn leaves a live server rock with no client
        /// mesh, which rams the hull in empty space.
        /// </summary>
        static readonly List<PendingRespawnPose> UnmatchedRespawns = new List<PendingRespawnPose>(16);

        /// <summary>
        /// Fallback match radius when scale is unknown (small rocks). Prefer
        /// <see cref="MatchRadiusForScale"/>.
        /// </summary>
        public const float MatchRadius = 2.5f;

        /// <summary>Hard cap so a bad Scale on the wire cannot match the whole map.</summary>
        const float MaxMatchRadius = 48f;

        /// <summary>
        /// Tiny LocalTransform.Scale written on cull. [PHYSICS] Static Unity Physics worlds
        /// sometimes keep the previous sphere until a transform change dirties them — a 0.01
        /// scale makes any leftover hull harmless on the play plane.
        /// </summary>
        const float CulledTransformScale = 0.01f;

        /// <summary>
        /// How many client sim ticks to keep retrying an unmatched destroy pose.
        /// ~10s at 60 Hz — long enough for join skip to end, short enough to drop true misses.
        /// </summary>
        const int MaxDestroyRetryAttempts = 600;

        /// <summary>
        /// Respawn retries last longer (~30s at 60 Hz). A missed respawn is a permanent
        /// invisible server rock; join skip / prefab lag can outlast a destroy retry.
        /// </summary>
        const int MaxRespawnRetryAttempts = 1800;

        /// <summary>
        /// Max toroidal distance to treat two asteroid poses as "the same slot" on respawn.
        /// Must stay tiny — <see cref="MatchRadiusForScale"/> is large enough to swallow
        /// live neighbors in a dense belt (client hard-destroyed them, server kept ramming).
        /// </summary>
        const float RespawnPoseEpsilon = 0.75f;

        /// <summary>One unmatched <see cref="AsteroidDestroyedRpc"/> waiting for a local rock.</summary>
        struct PendingDestroyPose
        {
            /// <summary>Logical XZ center from the server RPC (Y forced to 0).</summary>
            public float3 Position;

            /// <summary>Uniform scale hint for match radius.</summary>
            public float Scale;

            /// <summary>Ticks we have already retried this pose.</summary>
            public int Attempts;
        }

        /// <summary>One unmatched <see cref="AsteroidRespawnRpc"/> waiting to Instantiates.</summary>
        struct PendingRespawnPose
        {
            /// <summary>Logical XZ center (Y forced to 0).</summary>
            public float3 Position;

            /// <summary>Uniform LocalTransform scale to restore.</summary>
            public float Scale;

            /// <summary>Full gem capacity.</summary>
            public float GemValue;

            /// <summary>Full Health.</summary>
            public float MaxHealth;

            /// <summary>Designer Size for bounce mass.</summary>
            public float Size;

            /// <summary>Ticks we have already retried this pose.</summary>
            public int Attempts;
        }

        /// <summary>
        /// Toroidal match radius from asteroid scale — HitRpc hits land on the surface, not the
        /// center. A fixed 2.5u radius missed large rocks → cull/hide never ran → desync phantom.
        /// Also at least the ship-ram body radius so destroy-at-center still covers grind hulls.
        /// </summary>
        public static float MatchRadiusForScale(float scale)
        {
            float safeScale = math.max(0.01f, scale);
            float hitRadius = BulletCollision.AsteroidHitRadius(safeScale);
            float bodyRadius = BodyCollisionMath.GetAsteroidBodyRadiusWorld(safeScale);
            float radius = math.max(hitRadius, bodyRadius);
            // Slack for network/display jitter + slightly oversized meshes.
            return math.min(MaxMatchRadius, radius + 1.25f);
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
        /// Writes authoritative Health / IsDestroyed onto the local asteroid that best matches
        /// this HitRpc impact (surface fit, not nearest-center). On kill, soft-destroys
        /// (cull + hide) — no DestroyEntity.
        /// </summary>
        public static Entity ApplyHitAtPosition(
            EntityManager em,
            float3 hitPosition,
            float asteroidHealthAfter)
        {
            if (asteroidHealthAfter < 0f || !em.World.IsCreated)
                return Entity.Null;

            // Kill frames must still match a rock we just culled (presentation hide can win the race).
            bool liveOnly = asteroidHealthAfter > 0.01f;
            if (!TryFindAsteroidAtSurfaceHit(em, hitPosition, liveOnly, out Entity asteroid, out _))
                return Entity.Null;

            ApplyAuthoritativeHealth(em, asteroid, asteroidHealthAfter);

            if (asteroidHealthAfter <= 0.01f)
                SoftDestroyLocalAsteroidEntity(em, asteroid);

            return asteroid;
        }

        /// <summary>
        /// Ram/grind HitRpc apply. Uses the PhysX hull radius (not the bullet hit-sphere) so the
        /// contact point we authored as <c>center + normal × bodyRadius</c> selects the rock the
        /// server damaged — bullet surface-fit picked a neighbor in a dense belt, hid it, and
        /// left a live server rock that still rammed the hull in empty space.
        /// </summary>
        public static Entity ApplyRamHitAtPosition(
            EntityManager em,
            float3 hitPosition,
            float asteroidHealthAfter)
        {
            if (asteroidHealthAfter < 0f || !em.World.IsCreated)
                return Entity.Null;

            bool liveOnly = asteroidHealthAfter > 0.01f;
            if (!TryFindAsteroidAtRamContact(em, hitPosition, liveOnly, out Entity asteroid, out _))
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
        /// Soft-destroys the single nearest local asteroid to <paramref name="position"/>
        /// (toroidal center distance, within that rock's match radius). Used by
        /// <see cref="AsteroidDestroyedRpc"/> — keeps the ECS entity alive as a culled zombie
        /// until respawn hard-destroys it.
        /// <para>
        /// [TITAN-ORBIT] Must not cull every rock in the radius. A dense belt would hide
        /// neighbors while the server still simulated them — empty space the ship cannot fly through.
        /// Respawn still uses <see cref="DestroyLocalAsteroidsNear"/> to wipe stacked zombies.
        /// </para>
        /// </summary>
        /// <returns>1 if a rock was culled, 0 if the pose should be retried.</returns>
        public static int SoftDestroyLocalAsteroidsNear(
            EntityManager em,
            float3 position,
            float scaleHint = 0f)
        {
            if (!TryFindNearestAsteroidAtCenter(em, position, scaleHint, out Entity asteroid, out _))
                return 0;

            SoftDestroyLocalAsteroidEntity(em, asteroid);
            return 1;
        }

        /// <summary>
        /// Hard-destroys culled / dead zombies at this exact pose, then the caller Instantiates
        /// a replacement. Live neighbors are never touched — a MatchRadius wipe deleted healthy
        /// client rocks while the server still simulated them (invisible ram damage).
        /// </summary>
        public static int DestroyLocalAsteroidsNear(EntityManager em, float3 position, float scaleHint = 0f)
        {
            return DestroyCulledZombiesAtPose(em, position);
        }

        /// <summary>
        /// Applies one <see cref="AsteroidRespawnRpc"/>: wipe zombies at the slot, skip if a live
        /// rock is already there, otherwise Instantiates. Returns false when join skip / missing
        /// prefab should retry.
        /// </summary>
        public static bool TryApplyAsteroidRespawn(
            EntityManager em,
            Entity asteroidPrefab,
            float3 position,
            float scale,
            float gemValue,
            float maxHealth,
            float size)
        {
            if (!em.World.IsCreated)
                return false;
            if (asteroidPrefab == Entity.Null)
                return false;
            if (ClientJoinSettleCache.ShouldSkipMapBodyQueries)
                return false;

            position.y = 0f;
            DestroyCulledZombiesAtPose(em, position);

            // --- Already hydrated (duplicate RPC / retry after a successful spawn) ---
            if (HasLiveAsteroidAtPose(em, position))
                return true;

            var body = new MapLayoutBlueprint.Body
            {
                EntityKind = 3,
                Position = position,
                Scale = scale,
                AsteroidScale = new float3(scale),
                GemValue = gemValue,
                MaxHealth = maxHealth,
                Size = size,
            };
            Entity spawned = ClientLocalMapBodySpawn.SpawnAsteroid(em, asteroidPrefab, body);
            return spawned != Entity.Null;
        }

        /// <summary>
        /// Remembers a destroy pose that matched nothing this tick. <see cref="RetryUnmatchedDestroys"/>
        /// keeps trying so a join skip or registry lag cannot leave a solid invisible rock forever.
        /// </summary>
        public static void QueueUnmatchedDestroy(float3 position, float scaleHint)
        {
            position.y = 0f;
            for (int i = 0; i < UnmatchedDestroys.Count; i++)
            {
                float3 existing = UnmatchedDestroys[i].Position;
                if (math.distancesq(existing, position) < 0.25f)
                    return;
            }

            UnmatchedDestroys.Add(new PendingDestroyPose
            {
                Position = position,
                Scale = scaleHint,
                Attempts = 0,
            });
        }

        /// <summary>
        /// Re-applies unmatched destroy poses. Call every client SimulationSystemGroup tick
        /// (not only on inbound RPC frames — RequireForUpdate would skip retries).
        /// </summary>
        public static void RetryUnmatchedDestroys(EntityManager em)
        {
            if (UnmatchedDestroys.Count == 0 || !em.World.IsCreated)
                return;

            // Join Instantiates window — do not walk the registry yet, and do not burn attempts.
            if (ClientJoinSettleCache.ShouldSkipMapBodyQueries)
                return;

            for (int i = UnmatchedDestroys.Count - 1; i >= 0; i--)
            {
                PendingDestroyPose pending = UnmatchedDestroys[i];
                int culled = SoftDestroyLocalAsteroidsNear(em, pending.Position, pending.Scale);
                if (culled > 0)
                {
                    UnmatchedDestroys.RemoveAt(i);
                    continue;
                }

                pending.Attempts++;
                if (pending.Attempts >= MaxDestroyRetryAttempts)
                {
                    UnmatchedDestroys.RemoveAt(i);
                    continue;
                }

                UnmatchedDestroys[i] = pending;
            }
        }

        /// <summary>Clears retry / proxy queues on session teardown.</summary>
        public static void ClearPendingQueues()
        {
            UnmatchedDestroys.Clear();
            UnmatchedRespawns.Clear();
            PendingProxyDestroy.Clear();
        }

        /// <summary>
        /// Remembers a respawn payload that could not Instantiates this tick.
        /// </summary>
        public static void QueueUnmatchedRespawn(
            float3 position,
            float scale,
            float gemValue,
            float maxHealth,
            float size)
        {
            position.y = 0f;
            for (int i = 0; i < UnmatchedRespawns.Count; i++)
            {
                float3 existing = UnmatchedRespawns[i].Position;
                if (math.distancesq(existing, position) < 0.25f)
                    return;
            }

            UnmatchedRespawns.Add(new PendingRespawnPose
            {
                Position = position,
                Scale = scale,
                GemValue = gemValue,
                MaxHealth = maxHealth,
                Size = size,
                Attempts = 0,
            });
        }

        /// <summary>
        /// Re-applies unmatched respawn poses. Call every client SimulationSystemGroup tick
        /// (not only on inbound RPC frames).
        /// </summary>
        public static void RetryUnmatchedRespawns(EntityManager em, Entity asteroidPrefab)
        {
            if (UnmatchedRespawns.Count == 0 || !em.World.IsCreated)
                return;
            if (ClientJoinSettleCache.ShouldSkipMapBodyQueries)
                return;
            if (asteroidPrefab == Entity.Null)
                return;

            for (int i = UnmatchedRespawns.Count - 1; i >= 0; i--)
            {
                PendingRespawnPose pending = UnmatchedRespawns[i];
                if (TryApplyAsteroidRespawn(
                    em,
                    asteroidPrefab,
                    pending.Position,
                    pending.Scale,
                    pending.GemValue,
                    pending.MaxHealth,
                    pending.Size))
                {
                    UnmatchedRespawns.RemoveAt(i);
                    continue;
                }

                pending.Attempts++;
                if (pending.Attempts >= MaxRespawnRetryAttempts)
                {
                    UnmatchedRespawns.RemoveAt(i);
                    continue;
                }

                UnmatchedRespawns[i] = pending;
            }
        }

        /// <summary>
        /// Hard-destroys only culled / 0-HP zombies whose center sits in
        /// <see cref="RespawnPoseEpsilon"/> of <paramref name="position"/>. Live rocks in the
        /// belt are left alone.
        /// </summary>
        static int DestroyCulledZombiesAtPose(EntityManager em, float3 position)
        {
            if (!em.World.IsCreated || ClientJoinSettleCache.ShouldSkipMapBodyQueries)
                return 0;

            AsteroidClientEntityRegistry.CopyLive(RegistryScratch);
            float mapW = 0f;
            float mapH = 0f;
            bool haveMap = ToroidalMapEcs.TryGetMapSize(out mapW, out mapH);
            position.y = 0f;

            int count = 0;
            var matched = new NativeList<Entity>(8, Allocator.Temp);
            for (int i = 0; i < RegistryScratch.Count; i++)
            {
                Entity e = RegistryScratch[i];
                if (!em.Exists(e) || !em.HasComponent<AsteroidTag>(e) || !em.HasComponent<LocalTransform>(e))
                    continue;
                if (IsClientLiveAsteroid(em, e))
                    continue;

                var lt = em.GetComponentData<LocalTransform>(e);
                float3 pos = lt.Position;
                pos.y = 0f;
                float dist = haveMap
                    ? ToroidalMapEcs.ToroidalDistance(position, pos, mapW, mapH)
                    : math.distance(position, pos);
                if (dist > RespawnPoseEpsilon)
                    continue;

                matched.Add(e);
            }

            for (int i = 0; i < matched.Length; i++)
            {
                DestroyOrCullLocalAsteroidEntity(em, matched[i]);
                count++;
            }

            matched.Dispose();
            return count;
        }

        /// <summary>
        /// True when a healthy (not culled, Health &gt; 0) asteroid already occupies this slot.
        /// </summary>
        static bool HasLiveAsteroidAtPose(EntityManager em, float3 position)
        {
            if (!em.World.IsCreated || ClientJoinSettleCache.ShouldSkipMapBodyQueries)
                return false;

            AsteroidClientEntityRegistry.CopyLive(RegistryScratch);
            float mapW = 0f;
            float mapH = 0f;
            bool haveMap = ToroidalMapEcs.TryGetMapSize(out mapW, out mapH);
            position.y = 0f;

            for (int i = 0; i < RegistryScratch.Count; i++)
            {
                Entity e = RegistryScratch[i];
                if (!em.Exists(e) || !em.HasComponent<LocalTransform>(e))
                    continue;
                if (!IsClientLiveAsteroid(em, e))
                    continue;

                var lt = em.GetComponentData<LocalTransform>(e);
                float3 pos = lt.Position;
                pos.y = 0f;
                float dist = haveMap
                    ? ToroidalMapEcs.ToroidalDistance(position, pos, mapW, mapH)
                    : math.distance(position, pos);
                if (dist <= RespawnPoseEpsilon)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// True when this client asteroid is still a real rock (not a kill-frame zombie).
        /// </summary>
        static bool IsClientLiveAsteroid(EntityManager em, Entity asteroid)
        {
            if (!em.Exists(asteroid) || !em.HasComponent<AsteroidTag>(asteroid))
                return false;
            if (em.HasComponent<AsteroidClientCulledTag>(asteroid))
                return false;
            if (!em.HasComponent<AsteroidState>(asteroid))
                return false;

            var state = em.GetComponentData<AsteroidState>(asteroid);
            return !state.IsDestroyed && state.Health > 0.01f;
        }

        /// <summary>
        /// Picks the single nearest asteroid root to <paramref name="position"/> (toroidal
        /// center-to-center), within that rock's match radius (RPC scale hint wins when the
        /// local body was already squashed by a prior cull).
        /// </summary>
        /// <param name="scaleHint">Uniform scale from <see cref="AsteroidDestroyedRpc"/>.</param>
        /// <returns>True when a registry asteroid sits inside the match radius.</returns>
        static bool TryFindNearestAsteroidAtCenter(
            EntityManager em,
            float3 position,
            float scaleHint,
            out Entity asteroid,
            out float matchedScale)
        {
            asteroid = Entity.Null;
            matchedScale = 0f;
            if (!em.World.IsCreated)
                return false;
            if (ClientJoinSettleCache.ShouldSkipMapBodyQueries)
                return false;

            AsteroidClientEntityRegistry.CopyLive(RegistryScratch);
            float mapW = 0f;
            float mapH = 0f;
            bool haveMap = ToroidalMapEcs.TryGetMapSize(out mapW, out mapH);
            position.y = 0f;

            float hintRadius = scaleHint > 0.01f ? MatchRadiusForScale(scaleHint) : 0f;
            float bestDist = float.MaxValue;
            Entity bestEntity = Entity.Null;
            float bestScale = 0f;

            for (int i = 0; i < RegistryScratch.Count; i++)
            {
                Entity e = RegistryScratch[i];
                if (!em.Exists(e) || !em.HasComponent<LocalTransform>(e))
                    continue;
                if (!em.HasComponent<AsteroidTag>(e))
                    continue;

                var lt = em.GetComponentData<LocalTransform>(e);
                float3 pos = lt.Position;
                pos.y = 0f;
                float dist = haveMap
                    ? ToroidalMapEcs.ToroidalDistance(position, pos, mapW, mapH)
                    : math.distance(position, pos);

                // Culled zombies may already have Scale squashed — prefer the RPC hint radius.
                float radius = MatchRadiusForScale(math.max(lt.Scale, scaleHint));
                if (hintRadius > radius)
                    radius = hintRadius;
                if (dist > radius)
                    continue;
                if (dist >= bestDist)
                    continue;

                bestDist = dist;
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
        /// Finds the asteroid whose bullet hit-sphere best fits <paramref name="worldPos"/>
        /// (surface residual, not nearest center). Nearest-center picked a neighbor when a
        /// surface hit sat closer to the next rock — visual hide used surface-fit, ECS HP/cull
        /// used nearest-center, and the killed mesh vanished while the solid hull stayed.
        /// </summary>
        /// <param name="liveOnly">When true, skip IsDestroyed / culled rocks.</param>
        public static bool TryFindAsteroidAtSurfaceHit(
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

            float bestError = float.MaxValue;
            Entity bestEntity = Entity.Null;
            float bestScale = 0f;

            for (int i = 0; i < RegistryScratch.Count; i++)
            {
                Entity e = RegistryScratch[i];
                if (!em.Exists(e) ||
                    !em.HasComponent<AsteroidTag>(e) ||
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
                float hitRadius = BulletCollision.AsteroidHitRadius(math.max(0.01f, lt.Scale));
                float maxDist = MatchRadiusForScale(lt.Scale);
                if (dist > maxDist)
                    continue;

                // Surface residual: 0 = impact sits exactly on the bullet sphere.
                float surfaceError = math.abs(dist - hitRadius);
                if (surfaceError >= bestError)
                    continue;

                bestError = surfaceError;
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
        /// Finds the asteroid whose PhysX hull best fits <paramref name="worldPos"/> (body-radius
        /// residual). Ram HitRpc contact points are authored at
        /// <c>center + normal × GetAsteroidBodyRadiusWorld</c> — the bullet hit-sphere residual
        /// picked a packed neighbor instead.
        /// </summary>
        /// <param name="liveOnly">When true, skip IsDestroyed / culled rocks.</param>
        public static bool TryFindAsteroidAtRamContact(
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

            float bestError = float.MaxValue;
            Entity bestEntity = Entity.Null;
            float bestScale = 0f;

            for (int i = 0; i < RegistryScratch.Count; i++)
            {
                Entity e = RegistryScratch[i];
                if (!em.Exists(e) ||
                    !em.HasComponent<AsteroidTag>(e) ||
                    !em.HasComponent<AsteroidState>(e) ||
                    !em.HasComponent<LocalTransform>(e))
                    continue;

                if (liveOnly && !IsClientLiveAsteroid(em, e))
                    continue;

                var lt = em.GetComponentData<LocalTransform>(e);
                float3 pos = lt.Position;
                pos.y = 0f;
                float dist = haveMap
                    ? ToroidalMapEcs.ToroidalDistance(worldPos, pos, mapW, mapH)
                    : math.distance(worldPos, pos);
                float bodyRadius = BodyCollisionMath.GetAsteroidBodyRadiusWorld(math.max(0.01f, lt.Scale));
                float maxDist = MatchRadiusForScale(lt.Scale);
                if (dist > maxDist)
                    continue;

                float surfaceError = math.abs(dist - bodyRadius);
                if (surfaceError >= bestError)
                    continue;

                bestError = surfaceError;
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
        /// Finds the nearest asteroid within its own surface match radius of
        /// <paramref name="worldPos"/>. Prefer <see cref="TryFindAsteroidAtSurfaceHit"/> for HitRpc.
        /// </summary>
        /// <param name="liveOnly">When true, skip IsDestroyed / culled rocks.</param>
        public static bool TryFindNearestAsteroid(
            EntityManager em,
            float3 worldPos,
            bool liveOnly,
            out Entity asteroid,
            out float matchedScale)
        {
            return TryFindAsteroidAtSurfaceHit(em, worldPos, liveOnly, out asteroid, out matchedScale);
        }

        /// <summary>
        /// Kill-frame teardown without DestroyEntity: mark dead, strip collision, queue hybrid GO
        /// destroy. Safe to call from SimulationSystemGroup during combat. Also culls stray
        /// ghost leftovers (no <see cref="ClientSeedHydratedMapBody"/>) so mixed-build phantoms die.
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

            // Any asteroid root — seed-hydrated or leftover ghost.
            if (!em.HasComponent<AsteroidTag>(asteroid))
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
            // [PHYSICS] SimulationSystemGroup can RemoveComponent this frame. Blob-swap alone
            // left a static sphere that pinned the hull while grinding inside the volume.
            StripPhysicsCollidersImmediate(em, asteroid);
            // Keep registry entry until hard destroy — respawn matching still needs the pose.
        }

        /// <summary>
        /// Respawn helper: hard-destroy seed-hydrated locals; only cull stray ghosts.
        /// </summary>
        static void DestroyOrCullLocalAsteroidEntity(EntityManager em, Entity asteroid)
        {
            if (asteroid == Entity.Null || !em.Exists(asteroid))
            {
                DestroyLocalAsteroidEntity(em, asteroid);
                return;
            }

            if (em.HasComponent<ClientSeedHydratedMapBody>(asteroid))
                DestroyLocalAsteroidEntity(em, asteroid);
            else
                SoftDestroyLocalAsteroidEntity(em, asteroid);
        }

        /// <summary>
        /// Hard-destroys a local asteroid root (LinkedEntityGroup cascades). Respawn path only.
        /// Seed-hydrated locals only — never DestroyEntity a NetCode ghost.
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

            // Only seed-hydrated asteroid roots (defensive against ghost ids / wrong entities).
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

        /// <summary>
        /// Marks culled and disables collision on the root and every LinkedEntityGroup child.
        /// Presentation hide may call this (blob-swap only — jobs may still be in flight).
        /// SimulationSystemGroup kill paths then call <see cref="StripPhysicsCollidersImmediate"/>.
        /// </summary>
        public static void CullPhysics(EntityManager em, Entity asteroid)
        {
            if (!em.Exists(asteroid))
                return;

            ApplyCullOnEntity(em, asteroid);

            // --- Child colliders (ghost prefab LinkedEntityGroup) ---
            // [ECS/DOTS] Copy member ids first — AddComponent is structural and invalidates the buffer.
            if (!em.HasBuffer<LinkedEntityGroup>(asteroid))
                return;

            var group = em.GetBuffer<LinkedEntityGroup>(asteroid);
            var members = new NativeArray<Entity>(group.Length, Allocator.Temp);
            for (int i = 0; i < group.Length; i++)
                members[i] = group[i].Value;

            for (int i = 0; i < members.Length; i++)
            {
                Entity member = members[i];
                if (member == asteroid || !em.Exists(member))
                    continue;
                ApplyCullOnEntity(em, member);
            }

            members.Dispose();
        }

        /// <summary>
        /// Registry walk: any dead local asteroid that still has a collider gets culled.
        /// Catches HitRpc hide / GO teardown that never tagged the ECS body.
        /// </summary>
        public static void CullDeadAsteroidsStillSolid(EntityManager em)
        {
            if (!em.World.IsCreated || ClientJoinSettleCache.ShouldSkipMapBodyQueries)
                return;

            AsteroidClientEntityRegistry.CopyLive(RegistryScratch);
            for (int i = 0; i < RegistryScratch.Count; i++)
            {
                Entity e = RegistryScratch[i];
                if (!em.Exists(e) || !em.HasComponent<AsteroidTag>(e))
                    continue;
                if (em.HasComponent<AsteroidClientCulledTag>(e))
                    continue;
                if (!em.HasComponent<AsteroidState>(e))
                    continue;

                var state = em.GetComponentData<AsteroidState>(e);
                if (!state.IsDestroyed && state.Health > 0.01f)
                    continue;

                CullPhysics(em, e);
            }
        }

        /// <summary>
        /// CulledTag + no-collide blob + tiny scale on one entity (root or child).
        /// Does not RemoveComponent here — presentation-thread hide must not structurally
        /// mutate PhysicsCollider while jobs may still be in flight. The predicted strip pass does that.
        /// </summary>
        static void ApplyCullOnEntity(EntityManager em, Entity entity)
        {
            if (!em.Exists(entity))
                return;

            if (!em.HasComponent<AsteroidClientCulledTag>(entity))
                em.AddComponent<AsteroidClientCulledTag>(entity);

            // [PHYSICS] Nudge static-world dirty flags for the one frame before collider strip.
            if (em.HasComponent<LocalTransform>(entity))
            {
                var lt = em.GetComponentData<LocalTransform>(entity);
                if (lt.Scale > CulledTransformScale + 0.001f)
                {
                    lt.Scale = CulledTransformScale;
                    em.SetComponentData(entity, lt);
                }
            }

            if (!em.HasComponent<PhysicsCollider>(entity))
                return;

            var noCollide = AsteroidClientCullPhysicsSystem.NoCollideCollider;
            var pc = em.GetComponentData<PhysicsCollider>(entity);
            if (pc.Value != noCollide)
                em.SetComponentData(entity, new PhysicsCollider { Value = noCollide });
        }

        /// <summary>
        /// Removes <see cref="PhysicsCollider"/> from the asteroid root and every LinkedEntityGroup
        /// child so Unity Physics rebuilds the static world this frame. Call only from
        /// SimulationSystemGroup (HitRpc / DestroyRpc apply). Presentation-thread hide must keep
        /// using blob-swap via <see cref="CullPhysics"/>.
        /// </summary>
        public static void StripPhysicsCollidersImmediate(EntityManager em, Entity asteroid)
        {
            if (!em.Exists(asteroid))
                return;

            StripPhysicsColliderOnEntity(em, asteroid);

            if (!em.HasBuffer<LinkedEntityGroup>(asteroid))
                return;

            // [ECS/DOTS] Copy member ids first — RemoveComponent is structural.
            var group = em.GetBuffer<LinkedEntityGroup>(asteroid);
            var members = new NativeArray<Entity>(group.Length, Allocator.Temp);
            for (int i = 0; i < group.Length; i++)
                members[i] = group[i].Value;

            for (int i = 0; i < members.Length; i++)
            {
                Entity member = members[i];
                if (member == asteroid || !em.Exists(member))
                    continue;
                StripPhysicsColliderOnEntity(em, member);
            }

            members.Dispose();
        }

        /// <summary>Drops PhysicsCollider on one entity when present.</summary>
        static void StripPhysicsColliderOnEntity(EntityManager em, Entity entity)
        {
            if (!em.Exists(entity) || !em.HasComponent<PhysicsCollider>(entity))
                return;
            em.RemoveComponent<PhysicsCollider>(entity);
        }
    }
}
