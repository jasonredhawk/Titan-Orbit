using Unity.Entities;
using Unity.NetCode;
using Unity.Physics.Systems;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server: copies this tick's classified Unity Physics contacts into
    /// <see cref="PendingRamContactElement"/> for <see cref="ShipRammingCollisionDamageSystem"/>.
    /// Ship↔asteroid and ship↔ship only — planets/moons are bounce-only.
    /// </summary>
    [UpdateInGroup(typeof(AfterPhysicsSystemGroup))]
    [UpdateAfter(typeof(ShipPhysicsContactCollectSystem))]
    [UpdateAfter(typeof(ShipCircleOverlapSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    public partial struct ShipRamPhysicsContactCollectSystem : ISystem
    {
        /// <summary>Create the ram queue singleton; require classified contacts.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<ShipPhysicsContactQueueTag>();

            if (!SystemAPI.TryGetSingletonEntity<RamContactQueueTag>(out _))
            {
                var e = state.EntityManager.CreateEntity();
                state.EntityManager.AddComponentData(e, new RamContactQueueTag());
                state.EntityManager.AddBuffer<PendingRamContactElement>(e);
            }
        }

        /// <summary>
        /// Clears the pending ram queue, then copies asteroid and ship contacts.
        /// Closing speed was measured at collect time from pre-collision velocity.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            if (!SystemAPI.TryGetSingletonBuffer<PendingRamContactElement>(out var queue))
                return;
            queue.Clear();

            if (!SystemAPI.TryGetSingletonBuffer<ShipPhysicsContactElement>(out var pairs))
                return;

            for (int i = 0; i < pairs.Length; i++)
            {
                ShipPhysicsContactElement p = pairs[i];
                if (p.Kind != ShipPhysicsContactKind.Asteroid &&
                    p.Kind != ShipPhysicsContactKind.Ship)
                    continue;

                queue.Add(new PendingRamContactElement
                {
                    Ship = p.Ship,
                    Other = p.Other,
                    OtherIsShip = p.Kind == ShipPhysicsContactKind.Ship ? (byte)1 : (byte)0,
                    NormalShipFromOther = p.NormalShipFromOther,
                    ClosingSpeed = p.ClosingSpeed,
                    EstimatedImpulse = 0f,
                });
            }
        }
    }
}
