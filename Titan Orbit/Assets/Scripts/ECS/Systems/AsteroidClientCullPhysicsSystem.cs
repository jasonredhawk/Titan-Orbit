using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Physics;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Client predicted step: drop culled asteroid hulls out of Unity Physics <b>before</b> the
    /// solver runs. Blob-swapping <see cref="PhysicsCollider"/> to a zero-filter sphere was not
    /// enough — static collision worlds often keep the previous sphere, so the ship still rammed
    /// empty space after the mesh hid.
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

        /// <summary>Dead-rock registry walk once per render frame, not every predicted resim.</summary>
        int _lastDeadScanFrame;

        /// <summary>
        /// No RequireForUpdate on CulledTag — we must also catch dead rocks that were never tagged.
        /// </summary>
        public void OnCreate(ref SystemState state)
        {
            EnsureNoCollideBlob();
        }

        /// <summary>
        /// Tags leftover dead rocks, then removes PhysicsCollider from every culled entity so
        /// this tick's physics step cannot block the ship.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // [TITAN-ORBIT] Structural collider strips during Settling Instantiates are
            // Crash!!!-adjacent — wait until map gathers are safe.
            if (ClientJoinSettleCache.ShouldSkipMapBodyQueries)
                return;

            EnsureNoCollideBlob();

            var em = state.EntityManager;

            // --- Catch dead rocks that still look solid (match miss / GO-only hide) ---
            // PredictedFixedStep resims several times per frame; the registry walk is O(rocks).
            // Profiler: this system was 2.2ms and then PhysicsInitialize rebuilt (3.5ms).
            int frame = UnityEngine.Time.frameCount;
            if (_lastDeadScanFrame != frame)
            {
                _lastDeadScanFrame = frame;
                ClientLocalAsteroidCombatSync.CullDeadAsteroidsStillSolid(em);
            }

            // --- Drop hulls from the static physics world (blob-swap is not enough) ---
            // [PHYSICS] RemoveComponent forces BuildPhysicsWorld to rebuild static bodies.
            // Do this before PhysicsSystemGroup this predicted step.
            var stripEcb = new EntityCommandBuffer(Allocator.Temp);
            int stripped = 0;
            foreach (var (_, entity) in SystemAPI
                         .Query<RefRO<PhysicsCollider>>()
                         .WithAll<AsteroidClientCulledTag>()
                         .WithEntityAccess())
            {
                stripEcb.RemoveComponent<PhysicsCollider>(entity);
                stripped++;
            }

            if (stripped > 0)
                stripEcb.Playback(em);
            stripEcb.Dispose();

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
