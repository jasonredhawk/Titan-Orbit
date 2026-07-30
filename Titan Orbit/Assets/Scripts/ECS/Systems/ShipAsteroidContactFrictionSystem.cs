using TitanOrbit.Data;
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
    /// After Unity Physics exports contacts, bleeds ship tangential (slide) velocity on
    /// ship↔asteroid collision events using <see cref="AsteroidSettings.Friction"/>.
    /// Same-tile PhysX often still feels icy because the ship hull uses Friction 0.05 with
    /// GeometricMean combine — this pass makes the Inspector slider feel immediate for rams/grinds.
    /// <para>
    /// Runs on ServerSimulation and ClientSimulation (predicted) so grip matches. Uses the
    /// CollisionEvent stream only — no asteroid <c>ToEntityArray</c> (join-crash safe).
    /// </para>
    /// <para>
    /// [PHYSICS] Must run in <see cref="AfterPhysicsSystemGroup"/> (after
    /// <see cref="ExportPhysicsWorld"/>). Writing <see cref="PhysicsVelocity"/> between
    /// BuildPhysicsWorld and Export throws
    /// "changing … velocity … on dynamic entities during physics step". Unity's own
    /// DisplayCollisionEventsSystem uses the same AfterPhysics slot for event jobs.
    /// </para>
    /// <para>
    /// [TITAN-ORBIT] On the client, skip while <see cref="ClientJoinSettleCache.ShouldSkipShipEntityQueries"/>
    /// (Settling / GhostSpawnBacklog / post–TeamChoice hold). ShipTag ComponentLookup during
    /// TeamChoice Instantiates Crash!!! — server always applies friction.
    /// </para>
    /// Pipeline: Drive → Snapshot → PhysicsSimulation → Export → Bounce → Friction (this) → Toroidal / Planar / Kinematics.
    /// </summary>
    // [PHYSICS] AfterPhysicsSystemGroup sits after ExportPhysicsWorld inside PhysicsSystemGroup.
    // Do NOT UpdateAfter(PhysicsSimulationGroup) alone — that window forbids ECS velocity writes.
    [UpdateInGroup(typeof(AfterPhysicsSystemGroup))]
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
        /// asteroid collision event this tick. Safe to write <see cref="PhysicsVelocity"/>
        /// here because ExportPhysicsWorld has already finished.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // --- Join-crash gate (client only) ---
            // [TITAN-ORBIT] Collision-event ComponentLookup on ShipTag still touches ship
            // archetypes. During TeamChoice Instantiates Settling is OFF but GhostSpawnBacklog
            // is ON — ungated ship lookups Crash!!! (Player.log 2026-07-19 / 07-22).
            // Server always applies friction. Skip a few client frames; prediction resumes after.
            if (state.World.IsClient() && ClientJoinSettleCache.ShouldSkipShipEntityQueries)
                return;

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
            // [PHYSICS] ICollisionEventsJob is still valid in AfterPhysicsSystemGroup (same as
            // Unity's DisplayCollisionEventsSystem). Writing Velocities here is legal post-Export.
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

            // Need velocities written before OrderLast systems (toroidal / planar / kinematics).
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
