using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Physics;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Re-applies a no-collide <see cref="PhysicsCollider"/> on culled asteroid ghosts each predicted
    /// fixed step so prediction / structural churn cannot restore a solid hull after HitRpc hide.
    /// ClientSimulation only. Paired with <see cref="Game.ClientAsteroidCollisionCull"/>.
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
        /// For every culled asteroid that still has a PhysicsCollider, force the no-collide blob.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // [TITAN-ORBIT] Asteroid PhysicsCollider writes during Settling / TransformQuarantine
            // Instantiates are Crash!!!-adjacent — cull is cosmetic; wait until map gathers are safe.
            if (ClientJoinSettleCache.ShouldSkipMapBodyQueries)
                return;

            EnsureNoCollideBlob();

            foreach (var collider in SystemAPI
                         .Query<RefRW<PhysicsCollider>>()
                         .WithAll<AsteroidClientCulledTag, AsteroidTag>())
            {
                if (collider.ValueRO.Value == s_noCollide)
                    continue;
                collider.ValueRW.Value = s_noCollide;
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
