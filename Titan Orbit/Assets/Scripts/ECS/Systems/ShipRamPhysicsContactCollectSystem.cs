using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Physics;
using Unity.Physics.Systems;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server: ensures the ramming contact queue exists, then copies this tick's PhysX
    /// ship↔asteroid events into <see cref="PendingRamContactElement"/>. Ship↔ship ram is
    /// appended after this by <see cref="ShipShipHullContactSystem"/> (PhysX does not pair ships).
    /// Consumed by <see cref="ShipRammingCollisionDamageSystem"/>.
    /// </summary>
    [UpdateInGroup(typeof(AfterPhysicsSystemGroup))]
    [UpdateAfter(typeof(ShipCollisionBounceSystem))]
    [UpdateBefore(typeof(ShipShipHullContactSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    public partial struct ShipRamPhysicsContactCollectSystem : ISystem
    {
        /// <summary>Create the queue singleton once; require simulation + physics world.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<SimulationSingleton>();
            state.RequireForUpdate<PhysicsWorldSingleton>();

            if (!SystemAPI.TryGetSingletonEntity<RamContactQueueTag>(out _))
            {
                var e = state.EntityManager.CreateEntity();
                state.EntityManager.AddComponentData(e, new RamContactQueueTag());
                state.EntityManager.AddBuffer<PendingRamContactElement>(e);
            }
        }

        /// <summary>
        /// Clears last tick's queue, then appends this tick's hull contacts from collision events.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            if (!SystemAPI.TryGetSingletonBuffer<PendingRamContactElement>(out var queue))
                return;

            queue.Clear();

            if (!SystemAPI.TryGetSingleton(out SimulationSingleton sim))
                return;

            var events = new NativeList<CollisionEvent>(32, state.WorldUpdateAllocator);
            if (!PhysicsCollisionEventStream.TryCopyEvents(sim, events))
            {
                events.Dispose();
                return;
            }

            var ships = SystemAPI.GetComponentLookup<ShipTag>(true);
            var asteroids = SystemAPI.GetComponentLookup<AsteroidTag>(true);
            var snapshots = SystemAPI.GetComponentLookup<ShipPreCollisionVelocity>(true);
            var velocities = SystemAPI.GetComponentLookup<PhysicsVelocity>(true);
            var seen = new NativeHashSet<long>(32, Allocator.Temp);

            for (int i = 0; i < events.Length; i++)
            {
                CollisionEvent ev = events[i];
                Entity a = ev.EntityA;
                Entity b = ev.EntityB;
                bool aShip = ships.HasComponent(a);
                bool bShip = ships.HasComponent(b);

                float3 normalAFromB = ev.Normal;
                normalAFromB.y = 0f;
                if (math.lengthsq(normalAFromB) > 1e-8f)
                    normalAFromB = math.normalize(normalAFromB);
                else
                    normalAFromB = new float3(0f, 0f, 1f);

                if (aShip && bShip)
                    continue;

                Entity ship;
                Entity other;
                float3 normalShipFromOther;
                if (aShip && asteroids.HasComponent(b))
                {
                    ship = a;
                    other = b;
                    normalShipFromOther = normalAFromB;
                }
                else if (bShip && asteroids.HasComponent(a))
                {
                    ship = b;
                    other = a;
                    normalShipFromOther = -normalAFromB;
                }
                else
                {
                    continue;
                }

                if (!seen.Add(PackPairKey(ship, other)))
                    continue;

                float3 vShip = LinearOf(ship, snapshots, velocities);
                float closingRock = math.max(0f, -math.dot(vShip, normalShipFromOther));
                queue.Add(new PendingRamContactElement
                {
                    Ship = ship,
                    Other = other,
                    OtherIsShip = 0,
                    NormalShipFromOther = normalShipFromOther,
                    ClosingSpeed = closingRock,
                    EstimatedImpulse = closingRock * 10f,
                });
            }

            events.Dispose();
            seen.Dispose();
        }

        static long PackPairKey(Entity a, Entity b)
        {
            int aIdx = a.Index;
            int bIdx = b.Index;
            if (aIdx > bIdx)
                (aIdx, bIdx) = (bIdx, aIdx);
            return ((long)aIdx << 32) ^ (uint)bIdx;
        }

        static float3 LinearOf(
            Entity e,
            ComponentLookup<ShipPreCollisionVelocity> snapshots,
            ComponentLookup<PhysicsVelocity> velocities)
        {
            if (snapshots.HasComponent(e))
                return snapshots[e].Linear;
            if (velocities.HasComponent(e))
                return velocities[e].Linear;
            return float3.zero;
        }
    }
}
