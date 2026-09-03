using TitanOrbit.Data;
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
    /// reject inward motor velocity into asteroids (ship↔ship is Unity Physics only).
    /// </item>
    /// <item>
    /// Bleeds ship tangential (slide) velocity using <see cref="AsteroidSettings.Friction"/>.
    /// Same-tile PhysX often still feels icy because the ship hull uses Friction 0.05 with
    /// GeometricMean combine — this pass makes the Inspector slider feel immediate for rams/grinds.
    /// </item>
    /// </list>
    /// Consumes <see cref="ShipPhysicsContactElement"/> (one event walk per tick).
    /// MEGA hulls skip this pass (plow asteroids — no grip, no inward motor reject).
    /// Pipeline: Drive → Snapshot → PhysicsSimulation → Export → ContactCollect → Bounce →
    /// Friction/Contact (this) → Wrap → Planar → Kinematics.
    /// </summary>
    [UpdateInGroup(typeof(AfterPhysicsSystemGroup))]
    [UpdateAfter(typeof(ShipCollisionBounceSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    public partial struct ShipAsteroidContactFrictionSystem : ISystem
    {
        /// <summary>Require the classified contact buffer.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<ShipPhysicsContactQueueTag>();
        }

        /// <summary>
        /// Clears contact caches, then applies tangential damping and records contact normals for
        /// every ship↔asteroid contact this tick. Safe to write
        /// <see cref="PhysicsVelocity"/> here because ExportPhysicsWorld has already finished.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            if (state.World.IsClient() && ClientJoinSettleCache.ShouldSkipShipSimulation)
                return;

            foreach (var contact in SystemAPI
                         .Query<RefRW<ShipAsteroidContactState>>()
                         .WithAll<ShipTag, Simulate>())
            {
                contact.ValueRW = default;
            }

            var settings = AsteroidSettingsCache.ResolveOrDefault();
            settings.ClampValues();
            float friction = settings.Friction;

            float dt = SystemAPI.Time.DeltaTime;
            if (dt <= 0f)
                dt = 1f / 60f;

            if (!SystemAPI.TryGetSingletonBuffer<ShipPhysicsContactElement>(out var pairs) ||
                pairs.Length == 0)
                return;

            var asteroidStateLookup = SystemAPI.GetComponentLookup<AsteroidState>(true);
            var megaLookup = SystemAPI.GetComponentLookup<MegaShipState>(true);
            var culledLookup = SystemAPI.GetComponentLookup<AsteroidClientCulledTag>(true);
            var velocityLookup = SystemAPI.GetComponentLookup<PhysicsVelocity>(false);
            var contactLookup = SystemAPI.GetComponentLookup<ShipAsteroidContactState>(false);

            for (int i = 0; i < pairs.Length; i++)
            {
                ShipPhysicsContactElement pair = pairs[i];
                if (pair.Kind != ShipPhysicsContactKind.Asteroid)
                    continue;

                Entity ship = pair.Ship;
                Entity other = pair.Other;

                if (megaLookup.HasComponent(ship) && megaLookup[ship].IsMega)
                    continue;
                if (culledLookup.HasComponent(other))
                    continue;
                if (asteroidStateLookup.HasComponent(other))
                {
                    var rock = asteroidStateLookup[other];
                    if (rock.IsDestroyed || !(rock.Health > 0.01f))
                        continue;
                }

                float3 normal = pair.NormalShipFromOther;
                normal.y = 0f;
                if (math.lengthsq(normal) < 1e-8f)
                    continue;
                normal = math.normalize(normal);

                if (contactLookup.HasComponent(ship))
                {
                    contactLookup[ship] = new ShipAsteroidContactState
                    {
                        InContact = 1,
                        OutwardNormal = normal,
                    };
                }

                if (friction <= 0f || !velocityLookup.HasComponent(ship))
                    continue;

                var vel = velocityLookup[ship];
                vel.Linear = AsteroidColliderMaterialLogic.ApplyTangentialFriction(
                    vel.Linear, normal, friction, dt);
                velocityLookup[ship] = vel;
            }
        }
    }
}
