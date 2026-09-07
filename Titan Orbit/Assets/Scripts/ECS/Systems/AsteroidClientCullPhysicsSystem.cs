using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Physics;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Client predicted step: disable culled asteroid hulls <b>before</b> the solver runs.
    /// Swaps <see cref="PhysicsCollider"/> to a shared zero-filter sphere. Incremental static
    /// broadphase updates that BVH leaf — do not <c>RemoveComponent&lt;PhysicsCollider&gt;</c>
    /// (that rebuilt the whole static world).
    /// <para>
    /// Also walks the Instantiates registry for dead rocks that never got
    /// <see cref="AsteroidClientCulledTag"/> (HitRpc hide / GO teardown race).
    /// </para>
    /// World: ClientSimulation. Group: PredictedFixedStepSimulationSystemGroup OrderFirst
    /// (before PhysicsSystemGroup). Paired with <see cref="Game.ClientAsteroidCollisionCull"/>.
    /// </summary>
    // OrderFirst: before default-slot physics without UpdateInGroup(PhysicsSystemGroup) —
    // ClientWorld often lacks PhysicsSystemGroup as a PredictedFixedStep sibling (sorter spam).
    [UpdateInGroup(typeof(PredictedFixedStepSimulationSystemGroup), OrderFirst = true)]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    public partial struct AsteroidClientCullPhysicsSystem : ISystem
    {
        /// <summary>Shared zero-filter sphere — never mutate bake-shared asteroid blobs.</summary>
        static BlobAssetReference<Collider> s_noCollide;

        /// <summary>Last Unity frame we walked the Instantiates registry for leftover solids.</summary>
        static int s_DeadSolidCullFrame = -1;

        /// <summary>
        /// No RequireForUpdate on CulledTag — we must also catch dead rocks that were never tagged.
        /// </summary>
        public void OnCreate(ref SystemState state)
        {
            EnsureNoCollideBlob();
        }

        /// <summary>
        /// Tags leftover dead rocks and keeps culled LocalTransform scale squashed.
        /// Collision disable is the shared no-collide blob on <see cref="PhysicsCollider"/>.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // [TITAN-ORBIT] Structural collider strips during Settling Instantiates are
            // Crash!!!-adjacent — wait until map gathers are safe.
            if (ClientJoinSettleCache.ShouldSkipMapBodyQueries)
                return;

            EnsureNoCollideBlob();

            var em = state.EntityManager;

            // PredictedFixedStep can run several times per display frame. The registry walk
            // is enough once per Unity frame — extra ticks only squash already-culled scale.
            if (s_DeadSolidCullFrame != UnityEngine.Time.frameCount)
            {
                s_DeadSolidCullFrame = UnityEngine.Time.frameCount;
                ClientLocalAsteroidCombatSync.CullDeadAsteroidsStillSolid(em);
            }

            // Keep PhysicsCollider. Incremental static broadphase updates the BVH leaf
            // when CullPhysics swaps the shared no-collide blob. RemoveComponent forced
            // a full BuildPhysicsWorld (profiler: 104ms) plus PhysicsWorldHistory copy.

            // --- Keep scale squashed if a collider is restored this tick before strip ---
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
