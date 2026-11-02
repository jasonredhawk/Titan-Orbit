using TitanOrbit;
using TitanOrbit.Data;
using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Physics;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Adds <see cref="ShipRemoteMotorState"/> before prediction so the motor never
    /// takes a structural change inside the rollback loop.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(PredictedSimulationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    public partial struct ShipRemoteMotorEnsureSystem : ISystem
    {
        /// <summary>Require a flight sample before looking for hulls.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<ShipRemoteFlightSample>();
        }

        /// <summary>One client component per remote hull that does not have one yet.</summary>
        public void OnUpdate(ref SystemState state)
        {
            if (ClientJoinSettleCache.ShouldSkipShipSimulation)
                return;

            var em = state.EntityManager;
            var missing = new NativeList<Entity>(32, Allocator.Temp);
            foreach (var (_, entity) in SystemAPI
                         .Query<RefRO<ShipRemoteFlightSample>>()
                         .WithAll<ShipTag>()
                         .WithNone<ShipRemoteMotorState, Prefab>()
                         .WithEntityAccess())
            {
                if (em.HasComponent<LocalPlayerShipTag>(entity))
                    continue;
                if (em.HasComponent<GhostOwnerIsLocal>(entity) &&
                    em.IsComponentEnabled<GhostOwnerIsLocal>(entity))
                    continue;
                missing.Add(entity);
            }

            for (int i = 0; i < missing.Length; i++)
            {
                em.AddComponentData(missing[i], new ShipRemoteMotorState
                {
                    Territory = new ShipTerritoryBoostLatch
                    {
                        LatchedMult = 1f,
                        HoldUntilElapsed = -1.0,
                    },
                });
            }

            missing.Dispose();
        }
    }

    /// <summary>
    /// Client-only forward integration for remote and AI hulls. Starts from the latest
    /// <see cref="ShipRemoteFlightSample"/> and steps <see cref="ShipPhysicsDriveLogic"/>
    /// toward the estimated server tick. Does not enable <see cref="Simulate"/> or
    /// <see cref="PredictedGhost"/> — those hulls stay out of prediction rollback.
    /// Runs once on the newest predicting tick, before the local drive, so a bump sees
    /// the live pose. Map size comes from <see cref="MapStateSingleton"/> /
    /// <see cref="ToroidalMapEcs"/>.
    /// </summary>
    [UpdateInGroup(typeof(PredictedFixedStepSimulationSystemGroup), OrderFirst = true)]
    [UpdateBefore(typeof(ShipClientPredictedPhysicsDriveSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    public partial struct ShipRemoteMotorSystem : ISystem
    {
        /// <summary>Do not invent more than this many 60 Hz steps past the sample (~250 ms).</summary>
        public const int MaxReplayTicks = 15;

        /// <summary>How fast the displayed hull closes a wrong guess (1/seconds).</summary>
        public const float CorrectSharpness = 18f;

        /// <summary>Gaps larger than this snap. Matches the old remote presentation snap.</summary>
        public const float SnapDistance = 24f;

        /// <summary>Require a flight sample before scheduling.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<ShipRemoteFlightSample>();
        }

        /// <summary>
        /// Steps each remote at most <see cref="MaxReplayTicks"/> from its sample, then
        /// writes the bled pose onto <see cref="LocalTransform"/> and the motor velocity
        /// onto <see cref="ShipKinematics"/>.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            if (ClientJoinSettleCache.ShouldSkipShipSimulation)
                return;

            // Resim ticks must not advance remotes once per rollback step.
            if (SystemAPI.TryGetSingleton(out NetworkTime networkTime))
            {
                if (!networkTime.IsFirstTimeFullyPredictingTick || !networkTime.ServerTick.IsValid)
                    return;
            }
            else
            {
                return;
            }

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
            if (ToroidalMapEcs.IsValidMapSize(preferredW, preferredH))
                ToroidalMapEcs.SetMapSize(mapW, mapH);

            float dt = SystemAPI.Time.DeltaTime;
            if (dt <= 0f)
                dt = 1f / PlanetGemMoonOrbitClock.FallbackSimulationHz;

            int hz = 0;
            if (SystemAPI.TryGetSingleton<ClientServerTickRate>(out var tickRate))
                hz = tickRate.SimulationTickRate;
            double elapsed = PlanetGemMoonOrbitClock.GetElapsedSeconds(networkTime, hz, includeTickFraction: false);

            float now = UnityEngine.Time.unscaledTime;

            var em = state.EntityManager;

            // Same gather as owner prediction. Full ToEntityArray Crash!!! under quarantine.
            var planets = ClientJoinSettleCache.TransformQuarantine || ClientJoinSettleCache.Settling
                ? PlanetMotorSnapshotCollection.CollectFromClientRegistry(ref state, Allocator.Temp)
                : PlanetMotorSnapshotCollection.Collect(ref state, Allocator.Temp);
            var territory = PlanetConnectionGraphCache.GetRuntimeTrianglesNative(
                PlanetConnectionGraphSide.Client,
                planets.AsArray(),
                elapsed);
            var homeLevels = new NativeArray<int>(6, Allocator.Temp);
            PlanetConnectionGraphCache.CopyHomeLevels(PlanetConnectionGraphSide.Client, ref homeLevels);
            ShipCargoMobilitySettings mobility = ShipCargoMobilitySettingsCache.ResolveOrDefault();
            bool pointGuns = TitanOrbitDebugFlags.RegularShipGunsPoint;
            var colliders = SystemAPI.GetComponentLookup<PhysicsCollider>(true);

            try
            {
            foreach (var (sample, transform, motorState, shipState, entity) in SystemAPI
                         .Query<RefRO<ShipRemoteFlightSample>, RefRW<LocalTransform>, RefRW<ShipRemoteMotorState>, RefRO<ShipState>>()
                         .WithAll<ShipTag>()
                         .WithNone<Prefab>()
                         .WithEntityAccess())
            {
                if (IsLocalOwner(em, entity))
                    continue;
                if (!sample.ValueRO.HasSample)
                    continue;

                var motor = motorState.ValueRO;
                var moon = em.HasComponent<ShipMoonDockState>(entity)
                    ? em.GetComponentData<ShipMoonDockState>(entity)
                    : default;
                var orbit = em.HasComponent<ShipOrbitState>(entity)
                    ? em.GetComponentData<ShipOrbitState>(entity)
                    : default;
                var shock = em.HasComponent<ShipElectricShockState>(entity)
                    ? em.GetComponentData<ShipElectricShockState>(entity)
                    : default;
                var turret = em.HasComponent<ShipTurretControlState>(entity)
                    ? em.GetComponentData<ShipTurretControlState>(entity)
                    : default;
                var motorConfig = em.HasComponent<ShipMotorConfig>(entity)
                    ? em.GetComponentData<ShipMotorConfig>(entity)
                    : default;
                bool isMega = em.HasComponent<MegaShipState>(entity) && em.GetComponentData<MegaShipState>(entity).IsMega;
                float scale = transform.ValueRO.Scale;
                float radius = colliders.HasComponent(entity)
                    ? ShipPhysicsDriveLogic.MeasurePhysicsHullRadiusXZ(colliders[entity], transform.ValueRO)
                    : -1f;

                int gap = networkTime.ServerTick.TicksSince(new NetworkTick(sample.ValueRO.Tick));
                bool hardSnap = motor.Valid == 0 || shipState.ValueRO.IsDead || shipState.ValueRO.AwaitingTeamSelection;

                if (gap < 0 || gap > MaxReplayTicks)
                {
                    // Sample is too far ahead or too stale to replay. Sit on it.
                    if (motor.Valid == 0 || motor.SampleTick != sample.ValueRO.Tick)
                    {
                        motor.SampleTick = sample.ValueRO.Tick;
                        motor.Position = sample.ValueRO.Position;
                        motor.Rotation = sample.ValueRO.Rotation;
                        motor.Velocity = sample.ValueRO.Velocity;
                        motor.IntegratedTick = sample.ValueRO.Tick;
                        motor.Ship = shipState.ValueRO;
                        motor.MoonDock = moon;
                        motor.Orbit = orbit;
                    }

                    motor.Holding = 1;
                    motor.IntegratedUnscaledTime = now;
                    PublishDisplay(ref motor, motor.Position, motor.Rotation, dt, mapW, mapH, hardSnap);
                    motor.Valid = 1;
                    var held = transform.ValueRO;
                    ApplyPose(ref held, ref motor, em, entity);
                    transform.ValueRW = held;
                    motorState.ValueRW = motor;
                    continue;
                }

                bool newSample = motor.Valid == 0 || motor.SampleTick != sample.ValueRO.Tick;
                int steps;
                if (newSample)
                {
                    motor.SampleTick = sample.ValueRO.Tick;
                    motor.Position = sample.ValueRO.Position;
                    motor.Rotation = sample.ValueRO.Rotation;
                    motor.Velocity = sample.ValueRO.Velocity;
                    motor.Ship = shipState.ValueRO;
                    motor.MoonDock = moon;
                    motor.Orbit = orbit;
                    motor.IntegratedTick = sample.ValueRO.Tick;
                    steps = gap;
                }
                else
                {
                    steps = networkTime.ServerTick.TicksSince(new NetworkTick(motor.IntegratedTick));
                    if (steps < 0)
                        steps = 0;
                    if (steps > MaxReplayTicks)
                        steps = MaxReplayTicks;
                }

                var input = sample.ValueRO.ToFlightInput();
                for (int s = 0; s < steps; s++)
                {
                    Advance(
                        ref motor, in input, in motorConfig, in shock, in turret,
                        planets.AsArray(), territory, homeLevels, mobility,
                        dt, mapW, mapH, elapsed, scale, radius, isMega, pointGuns);
                }

                if (steps > 0)
                {
                    var integrated = new NetworkTick(motor.IntegratedTick);
                    integrated.Add((uint)steps);
                    motor.IntegratedTick = integrated.TickIndexForValidTick;
                }

                motor.Holding = 0;
                motor.IntegratedUnscaledTime = now;
                PublishDisplay(ref motor, motor.Position, motor.Rotation, dt, mapW, mapH, hardSnap);
                motor.Valid = 1;
                var posed = transform.ValueRO;
                ApplyPose(ref posed, ref motor, em, entity);
                transform.ValueRW = posed;
                motorState.ValueRW = motor;
            }
            }
            finally
            {
                planets.Dispose();
                homeLevels.Dispose();
            }
        }

        /// <summary>True for the connection-owned hull. That ship is already predicted.</summary>
        static bool IsLocalOwner(EntityManager em, Entity entity)
        {
            if (em.HasComponent<LocalPlayerShipTag>(entity))
                return true;
            return em.HasComponent<GhostOwnerIsLocal>(entity)
                   && em.IsComponentEnabled<GhostOwnerIsLocal>(entity);
        }

        /// <summary>One shared motor step plus planar integration. Moon dock pins its own position.</summary>
        static void Advance(
            ref ShipRemoteMotorState motor,
            in ShipInput input,
            in ShipMotorConfig motorConfig,
            in ShipElectricShockState shock,
            in ShipTurretControlState turret,
            in NativeArray<PlanetMotorSnapshot> planets,
            in NativeArray<PlanetConnectionGraphLogic.RuntimeTriangle> territory,
            in NativeArray<int> homeLevels,
            ShipCargoMobilitySettings mobility,
            float dt,
            float mapW,
            float mapH,
            double elapsed,
            float scale,
            float radius,
            bool isMega,
            bool pointGuns)
        {
            if (turret.IsControlling || shock.IsActive(elapsed))
            {
                motor.Velocity = float3.zero;
                motor.Ship.OverdriveBurstActive = false;
                return;
            }

            var transform = LocalTransform.FromPositionRotationScale(motor.Position, motor.Rotation, math.max(0.01f, scale));
            var velocity = new PhysicsVelocity { Linear = motor.Velocity };
            velocity.Linear.y = 0f;
            var damping = default(PhysicsDamping);
            var asteroid = default(ShipAsteroidContactState);
            var moon = motor.MoonDock;
            var orbit = motor.Orbit;
            var territoryLatch = motor.Territory;
            var ship = motor.Ship;

            ShipPhysicsDriveLogic.Step(
                input,
                motorConfig,
                ref moon,
                ref ship,
                ref velocity,
                ref damping,
                ref transform,
                ref orbit,
                ref territoryLatch,
                asteroid,
                in planets,
                dt,
                mapW,
                mapH,
                elapsed,
                in territory,
                in homeLevels,
                mobility.massPerGem,
                mobility.massPerPerson,
                mobility.massPerComponentSize,
                mobility.speedWeightPerMass,
                mobility.accelWeightPerMass,
                mobility.turnWeightPerMass,
                mobility.minSpeed,
                mobility.minAccel,
                mobility.minTurn,
                skipMassTax: motorConfig.SkipMassTax != 0,
                isMegaShip: isMega,
                shipPhysicsRadius: radius,
                pointGuns: pointGuns);

            float3 linear = velocity.Linear;
            linear.y = 0f;
            motor.Velocity = linear;
            motor.Rotation = transform.Rotation;
            motor.MoonDock = moon;
            motor.Orbit = orbit;
            motor.Territory = territoryLatch;
            motor.Ship = ship;

            float3 pos = transform.Position;
            if (!moon.IsFullyLanded && !moon.IsTakingOff && !ship.IsDead && !ship.AwaitingTeamSelection)
                pos += linear * dt;
            pos.y = 0f;
            motor.Position = ToroidalMapEcs.Wrap(pos, mapW, mapH);
        }

        /// <summary>
        /// Pulls the visible hull toward the integrated pose. Wrap, death, and a 24 unit
        /// gap snap. <paramref name="hardSnap"/> covers the first sample and a dead hull.
        /// </summary>
        static void PublishDisplay(
            ref ShipRemoteMotorState motor,
            float3 targetPos,
            quaternion targetRot,
            float dt,
            float mapW,
            float mapH,
            bool hardSnap)
        {
            if (motor.Valid == 0 || hardSnap)
            {
                motor.DisplayPosition = targetPos;
                motor.DisplayRotation = targetRot;
                return;
            }

            float3 gap = ToroidalMapEcs.ShortestOffsetXZ(motor.DisplayPosition, targetPos, mapW, mapH);
            gap.y = targetPos.y - motor.DisplayPosition.y;
            float gapLen = math.length(gap);
            bool wrap = ToroidalMapEcs.IsWrapJump(motor.DisplayPosition, targetPos, mapW, mapH);
            if (wrap || gapLen > SnapDistance)
            {
                motor.DisplayPosition = targetPos;
                motor.DisplayRotation = targetRot;
                return;
            }

            float blend = 1f - math.exp(-CorrectSharpness * math.max(0f, dt));
            float3 pos = motor.DisplayPosition + gap * blend;
            pos = ToroidalMapEcs.Wrap(pos, mapW, mapH);
            pos.y = targetPos.y;
            motor.DisplayPosition = pos;
            motor.DisplayRotation = math.slerp(motor.DisplayRotation, targetRot, math.saturate(blend));
        }

        /// <summary>Writes the bled pose and the integrated velocity. Does not add PhysicsVelocity.</summary>
        static void ApplyPose(ref LocalTransform transform, ref ShipRemoteMotorState motor, EntityManager em, Entity entity)
        {
            transform.Position = motor.DisplayPosition;
            transform.Rotation = motor.DisplayRotation;

            if (!em.HasComponent<ShipKinematics>(entity))
                return;
            var kin = em.GetComponentData<ShipKinematics>(entity);
            kin.Velocity = motor.Velocity;
            em.SetComponentData(entity, kin);
        }
    }

    /// <summary>
    /// Client-only integrated remote pose. Not ghosted. Presentation reads the display
    /// fields; the next motor step continues from <see cref="Position"/> and
    /// <see cref="Velocity"/>, not from the bled display.
    /// </summary>
    public struct ShipRemoteMotorState : IComponentData
    {
        /// <summary>Sample tick this state was last reset from.</summary>
        public uint SampleTick;

        /// <summary>Server tick the integrated pose corresponds to.</summary>
        public uint IntegratedTick;

        /// <summary>Unscaled time when this pose was written. Presentation coasts until the next tick.</summary>
        public float IntegratedUnscaledTime;

        /// <summary>Integrated planar position (motor truth).</summary>
        public float3 Position;

        /// <summary>Integrated rotation.</summary>
        public quaternion Rotation;

        /// <summary>Integrated planar velocity.</summary>
        public float3 Velocity;

        /// <summary>Bled pose written to the ghost transform.</summary>
        public float3 DisplayPosition;

        /// <summary>Bled rotation written to the ghost transform.</summary>
        public quaternion DisplayRotation;

        /// <summary>Local copy so a replay can drain energy without writing the ghost.</summary>
        public ShipState Ship;

        /// <summary>Local moon-dock progress across steps. The ghost copy stays the snapshot.</summary>
        public ShipMoonDockState MoonDock;

        /// <summary>Local orbit flags across steps.</summary>
        public ShipOrbitState Orbit;

        /// <summary>Territory latch rebuilt by the motor. Not ghosted.</summary>
        public ShipTerritoryBoostLatch Territory;

        /// <summary>1 while the newest sample is outside the replay window.</summary>
        public byte Holding;

        /// <summary>1 after the first sample has been applied.</summary>
        public byte Valid;
    }
}
