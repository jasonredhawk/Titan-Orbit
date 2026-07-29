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
            var physicsWorldSingleton = SystemAPI.GetSingleton<PhysicsWorldSingleton>();

            state.Dependency = new CollectCollisionEventsJob
            {
                Pairs = pairs,
                PhysicsWorldSingleton = physicsWorldSingleton,
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
                    ClosingSpeed = 0f,
                    EstimatedImpulse = p.EstimatedImpulse,
                });
            }
        }

        /// <summary>Intermediate pair written by the Burst collision-event job.</summary>
        struct RawPair
        {
            public Entity EntityA;
            public Entity EntityB;
            public float3 NormalAFromB;
            public float EstimatedImpulse;
        }

        /// <summary>
        /// Reads solver collision events. Does not classify tags; records EntityA/B and impulse
        /// so the managed damage pass can resolve ship vs asteroid vs enemy ship.
        /// </summary>
        [BurstCompile]
        struct CollectCollisionEventsJob : ICollisionEventsJob
        {
            public NativeList<RawPair> Pairs;

            [ReadOnly] public PhysicsWorldSingleton PhysicsWorldSingleton;

            /// <summary>One solver contact pair this tick.</summary>
            public void Execute(CollisionEvent collisionEvent)
            {
                // --- Impulse from solver details (post-solve velocities are already reflected) ---
                var world = PhysicsWorldSingleton.PhysicsWorld;
                var details = collisionEvent.CalculateDetails(ref world);
                float impulse = math.max(0f, details.EstimatedImpulse);

                float3 normalAFromB = collisionEvent.Normal;
                normalAFromB.y = 0f;
                if (math.lengthsq(normalAFromB) > 1e-8f)
                    normalAFromB = math.normalize(normalAFromB);
                else
                    normalAFromB = new float3(0f, 0f, 1f);

                Pairs.Add(new RawPair
                {
                    EntityA = collisionEvent.EntityA,
                    EntityB = collisionEvent.EntityB,
                    NormalAFromB = normalAFromB,
                    EstimatedImpulse = impulse,
                });
            }
        }
    }
}
