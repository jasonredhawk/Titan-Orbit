using TitanOrbit.Core;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Physics;
using Unity.Physics.Systems;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server: ensures the ramming contact queue singleton exists, then copies this tick's
    /// Unity Physics <see cref="CollisionEvent"/> stream into <see cref="PendingRamContactElement"/>.
    /// Runs inside <see cref="PhysicsSystemGroup"/> after the solver so events are current-frame.
    /// <para>
    /// [PHYSICS] Ship colliders use <c>CollideRaiseCollisionEvents</c> so ship↔world and
    /// ship↔ship contacts appear here. Flybys with no collider contact produce no events.
    /// Cross-seam asteroid hits are added later by <see cref="ShipToroidalWorldCollisionSystem"/>.
    /// </para>
    /// <para>
    /// [TITAN-ORBIT] Closing speed comes from <see cref="PhysicsVelocity"/> along the contact
    /// normal — not <c>CollisionEvent.CalculateDetails</c>. That details call walks the contact
    /// manifold every physics tick while grinding and hitch the server (and Local Host) so the
    /// whole match looks stepped. Impulse is left 0; damage prefers measured closing.
    /// </para>
    /// </summary>
    [UpdateInGroup(typeof(PhysicsSystemGroup))]
    [UpdateAfter(typeof(PhysicsSimulationGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    public partial struct ShipRamPhysicsContactCollectSystem : ISystem
    {
        /// <summary>Create the queue singleton once; require simulation + physics world.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<SimulationSingleton>();
            state.RequireForUpdate<PhysicsWorldSingleton>();

            // --- Singleton queue entity ---
            if (!SystemAPI.TryGetSingletonEntity<RamContactQueueTag>(out _))
            {
                var e = state.EntityManager.CreateEntity();
                state.EntityManager.AddComponentData(e, new RamContactQueueTag());
                state.EntityManager.AddBuffer<PendingRamContactElement>(e);
            }
        }

        /// <summary>
        /// Clears the pending queue, then schedules a collision-event job that appends real contacts.
        /// Classification (asteroid vs ship) happens in the damage system.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            if (!SystemAPI.TryGetSingletonBuffer<PendingRamContactElement>(out var queue))
                return;

            // Fresh frame — toroidal seam hits append after this system in OrderLast.
            queue.Clear();

            var pairs = new NativeList<RawPair>(32, state.WorldUpdateAllocator);
            var velocityLookup = SystemAPI.GetComponentLookup<PhysicsVelocity>(true);
            var preVelLookup = SystemAPI.GetComponentLookup<ShipPreCollisionVelocity>(true);

            state.Dependency = new CollectCollisionEventsJob
            {
                Pairs = pairs,
                Velocities = velocityLookup,
                PreCollision = preVelLookup,
            }.Schedule(SystemAPI.GetSingleton<SimulationSingleton>(), state.Dependency);

            state.Dependency.Complete();

            for (int i = 0; i < pairs.Length; i++)
            {
                RawPair p = pairs[i];
                queue.Add(new PendingRamContactElement
                {
                    Ship = p.EntityA,
                    Other = p.EntityB,
                    OtherIsShip = 0,
                    NormalShipFromOther = p.NormalAFromB,
                    ClosingSpeed = p.ClosingSpeed,
                    EstimatedImpulse = 0f,
                });
            }
        }

        /// <summary>Intermediate pair written by the Burst collision-event job.</summary>
        struct RawPair
        {
            public Entity EntityA;
            public Entity EntityB;
            public float3 NormalAFromB;
            public float ClosingSpeed;
        }

        /// <summary>
        /// Reads solver collision events. Does not classify tags; records EntityA/B and
        /// planar closing speed so the managed damage pass can resolve ship vs asteroid vs
        /// enemy ship without walking contact manifolds.
        /// </summary>
        [BurstCompile]
        struct CollectCollisionEventsJob : ICollisionEventsJob
        {
            public NativeList<RawPair> Pairs;

            [ReadOnly] public ComponentLookup<PhysicsVelocity> Velocities;
            [ReadOnly] public ComponentLookup<ShipPreCollisionVelocity> PreCollision;

            /// <summary>One solver contact pair this tick.</summary>
            public void Execute(CollisionEvent collisionEvent)
            {
                float3 normalAFromB = collisionEvent.Normal;
                normalAFromB.y = 0f;
                if (math.lengthsq(normalAFromB) > 1e-8f)
                    normalAFromB = math.normalize(normalAFromB);
                else
                    normalAFromB = new float3(0f, 0f, 1f);

                // --- Closing speed from pre-collision velocity (no CalculateDetails) ---
                // [PHYSICS] Event normal is B → A. Closing when A moves toward B (against the normal).
                // Prefer ShipPreCollisionVelocity (post-drive, pre-solve) so the first ram impact
                // still sees approach speed after the inelastic PhysX solve has killed relative n-vel.
                float3 vA = LinearOf(collisionEvent.EntityA);
                float3 vB = LinearOf(collisionEvent.EntityB);
                vA.y = 0f;
                vB.y = 0f;
                float closing = math.max(0f, -math.dot(vA - vB, normalAFromB));

                Pairs.Add(new RawPair
                {
                    EntityA = collisionEvent.EntityA,
                    EntityB = collisionEvent.EntityB,
                    NormalAFromB = normalAFromB,
                    ClosingSpeed = closing,
                });
            }

            /// <summary>Pre-collision ship vel, else PhysicsVelocity, else zero (static rock).</summary>
            float3 LinearOf(Entity entity)
            {
                if (PreCollision.HasComponent(entity))
                    return PreCollision[entity].Linear;
                if (Velocities.HasComponent(entity))
                    return Velocities[entity].Linear;
                return float3.zero;
            }
        }
    }
}
