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
    /// One ship-motion step shared by the server and the predicting client.
    /// The motor has already written velocity. This system integrates that velocity,
    /// then resolves ship-vs-ship overlap from mass and closing speed.
    /// Unity Physics does not integrate the hull: <see cref="ShipTickSolverParkSystem"/>
    /// zeroes velocity for the solver, and <see cref="ShipTickSolverRestoreSystem"/>
    /// puts it back so rocks and planets can still bounce. Ship-vs-ship pairs are
    /// not left to the solver.
    /// Map size comes from <see cref="MapStateSingleton"/> / <see cref="ToroidalMapEcs"/>.
    /// </summary>
    [UpdateInGroup(typeof(PredictedFixedStepSimulationSystemGroup), OrderFirst = true)]
    [UpdateAfter(typeof(ShipPhysicsDriveSystem))]
    [UpdateAfter(typeof(ShipClientPredictedPhysicsDriveSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    public partial struct ShipDeterministicTickSystem : ISystem
    {
        NativeList<ShipTickBody> _bodies;
        NativeList<float3> _deltaV;
        NativeList<float3> _deltaP;
        NativeList<ShipTickHoldElement> _holds;
        NativeList<ShipTickOverlapElement> _overlaps;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<ShipTag>();
            _bodies = new NativeList<ShipTickBody>(32, Allocator.Persistent);
            _deltaV = new NativeList<float3>(32, Allocator.Persistent);
            _deltaP = new NativeList<float3>(32, Allocator.Persistent);
            _holds = new NativeList<ShipTickHoldElement>(32, Allocator.Persistent);
            _overlaps = new NativeList<ShipTickOverlapElement>(32, Allocator.Persistent);

            if (!SystemAPI.TryGetSingletonEntity<ShipTickMotionTag>(out _))
            {
                Entity e = state.EntityManager.CreateEntity();
                state.EntityManager.AddComponentData(e, new ShipTickMotionTag());
                state.EntityManager.AddBuffer<ShipTickHoldElement>(e);
                state.EntityManager.AddBuffer<ShipTickOverlapElement>(e);
            }
        }

        public void OnDestroy(ref SystemState state)
        {
            if (_bodies.IsCreated)
                _bodies.Dispose();
            if (_deltaV.IsCreated)
                _deltaV.Dispose();
            if (_deltaP.IsCreated)
                _deltaP.Dispose();
            if (_holds.IsCreated)
                _holds.Dispose();
            if (_overlaps.IsCreated)
                _overlaps.Dispose();
        }

        public void OnUpdate(ref SystemState state)
        {
            _holds.Clear();
            _overlaps.Clear();
            FlushScratch(ref state);

            if (state.World.IsClient() && ClientJoinSettleCache.ShouldSkipShipSimulation)
                return;

            float dt = SystemAPI.Time.DeltaTime;
            if (dt <= 0f)
                return;

            float preferredW = 0f;
            float preferredH = 0f;
            if (SystemAPI.TryGetSingleton(out MapStateSingleton mapState) &&
                ToroidalMapEcs.IsValidMapSize(mapState.MapWidth, mapState.MapHeight))
            {
                preferredW = mapState.MapWidth;
                preferredH = mapState.MapHeight;
            }

            if (!ToroidalMapEcs.ResolveMapSize(preferredW, preferredH, out float mapW, out float mapH))
                return;

            bool isClient = state.World.IsClient();
            ShipCargoMobilitySettings mobility = ShipCargoMobilitySettingsCache.ResolveOrDefault();
            var transformLookup = SystemAPI.GetComponentLookup<LocalTransform>(false);
            var velocityLookup = SystemAPI.GetComponentLookup<PhysicsVelocity>(false);
            var kinematicsLookup = SystemAPI.GetComponentLookup<ShipKinematics>(false);
            var motorLookup = SystemAPI.GetComponentLookup<ShipMotorConfig>(true);
            var shipLookup = SystemAPI.GetComponentLookup<ShipState>(true);
            var moonLookup = SystemAPI.GetComponentLookup<ShipMoonDockState>(true);
            var ownerLookup = SystemAPI.GetComponentLookup<GhostOwner>(true);
            var radiusLookup = SystemAPI.GetComponentLookup<ShipPlanarHullRadius>(true);
            var colliderLookup = SystemAPI.GetComponentLookup<PhysicsCollider>(true);

            _bodies.Clear();
            foreach (var (transform, velocity, shipState, entity) in SystemAPI
                         .Query<RefRO<LocalTransform>, RefRO<PhysicsVelocity>, RefRO<ShipState>>()
                         .WithAll<ShipTag, Simulate>()
                         .WithNone<Prefab>()
                         .WithEntityAccess())
            {
                if (shipState.ValueRO.IsDead || shipState.ValueRO.AwaitingTeamSelection)
                    continue;
                if (moonLookup.HasComponent(entity))
                {
                    var dock = moonLookup[entity];
                    if (dock.IsFullyLanded || dock.IsTakingOff)
                        continue;
                }

                int networkId = ownerLookup.HasComponent(entity)
                    ? ownerLookup[entity].NetworkId
                    : entity.Index;

                // Client bots stay on the interpolated snapshot. Read them as the other hull.
                if (isClient && BotShipIds.IsBot(networkId))
                {
                    AddBody(
                        ref _bodies, entity, transform.ValueRO, kinematicsLookup, motorLookup,
                        shipLookup, radiusLookup, colliderLookup, networkId,
                        mobility, write: false, integrate: false, dt, mapW, mapH);
                    continue;
                }

                float3 flight = kinematicsLookup.HasComponent(entity)
                    ? kinematicsLookup[entity].Velocity
                    : velocity.ValueRO.Linear;
                AddBody(
                    ref _bodies, entity, transform.ValueRO, kinematicsLookup, motorLookup,
                    shipLookup, radiusLookup, colliderLookup, networkId,
                    mobility, write: true, integrate: true, dt, mapW, mapH,
                    integratedVelocity: flight);
            }

            if (isClient)
            {
                foreach (var (transform, shipState, entity) in SystemAPI
                             .Query<RefRO<LocalTransform>, RefRO<ShipState>>()
                             .WithAll<ShipTag>()
                             .WithNone<PhysicsVelocity, Prefab>()
                             .WithEntityAccess())
                {
                    if (shipState.ValueRO.IsDead || shipState.ValueRO.AwaitingTeamSelection)
                        continue;
                    int networkId = ownerLookup.HasComponent(entity)
                        ? ownerLookup[entity].NetworkId
                        : entity.Index;
                    AddBody(
                        ref _bodies, entity, transform.ValueRO, kinematicsLookup, motorLookup,
                        shipLookup, radiusLookup, colliderLookup, networkId,
                        mobility, write: false, integrate: false, dt, mapW, mapH);
                }
            }

            SortByNetworkId(_bodies);
            int count = _bodies.Length;
            while (_deltaV.Length < count)
            {
                _deltaV.Add(float3.zero);
                _deltaP.Add(float3.zero);
            }

            for (int i = 0; i < count; i++)
            {
                _deltaV[i] = float3.zero;
                _deltaP[i] = float3.zero;
            }

            for (int i = 0; i < count; i++)
            {
                ShipTickBody a = _bodies[i];
                for (int j = i + 1; j < count; j++)
                {
                    ShipTickBody b = _bodies[j];
                    if (a.Write == 0 && b.Write == 0)
                        continue;

                    float3 offset = ToroidalMapEcs.ShortestOffsetXZ(b.Position, a.Position, mapW, mapH);
                    offset.y = 0f;
                    float dist = math.length(offset);
                    float reach = a.Radius + b.Radius;
                    if (dist >= reach)
                        continue;

                    float3 normal = dist > 1e-4f ? offset / dist : new float3(1f, 0f, 0f);
                    ShipCollisionImpulseLogic.ComputeDeterministicShipShip(
                        a.Velocity, b.Velocity, normal, a.Mass, b.Mass, reach - dist,
                        out float3 dVa, out float3 dVb, out float3 dPa, out float3 dPb,
                        out float closing);

                    if (a.Write != 0)
                    {
                        _deltaV[i] += dVa;
                        _deltaP[i] += dPa;
                    }

                    if (b.Write != 0)
                    {
                        _deltaV[j] += dVb;
                        _deltaP[j] += dPb;
                    }

                    _overlaps.Add(new ShipTickOverlapElement
                    {
                        Ship = a.Entity,
                        Other = b.Entity,
                        NormalShipFromOther = normal,
                        ClosingSpeed = closing,
                    });
                }
            }

            for (int i = 0; i < count; i++)
            {
                ShipTickBody body = _bodies[i];
                if (body.Write == 0)
                    continue;
                if (!transformLookup.HasComponent(body.Entity) || !velocityLookup.HasComponent(body.Entity))
                    continue;
                if (!kinematicsLookup.HasComponent(body.Entity))
                    continue;

                float3 dp = _deltaP[i];
                dp.y = 0f;
                float dpLen = math.length(dp);
                if (dpLen > ShipCollisionImpulseLogic.ShipShipTickMaxSeparation)
                    dp *= ShipCollisionImpulseLogic.ShipShipTickMaxSeparation / dpLen;

                float3 pos = body.Position + dp;
                pos.y = 0f;
                pos = ToroidalMapEcs.Wrap(pos, mapW, mapH);

                float3 vel = body.Velocity + _deltaV[i];
                vel.y = 0f;

                var lt = transformLookup[body.Entity];
                lt.Position = pos;
                transformLookup[body.Entity] = lt;

                var kin = kinematicsLookup[body.Entity];
                kin.Velocity = vel;
                kinematicsLookup[body.Entity] = kin;

                var pv = velocityLookup[body.Entity];
                pv.Linear = float3.zero;
                pv.Angular = float3.zero;
                velocityLookup[body.Entity] = pv;

                _holds.Add(new ShipTickHoldElement
                {
                    Ship = body.Entity,
                    Linear = vel,
                });
            }

            // Solver body stays at rest for every simulated ship, including docked hulls
            // the integrate loop skipped. Flight speed is already on ShipKinematics.
            foreach (var velocity in SystemAPI.Query<RefRW<PhysicsVelocity>>().WithAll<ShipTag, Simulate>())
                velocity.ValueRW = PhysicsVelocity.Zero;

            FlushScratch(ref state);
        }

        void FlushScratch(ref SystemState state)
        {
            if (SystemAPI.TryGetSingletonBuffer<ShipTickHoldElement>(out var holds))
            {
                holds.Clear();
                for (int i = 0; i < _holds.Length; i++)
                    holds.Add(_holds[i]);
            }

            if (SystemAPI.TryGetSingletonBuffer<ShipTickOverlapElement>(out var overlaps))
            {
                overlaps.Clear();
                for (int i = 0; i < _overlaps.Length; i++)
                    overlaps.Add(_overlaps[i]);
            }
        }

        static void AddBody(
            ref NativeList<ShipTickBody> bodies,
            Entity entity,
            in LocalTransform transform,
            ComponentLookup<ShipKinematics> kinematics,
            ComponentLookup<ShipMotorConfig> motors,
            ComponentLookup<ShipState> ships,
            ComponentLookup<ShipPlanarHullRadius> radii,
            ComponentLookup<PhysicsCollider> colliders,
            int networkId,
            ShipCargoMobilitySettings mobility,
            bool write,
            bool integrate,
            float dt,
            float mapW,
            float mapH,
            float3 integratedVelocity = default)
        {
            float3 velocity = integratedVelocity;
            velocity.y = 0f;
            if (!integrate)
            {
                velocity = float3.zero;
                if (kinematics.HasComponent(entity))
                {
                    velocity = kinematics[entity].Velocity;
                    velocity.y = 0f;
                }
            }

            float3 position = transform.Position;
            if (integrate)
            {
                position += velocity * dt;
                position.y = 0f;
                position = ToroidalMapEcs.Wrap(position, mapW, mapH);
            }

            bodies.Add(new ShipTickBody
            {
                Entity = entity,
                Position = position,
                Velocity = velocity,
                Mass = RammingMass(entity, motors, ships, mobility),
                Radius = HullRadius(entity, transform.Scale, radii, colliders),
                NetworkId = networkId,
                Write = write ? (byte)1 : (byte)0,
            });
        }

        static float RammingMass(
            Entity entity,
            ComponentLookup<ShipMotorConfig> motors,
            ComponentLookup<ShipState> ships,
            ShipCargoMobilitySettings mobility)
        {
            if (!motors.HasComponent(entity) || !ships.HasComponent(entity))
                return ShipCollisionImpulseLogic.MinCollisionMass;

            ShipMotorConfig motor = motors[entity];
            ShipState ship = ships[entity];
            float baseMass = motor.Mass > 0f ? motor.Mass : ShipMassLogic.DefaultBaseMass;
            return ShipMassLogic.ComputeRammingMass(
                motor.HullMassReference,
                ship.MaxHealth,
                motor.ChassisReferenceHealth,
                ship.CurrentGems,
                baseMass,
                ship.CurrentPeople,
                mobility.massPerGem,
                mobility.massPerPerson);
        }

        static float HullRadius(
            Entity entity,
            float scale,
            ComponentLookup<ShipPlanarHullRadius> radii,
            ComponentLookup<PhysicsCollider> colliders)
        {
            if (radii.HasComponent(entity) && radii[entity].Unscaled >= 0.05f)
                return radii[entity].Unscaled * math.max(0.01f, scale);
            if (colliders.HasComponent(entity))
                return ShipToroidalWorldCollisionLogic.GetShipCollisionRadiusWorld(colliders[entity], scale);
            return math.max(0.5f, scale);
        }

        static void SortByNetworkId(NativeList<ShipTickBody> bodies)
        {
            for (int i = 1; i < bodies.Length; i++)
            {
                ShipTickBody key = bodies[i];
                int j = i - 1;
                while (j >= 0 && Compare(bodies[j], key) > 0)
                {
                    bodies[j + 1] = bodies[j];
                    j--;
                }

                bodies[j + 1] = key;
            }
        }

        static int Compare(in ShipTickBody a, in ShipTickBody b)
        {
            if (a.NetworkId != b.NetworkId)
                return a.NetworkId < b.NetworkId ? -1 : 1;
            if (a.Entity.Index != b.Entity.Index)
                return a.Entity.Index < b.Entity.Index ? -1 : 1;
            return 0;
        }

        struct ShipTickBody
        {
            public Entity Entity;
            public float3 Position;
            public float3 Velocity;
            public float Mass;
            public float Radius;
            public int NetworkId;
            public byte Write;
        }
    }

    /// <summary>Singleton for the per-tick hold and ship-ship overlap buffers.</summary>
    public struct ShipTickMotionTag : IComponentData
    {
    }

    /// <summary>
    /// Velocity the tick wrote. The solver step sees zero so it does not integrate
    /// the hull a second time. Restored before world bounce.
    /// </summary>
    public struct ShipTickHoldElement : IBufferElementData
    {
        public Entity Ship;
        public float3 Linear;
    }

    /// <summary>
    /// One analytic ship-vs-ship overlap this tick. The server copies these into the
    /// physics contact queue so ram damage still sees the hit after ship pairs leave the solver.
    /// </summary>
    public struct ShipTickOverlapElement : IBufferElementData
    {
        public Entity Ship;
        public Entity Other;
        public float3 NormalShipFromOther;
        public float ClosingSpeed;
    }

    /// <summary>
    /// After the pre-collision snapshot, zeroes parked ship velocity so Unity Physics
    /// does not move the hull. World contacts still generate events.
    /// </summary>
    [UpdateInGroup(typeof(PhysicsSystemGroup))]
    [UpdateAfter(typeof(ShipPreCollisionVelocitySnapshotSystem))]
    [UpdateBefore(typeof(PhysicsSimulationGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    public partial struct ShipTickSolverParkSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<ShipTickMotionTag>();
        }

        public void OnUpdate(ref SystemState state)
        {
            // Velocity stays on ShipKinematics. Zeroing PhysicsVelocity here used to be
            // exported back by Unity Physics and the motor started every tick from zero.
        }
    }

    /// <summary>
    /// Puts the tick velocity back before asteroid, planet, and moon bounce read it.
    /// </summary>
    [UpdateInGroup(typeof(AfterPhysicsSystemGroup), OrderFirst = true)]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    public partial struct ShipTickSolverRestoreSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<ShipTickMotionTag>();
        }

        public void OnUpdate(ref SystemState state)
        {
            // Do not copy the hold back onto PhysicsVelocity. ExportPhysicsWorld would
            // either wipe it or leave it for the solver to integrate a second time.
        }
    }

    /// <summary>
    /// Copies this tick's analytic ship overlaps into the classified contact buffer
    /// so server ram still runs after ship-ship pairs are removed from the solver.
    /// </summary>
    [UpdateInGroup(typeof(AfterPhysicsSystemGroup))]
    [UpdateAfter(typeof(ShipPhysicsContactCollectSystem))]
    [UpdateBefore(typeof(ShipRamPhysicsContactCollectSystem))]
    [UpdateBefore(typeof(ShipCollisionBounceSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    public partial struct ShipTickOverlapPublishSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<ShipTickMotionTag>();
            state.RequireForUpdate<ShipPhysicsContactQueueTag>();
        }

        public void OnUpdate(ref SystemState state)
        {
            if (!SystemAPI.TryGetSingletonBuffer<ShipTickOverlapElement>(out var overlaps))
                return;
            if (!SystemAPI.TryGetSingletonBuffer<ShipPhysicsContactElement>(out var pairs))
                return;

            for (int i = 0; i < overlaps.Length; i++)
            {
                ShipTickOverlapElement overlap = overlaps[i];
                pairs.Add(new ShipPhysicsContactElement
                {
                    Ship = overlap.Ship,
                    Other = overlap.Other,
                    NormalShipFromOther = overlap.NormalShipFromOther,
                    ClosingSpeed = overlap.ClosingSpeed,
                    Kind = ShipPhysicsContactKind.Ship,
                });
            }
        }
    }
}
