using TitanOrbit.Data;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Physics;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Shared grow-in math + collider suppress for a respawned asteroid.
    /// Presentation reads the eased scale; sim keeps <c>LocalTransform.Scale</c> at the
    /// designer size so destroy restore / pose match stay correct.
    /// </summary>
    public static class AsteroidRespawnGrowInLogic
    {
        /// <summary>Visual scale at t=0 as a fraction of <see cref="AsteroidRespawnGrowIn.TargetScale"/>.</summary>
        public const float StartFraction = 0.08f;

        /// <summary>Floor so a tiny designer rock is still a visible pebble at t=0.</summary>
        public const float MinStartScale = 0.04f;

        /// <summary>
        /// Eased  start→target scale. Smoothstep so the pebble is obvious, then it fills in quickly.
        /// </summary>
        public static float ComputeVisualScale(in AsteroidRespawnGrowIn grow)
        {
            return ComputeVisualScale(grow.TargetScale, grow.Elapsed, grow.Duration);
        }

        /// <summary>Eased start→target scale from raw fields.</summary>
        public static float ComputeVisualScale(float targetScale, float elapsed, float duration)
        {
            float target = math.max(MinStartScale, targetScale);
            float start = math.max(MinStartScale, target * StartFraction);
            float d = math.max(0.01f, duration);
            float t = math.saturate(elapsed / d);
            t = t * t * (3f - 2f * t);
            return math.lerp(start, target, t);
        }

        /// <summary>Designer grow / telegraph seconds (legacy 0 assets fall back inside ClampCounts).</summary>
        public static float ResolveDurationSeconds()
        {
            var settings = GemExplosionSettingsCache.ResolveOrDefault();
            settings.ClampCounts();
            return math.max(0.05f, settings.AsteroidRespawnGrowInSeconds);
        }

        /// <summary>
        /// Tags the rock for a visual grow and swaps hulls to the shared no-collide blob.
        /// Does not add <see cref="AsteroidClientCulledTag"/> and does not squash
        /// <c>LocalTransform.Scale</c> — those paths hide the mesh and would kill the grow-in.
        /// </summary>
        public static void Begin(EntityManager em, Entity asteroid, float targetScale, float duration)
        {
            if (!em.Exists(asteroid))
                return;

            var grow = new AsteroidRespawnGrowIn
            {
                TargetScale = math.max(MinStartScale, targetScale),
                Elapsed = 0f,
                Duration = math.max(0.05f, duration),
            };
            if (em.HasComponent<AsteroidRespawnGrowIn>(asteroid))
                em.SetComponentData(asteroid, grow);
            else
                em.AddComponentData(asteroid, grow);

            SuppressCollision(em, asteroid);
        }

        /// <summary>Restores the designer friction hull and drops the grow-in tag.</summary>
        public static void Complete(EntityManager em, Entity asteroid)
        {
            if (!em.Exists(asteroid))
                return;

            ArmCollision(em, asteroid);
            if (em.HasComponent<AsteroidRespawnGrowIn>(asteroid))
                em.RemoveComponent<AsteroidRespawnGrowIn>(asteroid);
        }

        /// <summary>No-collide blob on the root and every LinkedEntityGroup child that has a hull.</summary>
        static void SuppressCollision(EntityManager em, Entity asteroid)
        {
            ApplyNoCollide(em, asteroid);
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
                ApplyNoCollide(em, member);
            }

            members.Dispose();
        }

        /// <summary>Designer friction sphere on every member that still has a PhysicsCollider.</summary>
        static void ArmCollision(EntityManager em, Entity asteroid)
        {
            var live = AsteroidColliderMaterialLogic.CreateFromSettingsCache();
            ApplyLiveCollider(em, asteroid, live);
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
                ApplyLiveCollider(em, member, live);
            }

            members.Dispose();
        }

        static void ApplyNoCollide(EntityManager em, Entity entity)
        {
            if (!em.HasComponent<PhysicsCollider>(entity))
                return;

            var noCollide = AsteroidClientCullPhysicsSystem.NoCollideCollider;
            var pc = em.GetComponentData<PhysicsCollider>(entity);
            if (pc.Value != noCollide)
                em.SetComponentData(entity, new PhysicsCollider { Value = noCollide });
        }

        static void ApplyLiveCollider(EntityManager em, Entity entity, BlobAssetReference<Collider> live)
        {
            if (!em.HasComponent<PhysicsCollider>(entity))
                return;
            em.SetComponentData(entity, new PhysicsCollider { Value = live });
        }
    }

    /// <summary>
    /// Client: ticks respawn grow-in and arms collision when the mesh has reached full size.
    /// World: ClientSimulation. After <see cref="AsteroidRespawnRpcClientSystem"/> so a rock
    /// Instantiated this tick starts at elapsed 0 (pebble) for the same-frame visualizer.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(AsteroidRespawnRpcClientSystem))]
    public partial struct AsteroidRespawnGrowInSystem : ISystem
    {
        /// <summary>Need at least one growing rock.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<AsteroidRespawnGrowIn>();
        }

        /// <summary>Advances elapsed; completes rocks whose grow has finished.</summary>
        public void OnUpdate(ref SystemState state)
        {
            float dt = SystemAPI.Time.DeltaTime;
            var em = state.EntityManager;
            var done = new NativeList<Entity>(4, Allocator.Temp);

            foreach (var (grow, entity) in SystemAPI.Query<RefRW<AsteroidRespawnGrowIn>>()
                         .WithAll<AsteroidTag>()
                         .WithEntityAccess())
            {
                var g = grow.ValueRO;
                g.Elapsed = math.min(g.Duration + dt, g.Elapsed + dt);
                grow.ValueRW = g;
                if (g.Elapsed >= g.Duration)
                    done.Add(entity);
            }

            for (int i = 0; i < done.Length; i++)
                AsteroidRespawnGrowInLogic.Complete(em, done[i]);

            done.Dispose();
        }
    }
}
