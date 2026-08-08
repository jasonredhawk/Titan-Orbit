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
    /// After Unity Physics exports contacts:
    /// <list type="number">
    /// <item>
    /// Clears then writes <see cref="ShipAsteroidContactState"/> so the next drive tick can
    /// reject inward motor velocity (stops progressive grind dig-in under continuous thrust).
    /// </item>
    /// <item>
    /// Bleeds ship tangential (slide) velocity using <see cref="AsteroidSettings.Friction"/>.
    /// Same-tile PhysX often still feels icy because the ship hull uses Friction 0.05 with
    /// GeometricMean combine — this pass makes the Inspector slider feel immediate for rams/grinds.
    /// </item>
    /// </list>
    /// Runs on ServerSimulation and ClientSimulation (predicted) so grip and contact reject match.
    /// Uses the CollisionEvent stream only — no full asteroid entity gather (join-crash safe).
    /// <para>
    /// [PHYSICS] Must run in <see cref="AfterPhysicsSystemGroup"/> (after
    /// <see cref="ExportPhysicsWorld"/>). Writing <see cref="PhysicsVelocity"/> between
    /// BuildPhysicsWorld and Export throws
    /// "changing … velocity … on dynamic entities during physics step". Unity's own
    /// DisplayCollisionEventsSystem uses the same AfterPhysics slot for event jobs.
    /// </para>
    /// <para>
    /// [TITAN-ORBIT] On the client, skip while <see cref="ClientJoinSettleCache.ShouldSkipShipSimulation"/>
    /// (Settling / TeamChoice hold). Intentional: not
    /// <see cref="ClientJoinSettleCache.ShouldSkipShipEntityQueries"/> — map Instantiates must not
    /// freeze friction/contact. ShipTag ComponentLookup during TeamChoice Instantiates Crash!!! —
    /// server always applies. Do <b>not</b> push ship position with AABB sphere radii — compound hulls
    /// over-estimate and violently shove (reverted 2026-08-07).
    /// </para>
    /// Pipeline: Drive → Snapshot → PhysicsSimulation → Export → Bounce → Friction/Contact (this) →
    /// Toroidal → Planar → Kinematics.
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
        /// Clears contact caches, then applies tangential damping and records contact normals for
        /// every ship in an asteroid collision event this tick. Safe to write
        /// <see cref="PhysicsVelocity"/> here because ExportPhysicsWorld has already finished.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // --- Join-crash gate (client only) ---
            // [TITAN-ORBIT] Collision-event ComponentLookup on ShipTag during TeamChoice
            // Instantiates Crash!!! (Player.log 2026-07-19 / 07-22). Gate with
            // ShouldSkipShipSimulation so friction/contact resume after Join Team while asteroids
            // still stream. Server always applies.
            if (state.World.IsClient() && ClientJoinSettleCache.ShouldSkipShipSimulation)
                return;

            // --- Clear last tick's contact flags (drive reads these next fixed step) ---
            // [TITAN-ORBIT] Must clear even when Friction is 0 — contact reject is independent of grip.
            foreach (var contact in SystemAPI
                         .Query<RefRW<ShipAsteroidContactState>>()
                         .WithAll<ShipTag, Simulate>())
            {
                contact.ValueRW = default;
            }

            // --- Designer slider (0 = skip tangential bleed only) ---
            var settings = AsteroidSettingsCache.ResolveOrDefault();
            settings.ClampValues();
            float friction = settings.Friction;

            float dt = SystemAPI.Time.DeltaTime;
            if (dt <= 0f)
                dt = 1f / 60f;

            // --- Collision-event job (no full asteroid entity gather) ---
            // [PHYSICS] ICollisionEventsJob is still valid in AfterPhysicsSystemGroup (same as
            // Unity's DisplayCollisionEventsSystem). Writing Velocities here is legal post-Export.
            var shipLookup = SystemAPI.GetComponentLookup<ShipTag>(true);
            var asteroidLookup = SystemAPI.GetComponentLookup<AsteroidTag>(true);
            var velocityLookup = SystemAPI.GetComponentLookup<PhysicsVelocity>(false);
            var contactLookup = SystemAPI.GetComponentLookup<ShipAsteroidContactState>(false);

            state.Dependency = new ApplyAsteroidFrictionJob
            {
                Ships = shipLookup,
                Asteroids = asteroidLookup,
                Velocities = velocityLookup,
                Contacts = contactLookup,
                Friction = friction,
                DeltaTime = dt,
            }.Schedule(SystemAPI.GetSingleton<SimulationSingleton>(), state.Dependency);

            // Need velocities + contact state written before OrderLast / next drive tick.
            state.Dependency.Complete();
        }

        /// <summary>
        /// For each PhysX collision event, if one body is a ship and the other an asteroid,
        /// records the outward XZ normal for next-tick motor reject and (when friction &gt; 0)
        /// damps tangential slide via <see cref="AsteroidColliderMaterialLogic"/>.
        /// </summary>
        [BurstCompile]
        struct ApplyAsteroidFrictionJob : ICollisionEventsJob
        {
            [ReadOnly] public ComponentLookup<ShipTag> Ships;
            [ReadOnly] public ComponentLookup<AsteroidTag> Asteroids;
            public ComponentLookup<PhysicsVelocity> Velocities;
            public ComponentLookup<ShipAsteroidContactState> Contacts;
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

                if (other == Entity.Null)
                    return;

                float3 normal = collisionEvent.Normal;
                if (!normalFromOtherToShip)
                    normal = -normal;
                normal.y = 0f;
                if (math.lengthsq(normal) < 1e-8f)
                    return;
                normal = math.normalize(normal);

                // --- Contact cache for next drive tick (inward motor reject) ---
                if (Contacts.HasComponent(ship))
                {
                    Contacts[ship] = new ShipAsteroidContactState
                    {
                        InContact = 1,
                        OutwardNormal = normal,
                    };
                }

                // --- Tangential grip (optional; Friction 0 still keeps contact reject) ---
                if (Friction <= 0f || !Velocities.HasComponent(ship))
                    return;

                var vel = Velocities[ship];
                float3 lin = AsteroidColliderMaterialLogic.ApplyTangentialFriction(
                    vel.Linear, normal, Friction, DeltaTime);
                vel.Linear = lin;
                Velocities[ship] = vel;
            }
        }
    }
}
