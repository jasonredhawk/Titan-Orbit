using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Physics;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Re-applies a no-collide <see cref="PhysicsCollider"/> and tiny scale on culled asteroid
    /// ghosts each predicted fixed step so prediction / structural churn cannot restore a solid
    /// hull after HitRpc hide. ClientSimulation only. Paired with
    /// <see cref="Game.ClientAsteroidCollisionCull"/>.
    /// </summary>
    // OrderFirst: before default-slot physics without UpdateInGroup(PhysicsSystemGroup) —
    // ClientWorld often lacks PhysicsSystemGroup as a PredictedFixedStep sibling (sorter spam).
    [UpdateInGroup(typeof(PredictedFixedStepSimulationSystemGroup), OrderFirst = true)]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    public partial struct AsteroidClientCullPhysicsSystem : ISystem
    {
        /// <summary>Shared zero-filter sphere — never mutate bake-shared asteroid blobs.</summary>
        static BlobAssetReference<Collider> s_noCollide;

        /// <summary>Ensures the zero-filter collider blob exists once per process.</summary>
        public void OnCreate(ref SystemState state)
        {
            EnsureNoCollideBlob();
            state.RequireForUpdate<AsteroidClientCulledTag>();
        }

        /// <summary>
        /// Forces the no-collide blob on every culled collider (root + children) and keeps
        /// asteroid-root scale squashed so a stale static PhysX hull cannot restore the old radius.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // [TITAN-ORBIT] Asteroid PhysicsCollider writes during Settling / TransformQuarantine
            // Instantiates are Crash!!!-adjacent — cull is cosmetic; wait until map gathers are safe.
            if (ClientJoinSettleCache.ShouldSkipMapBodyQueries)
                return;

            EnsureNoCollideBlob();

            // --- No-collide blob on every culled collider (root + LinkedEntityGroup children) ---
            foreach (var collider in SystemAPI
                         .Query<RefRW<PhysicsCollider>>()
                         .WithAll<AsteroidClientCulledTag>())
            {
                if (collider.ValueRO.Value == s_noCollide)
                    continue;
                collider.ValueRW.Value = s_noCollide;
            }

            // --- Squash scale on asteroid roots only (children keep authored local scale) ---
            foreach (var transform in SystemAPI
                         .Query<RefRW<LocalTransform>>()
                         .WithAll<AsteroidClientCulledTag, AsteroidTag>())
            {
                if (transform.ValueRO.Scale <= 0.011f)
                    continue;
                var lt = transform.ValueRW;
                lt.Scale = 0.01f;
                transform.ValueRW = lt;
            }
        }

        /// <summary>Creates a tiny sphere that belongs to / collides with nothing.</summary>
        static void EnsureNoCollideBlob()
        {
            if (s_noCollide.IsCreated)
                return;

            s_noCollide = Unity.Physics.SphereCollider.Create(
                new SphereGeometry { Center = float3.zero, Radius = 0.01f },
                CollisionFilter.Zero,
                Unity.Physics.Material.Default);
        }

        /// <summary>Shared no-collide blob for presentation-thread cull (same as this system).</summary>
        public static BlobAssetReference<Collider> NoCollideCollider
        {
            get
            {
                EnsureNoCollideBlob();
                return s_noCollide;
            }
        }
    }
}
