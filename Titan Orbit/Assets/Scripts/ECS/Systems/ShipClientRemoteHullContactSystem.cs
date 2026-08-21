using TitanOrbit.Data;
using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Physics;
using Unity.Physics.Systems;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Two hull spheres cannot occupy the same space. Unity Physics does not pair ships
    /// (interpolated remotes are not predicted bodies — PhysX lets them stack). This is the
    /// one ship↔ship solver: push apart, bounce velocity. Server writes both hulls and
    /// queues ram. Client writes the predicted ship only, with a full depenetrate against
    /// the remote you see.
    /// </summary>
    [UpdateInGroup(typeof(AfterPhysicsSystemGroup))]
    [UpdateAfter(typeof(ShipCollisionBounceSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    public partial struct ShipShipHullContactSystem : ISystem
    {
        struct Hull
        {
            public Entity Entity;
            public float3 Position;
            public float3 Velocity;
            public float Radius;
            public float Mass;
            public byte Simulated;
        }

        /// <summary>Need ships; skip work during join quarantine in OnUpdate.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<ShipTag>();
        }

        /// <summary>Gather living ships, resolve overlaps, write pose / velocity / ram.</summary>
        public void OnUpdate(ref SystemState state)
        {
            if (state.World.IsClient() && ClientJoinSettleCache.ShouldSkipShipEntityQueries)
                return;

            if (!ToroidalMapEcs.TryGetMapSize(out float mapW, out float mapH))
            {
                mapW = 1f;
                mapH = 1f;
            }

            var simulateLookup = SystemAPI.GetComponentLookup<Simulate>(true);
            var kinematicsLookup = SystemAPI.GetComponentLookup<ShipKinematics>(true);
            var velocityLookup = SystemAPI.GetComponentLookup<PhysicsVelocity>(false);
            var transformLookup = SystemAPI.GetComponentLookup<LocalTransform>(false);
            var colliderLookup = SystemAPI.GetComponentLookup<PhysicsCollider>(true);

            var hulls = new NativeList<Hull>(16, Allocator.Temp);
            foreach (var (transform, shipState, motor, mega, shipEntity) in SystemAPI
                         .Query<RefRO<LocalTransform>, RefRO<ShipState>, RefRO<ShipMotorConfig>,
                             RefRO<MegaShipState>>()
                         .WithAll<ShipTag>()
                         .WithEntityAccess())
            {
                if (shipState.ValueRO.IsDead || shipState.ValueRO.AwaitingTeamSelection)
                    continue;

                bool simulated = simulateLookup.HasComponent(shipEntity)
                                 && simulateLookup.IsComponentEnabled(shipEntity);
                float3 lin = velocityLookup.HasComponent(shipEntity)
                    ? velocityLookup[shipEntity].Linear
                    : float3.zero;
                if (!simulated && kinematicsLookup.HasComponent(shipEntity))
                    lin = kinematicsLookup[shipEntity].Velocity;

                float radius = colliderLookup.HasComponent(shipEntity)
                    ? ShipToroidalWorldCollisionLogic.GetShipCollisionRadiusWorld(
                        colliderLookup[shipEntity], transform.ValueRO.Scale)
                    : BodyCollisionMath.GetShipHullRadiusWorld(transform.ValueRO.Scale);

                hulls.Add(new Hull
                {
                    Entity = shipEntity,
                    Position = transform.ValueRO.Position,
                    Velocity = lin,
                    Radius = radius,
                    Mass = CollisionMass(motor.ValueRO, shipState.ValueRO, mega.ValueRO),
                    Simulated = simulated ? (byte)1 : (byte)0,
                });
            }

            if (hulls.Length < 2)
            {
                hulls.Dispose();
                return;
            }

            bool server = state.World.IsServer();
            DynamicBuffer<PendingRamContactElement> ramQueue = default;
            bool writeRam = server && SystemAPI.TryGetSingletonBuffer(out ramQueue);

            if (server)
            {
                for (int i = 0; i < hulls.Length; i++)
                {
                    for (int j = i + 1; j < hulls.Length; j++)
                    {
                        Hull a = hulls[i];
                        Hull b = hulls[j];
                        if (!ShipToroidalWorldCollisionLogic.TryResolveShipVsShip(
                                ref a.Position, ref a.Velocity, a.Radius, a.Mass,
                                ref b.Position, ref b.Velocity, b.Radius, b.Mass,
                                mapW, mapH,
                                ShipCollisionImpulseLogic.DefaultShipShipRestitution,
                                writePositionB: true,
                                out float3 normalAFromB,
                                out float closing))
                            continue;

                        hulls[i] = a;
                        hulls[j] = b;
                        if (!writeRam)
                            continue;
                        ramQueue.Add(new PendingRamContactElement
                        {
                            Ship = a.Entity,
                            Other = b.Entity,
                            OtherIsShip = 1,
                            NormalShipFromOther = normalAFromB,
                            ClosingSpeed = closing,
                            EstimatedImpulse = closing * 10f,
                        });
                    }
                }

                for (int i = 0; i < hulls.Length; i++)
                    WriteHull(hulls[i], transformLookup, velocityLookup);
            }
            else
            {
                for (int i = 0; i < hulls.Length; i++)
                {
                    if (hulls[i].Simulated == 0)
                        continue;

                    Hull local = hulls[i];
                    bool hit = false;
                    for (int j = 0; j < hulls.Length; j++)
                    {
                        if (j == i || hulls[j].Simulated != 0)
                            continue;

                        Hull remote = hulls[j];
                        if (!ShipToroidalWorldCollisionLogic.TryResolveShipVsShip(
                                ref local.Position, ref local.Velocity, local.Radius, local.Mass,
                                ref remote.Position, ref remote.Velocity, remote.Radius, remote.Mass,
                                mapW, mapH,
                                ShipCollisionImpulseLogic.DefaultShipShipRestitution,
                                writePositionB: false,
                                out _,
                                out _))
                            continue;
                        hit = true;
                    }

                    if (hit)
                        WriteHull(local, transformLookup, velocityLookup);
                }
            }

            hulls.Dispose();
        }

        static float CollisionMass(in ShipMotorConfig motor, in ShipState ship, in MegaShipState mega)
        {
            float baseMass = motor.Mass > 0f ? motor.Mass : ShipMassLogic.DefaultBaseMass;
            float mass = ShipMassLogic.ComputeRammingMass(
                motor.HullMassReference,
                ship.MaxHealth,
                motor.ChassisReferenceHealth,
                ship.CurrentGems,
                baseMass,
                ship.CurrentPeople);
            if (mega.IsMega)
                mass = math.max(mass, MegaShipCatalog.MinHullCollisionMass);
            return mass;
        }

        static void WriteHull(
            in Hull hull,
            ComponentLookup<LocalTransform> transforms,
            ComponentLookup<PhysicsVelocity> velocities)
        {
            if (transforms.HasComponent(hull.Entity))
            {
                var lt = transforms[hull.Entity];
                lt.Position = hull.Position;
                transforms[hull.Entity] = lt;
            }

            if (velocities.HasComponent(hull.Entity))
            {
                var pv = velocities[hull.Entity];
                pv.Linear = hull.Velocity;
                velocities[hull.Entity] = pv;
            }
        }
    }
}
