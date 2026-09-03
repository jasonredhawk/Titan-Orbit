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
    /// Single walk of the Unity Physics collision-event stream after Export.
    /// Classifies ship↔ship and ship↔world contacts into
    /// <see cref="ShipPhysicsContactElement"/> for bounce, friction, and ram.
    /// <para>
    /// This is the contact pipeline for 60–100 ships: narrowphase stays in Unity Physics
    /// (compound hulls, layer filters, speculative CCD via <see cref="PhysicsStep.CollisionTolerance"/>).
    /// Gameplay is O(contacts), not O(ships × world).
    /// </para>
    /// </summary>
    [UpdateInGroup(typeof(AfterPhysicsSystemGroup))]
    [UpdateBefore(typeof(ShipCollisionBounceSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    public partial struct ShipPhysicsContactCollectSystem : ISystem
    {
        /// <summary>Create the contact-queue singleton; require the solver event stream.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<SimulationSingleton>();
            state.RequireForUpdate<PhysicsWorldSingleton>();

            if (!SystemAPI.TryGetSingletonEntity<ShipPhysicsContactQueueTag>(out _))
            {
                var e = state.EntityManager.CreateEntity();
                state.EntityManager.AddComponentData(e, new ShipPhysicsContactQueueTag());
                state.EntityManager.AddBuffer<ShipPhysicsContactElement>(e);
            }
        }

        /// <summary>
        /// Clears last tick's contacts, then Burst-classifies this step's collision events.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            if (state.World.IsClient() && ClientJoinSettleCache.ShouldSkipShipSimulation)
                return;

            if (!SystemAPI.TryGetSingletonBuffer<ShipPhysicsContactElement>(out var queue))
                return;

            queue.Clear();

            var pairs = new NativeList<ShipPhysicsContactElement>(64, state.WorldUpdateAllocator);
            state.Dependency = new ClassifyContactsJob
            {
                Pairs = pairs,
                Ships = SystemAPI.GetComponentLookup<ShipTag>(true),
                Asteroids = SystemAPI.GetComponentLookup<AsteroidTag>(true),
                Planets = SystemAPI.GetComponentLookup<PlanetTag>(true),
                Moons = SystemAPI.GetComponentLookup<PlanetGemMoonColliderTag>(true),
                Velocities = SystemAPI.GetComponentLookup<PhysicsVelocity>(true),
                PreCollision = SystemAPI.GetComponentLookup<ShipPreCollisionVelocity>(true),
            }.Schedule(SystemAPI.GetSingleton<SimulationSingleton>(), state.Dependency);
            state.Dependency.Complete();

            for (int i = 0; i < pairs.Length; i++)
                queue.Add(pairs[i]);
        }

        /// <summary>
        /// One solver pair → at most one classified ship contact. Closing speed uses
        /// pre-collision velocity so grind/ram still see approach after an inelastic solve.
        /// </summary>
        [BurstCompile]
        struct ClassifyContactsJob : ICollisionEventsJob
        {
            public NativeList<ShipPhysicsContactElement> Pairs;

            [ReadOnly] public ComponentLookup<ShipTag> Ships;
            [ReadOnly] public ComponentLookup<AsteroidTag> Asteroids;
            [ReadOnly] public ComponentLookup<PlanetTag> Planets;
            [ReadOnly] public ComponentLookup<PlanetGemMoonColliderTag> Moons;
            [ReadOnly] public ComponentLookup<PhysicsVelocity> Velocities;
            [ReadOnly] public ComponentLookup<ShipPreCollisionVelocity> PreCollision;

            public void Execute(CollisionEvent collisionEvent)
            {
                Entity a = collisionEvent.EntityA;
                Entity b = collisionEvent.EntityB;
                float3 normalAFromB = collisionEvent.Normal;
                normalAFromB.y = 0f;
                if (math.lengthsq(normalAFromB) > 1e-8f)
                    normalAFromB = math.normalize(normalAFromB);
                else
                    normalAFromB = new float3(0f, 0f, 1f);

                bool aShip = Ships.HasComponent(a);
                bool bShip = Ships.HasComponent(b);
                if (!aShip && !bShip)
                    return;

                Entity ship;
                Entity other;
                float3 normalShipFromOther;
                byte kind;

                if (aShip && bShip)
                {
                    ship = a;
                    other = b;
                    normalShipFromOther = normalAFromB;
                    kind = ShipPhysicsContactKind.Ship;
                }
                else
                {
                    if (aShip)
                    {
                        ship = a;
                        other = b;
                        normalShipFromOther = normalAFromB;
                    }
                    else
                    {
                        ship = b;
                        other = a;
                        normalShipFromOther = -normalAFromB;
                    }

                    if (Asteroids.HasComponent(other))
                        kind = ShipPhysicsContactKind.Asteroid;
                    else if (Planets.HasComponent(other))
                        kind = ShipPhysicsContactKind.Planet;
                    else if (Moons.HasComponent(other))
                        kind = ShipPhysicsContactKind.Moon;
                    else
                        return;
                }

                float3 vShip = LinearOf(ship);
                float3 vOther = LinearOf(other);
                vShip.y = 0f;
                vOther.y = 0f;
                float closing = math.max(0f, -math.dot(vShip - vOther, normalShipFromOther));

                Pairs.Add(new ShipPhysicsContactElement
                {
                    Ship = ship,
                    Other = other,
                    NormalShipFromOther = normalShipFromOther,
                    ClosingSpeed = closing,
                    Kind = kind,
                });
            }

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
