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
    /// MEGA hulls skip this pass (plow asteroids — no grip, no inward motor reject).
    /// Pipeline: Drive → Snapshot → PhysicsSimulation → Export → Bounce → Friction/Contact (this) →
    /// Planar → Kinematics.
    /// </summary>
    // [PHYSICS] AfterPhysicsSystemGroup sits after ExportPhysicsWorld inside PhysicsSystemGroup.
    // Do NOT UpdateAfter(PhysicsSimulationGroup) alone — that window forbids ECS velocity writes.
    [UpdateInGroup(typeof(AfterPhysicsSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    public partial struct ShipAsteroidContactFrictionSystem : ISystem
    {
        /// <summary>Need at least one ship; events are optional.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<ShipTag>();
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

            if (!SystemAPI.TryGetSingleton(out SimulationSingleton simSingleton))
                return;

            var events = new NativeList<CollisionEvent>(16, state.WorldUpdateAllocator);
            if (!PhysicsCollisionEventStream.TryCopyEvents(simSingleton, events))
            {
                events.Dispose();
                return;
            }

            // --- Designer slider (0 = skip tangential bleed only) ---
            var settings = AsteroidSettingsCache.ResolveOrDefault();
            settings.ClampValues();
            float friction = settings.Friction;

            float dt = SystemAPI.Time.DeltaTime;
            if (dt <= 0f)
                dt = 1f / 60f;

            var shipLookup = SystemAPI.GetComponentLookup<ShipTag>(true);
            var asteroidLookup = SystemAPI.GetComponentLookup<AsteroidTag>(true);
            var asteroidStateLookup = SystemAPI.GetComponentLookup<AsteroidState>(true);
            var megaLookup = SystemAPI.GetComponentLookup<MegaShipState>(true);
            var culledLookup = SystemAPI.GetComponentLookup<AsteroidClientCulledTag>(true);
            var velocityLookup = SystemAPI.GetComponentLookup<PhysicsVelocity>(false);
            var contactLookup = SystemAPI.GetComponentLookup<ShipAsteroidContactState>(false);
            var seen = new NativeHashSet<long>(16, Allocator.Temp);

            for (int i = 0; i < events.Length; i++)
            {
                CollisionEvent ev = events[i];
                Entity a = ev.EntityA;
                Entity b = ev.EntityB;
                bool aShip = shipLookup.HasComponent(a);
                bool bRock = asteroidLookup.HasComponent(b);
                bool bShip = shipLookup.HasComponent(b);
                bool aRock = asteroidLookup.HasComponent(a);
                if (!((aShip && bRock) || (bShip && aRock)))
                    continue;

                Entity ship = aShip ? a : b;
                Entity rock = aShip ? b : a;
                long key = ((long)ship.Index << 32) ^ (uint)rock.Index;
                if (!seen.Add(key))
                    continue;

                ApplyAsteroidFrictionEvent(
                    ev, shipLookup, asteroidLookup, asteroidStateLookup, megaLookup,
                    culledLookup, velocityLookup, contactLookup, friction, dt);
            }

            seen.Dispose();
            events.Dispose();
        }

        /// <summary>
        /// For each PhysX collision event, if one body is a ship and the other an asteroid,
        /// records the outward XZ normal for next-tick motor reject and (when friction &gt; 0)
        /// damps tangential slide via <see cref="AsteroidColliderMaterialLogic"/>.
        /// </summary>
        static void ApplyAsteroidFrictionEvent(
            in CollisionEvent collisionEvent,
            ComponentLookup<ShipTag> ships,
            ComponentLookup<AsteroidTag> asteroids,
            ComponentLookup<AsteroidState> asteroidStates,
            ComponentLookup<MegaShipState> megas,
            ComponentLookup<AsteroidClientCulledTag> culled,
            ComponentLookup<PhysicsVelocity> velocities,
            ComponentLookup<ShipAsteroidContactState> contacts,
            float friction,
            float deltaTime)
        {
            Entity a = collisionEvent.EntityA;
            Entity b = collisionEvent.EntityB;

            Entity ship = Entity.Null;
            Entity other = Entity.Null;
            bool normalFromOtherToShip = true;

            if (ships.HasComponent(a) && asteroids.HasComponent(b))
            {
                ship = a;
                other = b;
                normalFromOtherToShip = true;
            }
            else if (ships.HasComponent(b) && asteroids.HasComponent(a))
            {
                ship = b;
                other = a;
                normalFromOtherToShip = false;
            }
            else
            {
                return;
            }

            if (megas.HasComponent(ship) && megas[ship].IsMega)
                return;

            if (culled.HasComponent(other))
                return;
            if (asteroidStates.HasComponent(other))
            {
                var rock = asteroidStates[other];
                if (rock.IsDestroyed || !(rock.Health > 0.01f))
                    return;
            }

            float3 normal = collisionEvent.Normal;
            if (!normalFromOtherToShip)
                normal = -normal;
            normal.y = 0f;
            if (math.lengthsq(normal) < 1e-8f)
                return;
            normal = math.normalize(normal);

            if (contacts.HasComponent(ship))
            {
                contacts[ship] = new ShipAsteroidContactState
                {
                    InContact = 1,
                    OutwardNormal = normal,
                };
            }

            if (friction <= 0f || !velocities.HasComponent(ship))
                return;

            var vel = velocities[ship];
            float3 lin = AsteroidColliderMaterialLogic.ApplyTangentialFriction(
                vel.Linear, normal, friction, deltaTime);
            vel.Linear = lin;
            velocities[ship] = vel;
        }

    }
}
