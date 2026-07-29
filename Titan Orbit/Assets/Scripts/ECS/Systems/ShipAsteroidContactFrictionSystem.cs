using TitanOrbit.Data;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using Unity.Physics.Systems;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// After Unity Physics solves contacts, bleeds ship tangential (slide) velocity on
    /// ship↔asteroid collision events using <see cref="AsteroidSettings.Friction"/>.
    /// Same-tile PhysX often still feels icy because the ship hull uses Friction 0.05 with
    /// GeometricMean combine — this pass makes the Inspector slider feel immediate for rams/grinds.
    /// <para>
    /// Runs on ServerSimulation and ClientSimulation (predicted) so grip matches. Uses the
    /// CollisionEvent stream only — no asteroid <c>ToEntityArray</c> (join-crash safe).
    /// </para>
    /// </summary>
    [UpdateInGroup(typeof(PhysicsSystemGroup))]
    [UpdateAfter(typeof(PhysicsSimulationGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    public partial struct ShipAsteroidContactFrictionSystem : ISystem
    {
        /// <summary>Require physics simulation + world for collision events.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<SimulationSingleton>();
            state.RequireForUpdate<PhysicsWorldSingleton>();
        }

        /// <summary>
        /// Reads designer friction, then applies tangential damping to every ship in an
        /// asteroid collision event this tick.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // --- Designer slider ---
            var settings = AsteroidSettingsCache.ResolveOrDefault();
            settings.ClampValues();
            float friction = settings.Friction;
            if (friction <= 0f)
                return;

            float dt = SystemAPI.Time.DeltaTime;
            if (dt <= 0f)
                dt = 1f / 60f;

            // --- Collision-event job (no full asteroid gather) ---
            var shipLookup = SystemAPI.GetComponentLookup<ShipTag>(true);
            var asteroidLookup = SystemAPI.GetComponentLookup<AsteroidTag>(true);
            var velocityLookup = SystemAPI.GetComponentLookup<PhysicsVelocity>(false);

            state.Dependency = new ApplyAsteroidFrictionJob
            {
                Ships = shipLookup,
                Asteroids = asteroidLookup,
                Velocities = velocityLookup,
                Friction = friction,
                DeltaTime = dt,
            }.Schedule(SystemAPI.GetSingleton<SimulationSingleton>(), state.Dependency);

            // Need velocities written before later systems (toroidal / kinematics) read them.
            state.Dependency.Complete();
        }

        /// <summary>
        /// For each PhysX collision event, if one body is a ship and the other an asteroid,
        /// damp the ship's tangential velocity using <see cref="AsteroidColliderMaterialLogic"/>.
        /// </summary>
        [BurstCompile]
        struct ApplyAsteroidFrictionJob : ICollisionEventsJob
        {
            [ReadOnly] public ComponentLookup<ShipTag> Ships;
            [ReadOnly] public ComponentLookup<AsteroidTag> Asteroids;
            public ComponentLookup<PhysicsVelocity> Velocities;
            public float Friction;
            public float DeltaTime;

            /// <summary>One solver contact pair this tick.</summary>
            public void Execute(CollisionEvent collisionEvent)
            {
                Entity ship = Entity.Null;
                Entity other = Entity.Null;
                bool normalFromOtherToShip = true;

                if (Ships.HasComponent(collisionEvent.EntityA) &&
                    Asteroids.HasComponent(collisionEvent.EntityB))
                {
                    ship = collisionEvent.EntityA;
                    other = collisionEvent.EntityB;
                    // Normal is from B → A in Unity Physics collision events.
                    normalFromOtherToShip = true;
                }
                else if (Ships.HasComponent(collisionEvent.EntityB) &&
                         Asteroids.HasComponent(collisionEvent.EntityA))
                {
                    ship = collisionEvent.EntityB;
                    other = collisionEvent.EntityA;
                    normalFromOtherToShip = false;
                }
                else
                {
                    return;
                }

                if (!Velocities.HasComponent(ship) || other == Entity.Null)
                    return;

                float3 normal = collisionEvent.Normal;
                if (!normalFromOtherToShip)
                    normal = -normal;
                normal.y = 0f;
                if (math.lengthsq(normal) < 1e-8f)
                    return;
                normal = math.normalize(normal);

                var vel = Velocities[ship];
                float3 lin = AsteroidColliderMaterialLogic.ApplyTangentialFriction(
                    vel.Linear, normal, Friction, DeltaTime);
                vel.Linear = lin;
                Velocities[ship] = vel;
            }
        }
    }
}
