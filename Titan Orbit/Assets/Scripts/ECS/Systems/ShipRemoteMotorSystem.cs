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
                SeedHistory(em, missing[i]);
            }

            // Hulls that already have a motor still need the ring after a script reload.
            missing.Clear();
            foreach (var (_, entity) in SystemAPI
                         .Query<RefRO<ShipRemoteFlightSample>>()
                         .WithAll<ShipTag, ShipRemoteMotorState>()
                         .WithNone<ShipRemoteMotorHistoryElement, Prefab>()
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
                SeedHistory(em, missing[i]);

            missing.Dispose();
        }

        /// <summary>Empty 16-tick ring. Kept inline so a snapshot correction does not allocate.</summary>
        static void SeedHistory(EntityManager em, Entity entity)
        {
            var history = em.AddBuffer<ShipRemoteMotorHistoryElement>(entity);
            history.ResizeUninitialized(ShipRemoteMotorHistoryElement.Capacity);
            for (int i = 0; i < history.Length; i++)
                history[i] = default;
        }
    }

    /// <summary>
    /// Client-only forward integration for remote and AI hulls. Steps
    /// <see cref="ShipPhysicsDriveLogic"/> one tick at a time from the pose it already
    /// has. A new <see cref="ShipRemoteFlightSample"/> measures how far that pose was
    /// from the server at the sample tick and folds the error in. It does not rewind
    /// to the sample and replay the whole gap: that replay used only the newest input,
    /// so a late snapshot (common once many hulls share a send chunk) yanked every
    /// ship in the tile on the same frame. Does not enable <see cref="Simulate"/> or
    /// <see cref="PredictedGhost"/>. Runs once on the newest predicting tick, before
    /// the local drive. Map size comes from <see cref="MapStateSingleton"/> /
    /// <see cref="ToroidalMapEcs"/>.
    /// </summary>
    [UpdateInGroup(typeof(PredictedFixedStepSimulationSystemGroup), OrderFirst = true)]
    [UpdateBefore(typeof(ShipClientPredictedPhysicsDriveSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    public partial struct ShipRemoteMotorSystem : ISystem
    {
        /// <summary>Do not invent more than this many 60 Hz steps past a brand-new sample (~250 ms).</summary>
        public const int MaxReplayTicks = 15;

        /// <summary>
        /// Ticks a live hull may catch up in one call. This system already runs once per
        /// predicting tick, so steady state is 1. Two absorbs a skipped tick without
        /// replaying a stale gap for every ship in the tile.
        /// </summary>
        public const int MaxCatchUpSteps = 2;

        /// <summary>How fast the displayed hull closes a wrong guess (1/seconds).</summary>
        public const float CorrectSharpness = 18f;

        /// <summary>
        /// Cap on that blend, in world units per second. The exponential spends about a
        /// quarter of a large error in one tick; a crowd correction then reads as a hitch.
        /// </summary>
        public const float MaxDisplayCorrectSpeed = 36f;

        /// <summary>Gaps larger than this snap. Matches the old remote presentation snap.</summary>
        public const float SnapDistance = 24f;

        /// <summary>Require a flight sample before scheduling.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<ShipRemoteFlightSample>();
        }

        /// <summary>
        /// Steps each live remote at most <see cref="MaxCatchUpSteps"/> from its own
        /// integrated tick, then writes the bled pose onto <see cref="LocalTransform"/>
        /// and the motor velocity onto <see cref="ShipKinematics"/>.
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
            var moons = SystemAPI.GetComponentLookup<ShipMoonDockState>(true);
            var orbits = SystemAPI.GetComponentLookup<ShipOrbitState>(true);
            var shocks = SystemAPI.GetComponentLookup<ShipElectricShockState>(true);
            var turrets = SystemAPI.GetComponentLookup<ShipTurretControlState>(true);
            var motorConfigs = SystemAPI.GetComponentLookup<ShipMotorConfig>(true);
            var megas = SystemAPI.GetComponentLookup<MegaShipState>(true);
            var localPlayers = SystemAPI.GetComponentLookup<LocalPlayerShipTag>(true);
            var localOwners = SystemAPI.GetComponentLookup<GhostOwnerIsLocal>(true);
            var kinematics = SystemAPI.GetComponentLookup<ShipKinematics>(false);

            try
            {
            foreach (var (sample, transform, motorState, shipState, history, entity) in SystemAPI
                         .Query<RefRO<ShipRemoteFlightSample>, RefRW<LocalTransform>, RefRW<ShipRemoteMotorState>, RefRO<ShipState>, DynamicBuffer<ShipRemoteMotorHistoryElement>>()
                         .WithAll<ShipTag>()
                         .WithNone<Prefab>()
                         .WithEntityAccess())
            {
                if (IsLocalOwner(localPlayers, localOwners, entity))
                    continue;
                if (!sample.ValueRO.HasSample)
                    continue;

                var flight = sample.ValueRO;
                var motor = motorState.ValueRO;
                var moon = moons.HasComponent(entity) ? moons[entity] : default;
                var orbit = orbits.HasComponent(entity) ? orbits[entity] : default;
                var shock = shocks.HasComponent(entity) ? shocks[entity] : default;
                var turret = turrets.HasComponent(entity) ? turrets[entity] : default;
                var motorConfig = motorConfigs.HasComponent(entity) ? motorConfigs[entity] : default;
                bool isMega = megas.HasComponent(entity) && megas[entity].IsMega;
                float scale = transform.ValueRO.Scale;
                float radius = colliders.HasComponent(entity)
                    ? ShipPhysicsDriveLogic.MeasurePhysicsHullRadiusXZ(colliders[entity], transform.ValueRO)
                    : -1f;

                int gap = networkTime.ServerTick.TicksSince(new NetworkTick(flight.Tick));
                bool dead = shipState.ValueRO.IsDead || shipState.ValueRO.AwaitingTeamSelection;
                bool acquire = motor.Valid == 0;
                bool newSample = acquire || motor.SampleTick != flight.Tick;
                int steps = 0;

                if (acquire || dead)
                {
                    // First sample, or a wreck: sit on the snapshot. A live hull must
                    // not take this path or a late sample rewinds the whole tile.
                    AssignSamplePose(ref motor, in flight, shipState.ValueRO, in moon, in orbit);
                    ClearHistory(history);
                    if (acquire && !dead && gap > 0)
                        steps = math.min(gap, MaxReplayTicks);
                }
                else
                {
                    int behind = networkTime.ServerTick.TicksSince(new NetworkTick(motor.IntegratedTick));
                    if (behind < 0)
                        behind = 0;
                    steps = math.min(behind, MaxCatchUpSteps);

                    // A sample ahead of the estimated server tick is not scorable yet.
                    // Leave SampleTick alone so the correction still runs once we catch up.
                    if (newSample && gap >= 0)
                    {
                        motor.SampleTick = flight.Tick;
                        motor.Ship = shipState.ValueRO;
                        motor.MoonDock = moon;
                        motor.Orbit = orbit;

                        if (TryHistory(history, flight.Tick, out var hist))
                        {
                            // Error at the sample tick, added to today's pose. A respawn
                            // is a large error and still lands on the spawn, not on the wreck.
                            float3 histPos = new float3(hist.X, 0f, hist.Z);
                            float3 err = ToroidalMapEcs.ShortestOffsetXZ(histPos, flight.Position, mapW, mapH);
                            err.y = 0f;
                            float yawErr = DeltaYaw(hist.Yaw, flight.Yaw);
                            float3 velErr = flight.Velocity - new float3(hist.VX, 0f, hist.VZ);
                            velErr.y = 0f;
                            ApplySampleError(ref motor, history, err, yawErr, velErr, flight.Tick, mapW, mapH);
                        }
                        else
                        {
                            // No ring entry (sample older than 16 ticks). Keep flying unless
                            // the constant-velocity guess is a respawn-sized miss.
                            float3 expected = flight.Position;
                            if (gap > 0)
                                expected = ToroidalMapEcs.Wrap(flight.Position + flight.Velocity * (gap * dt), mapW, mapH);
                            float3 miss = ToroidalMapEcs.ShortestOffsetXZ(motor.Position, expected, mapW, mapH);
                            if (math.length(miss) > SnapDistance)
                            {
                                AssignSamplePose(ref motor, in flight, shipState.ValueRO, in moon, in orbit);
                                motor.Position = expected;
                                motor.IntegratedTick = networkTime.ServerTick.TickIndexForValidTick;
                                ClearHistory(history);
                                steps = 0;
                            }
                        }
                    }
                }

                // The pose at IntegratedTick has to be in the ring before we step,
                // or the next sample for this tick has nothing to score against.
                if (!dead)
                    RecordHistory(history, in motor);

                var input = flight.ToFlightInput();
                for (int s = 0; s < steps; s++)
                {
                    Advance(
                        ref motor, in input, in motorConfig, in shock, in turret,
                        planets.AsArray(), territory, homeLevels, mobility,
                        dt, mapW, mapH, elapsed, scale, radius, isMega, pointGuns);
                    var integrated = new NetworkTick(motor.IntegratedTick);
                    integrated.Add(1);
                    motor.IntegratedTick = integrated.TickIndexForValidTick;
                    RecordHistory(history, in motor);
                }

                motor.Holding = (byte)(dead ? 1 : 0);
                motor.IntegratedUnscaledTime = now;
                PublishDisplay(ref motor, motor.Position, motor.Rotation, dt, mapW, mapH, dead || acquire);
                motor.Valid = 1;
                var posed = transform.ValueRO;
                ApplyPose(ref posed, ref motor, ref kinematics, entity);
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
        static bool IsLocalOwner(
            ComponentLookup<LocalPlayerShipTag> localPlayers,
            ComponentLookup<GhostOwnerIsLocal> localOwners,
            Entity entity)
        {
            if (localPlayers.HasComponent(entity))
                return true;
            return localOwners.HasComponent(entity) && localOwners.IsComponentEnabled(entity);
        }

        /// <summary>Copies one server snapshot onto the motor. Used for acquire, death, and a 24u miss.</summary>
        static void AssignSamplePose(
            ref ShipRemoteMotorState motor,
            in ShipRemoteFlightSample sample,
            in ShipState ship,
            in ShipMoonDockState moon,
            in ShipOrbitState orbit)
        {
            motor.SampleTick = sample.Tick;
            motor.Position = sample.Position;
            motor.Rotation = sample.Rotation;
            motor.Velocity = sample.Velocity;
            motor.IntegratedTick = sample.Tick;
            motor.Ship = ship;
            motor.MoonDock = moon;
            motor.Orbit = orbit;
        }

        /// <summary>
        /// Adds the measured sample error to the live pose and to every history entry
        /// from that tick forward, so the next sample does not apply the same offset again.
        /// </summary>
        static void ApplySampleError(
            ref ShipRemoteMotorState motor,
            DynamicBuffer<ShipRemoteMotorHistoryElement> history,
            float3 error,
            float yawError,
            float3 velError,
            uint sampleTick,
            float mapW,
            float mapH)
        {
            float3 pos = motor.Position + error;
            motor.Position = ToroidalMapEcs.Wrap(pos, mapW, mapH);
            motor.Velocity += velError;
            motor.Velocity.y = 0f;
            float yaw = ShipRemoteFlightSample.YawFromRotation(motor.Rotation) + yawError;
            motor.Rotation = quaternion.RotateY(yaw);

            for (int i = 0; i < history.Length; i++)
            {
                var entry = history[i];
                if (entry.Valid == 0)
                    continue;
                int age = new NetworkTick(entry.Tick).TicksSince(new NetworkTick(sampleTick));
                if (age < 0 || age >= ShipRemoteMotorHistoryElement.Capacity)
                    continue;
                float3 shifted = ToroidalMapEcs.Wrap(new float3(entry.X, 0f, entry.Z) + error, mapW, mapH);
                entry.X = shifted.x;
                entry.Z = shifted.z;
                entry.Yaw = WrapYaw(entry.Yaw + yawError);
                entry.VX += velError.x;
                entry.VZ += velError.z;
                history[i] = entry;
            }
        }

        /// <summary>Signed shortest yaw delta, radians.</summary>
        static float DeltaYaw(float from, float to)
        {
            return WrapYaw(to - from);
        }

        /// <summary>Wraps a yaw into (-pi, pi].</summary>
        static float WrapYaw(float yaw)
        {
            const float TwoPi = math.PI * 2f;
            yaw %= TwoPi;
            if (yaw > math.PI)
                yaw -= TwoPi;
            else if (yaw <= -math.PI)
                yaw += TwoPi;
            return yaw;
        }

        /// <summary>True when this ring slot still holds <paramref name="tick"/>.</summary>
        static bool TryHistory(
            DynamicBuffer<ShipRemoteMotorHistoryElement> history,
            uint tick,
            out ShipRemoteMotorHistoryElement entry)
        {
            entry = default;
            int capacity = ShipRemoteMotorHistoryElement.Capacity;
            if (history.Length != capacity)
                return false;
            entry = history[(int)(tick % (uint)capacity)];
            return entry.Valid != 0 && entry.Tick == tick;
        }

        /// <summary>Writes the integrated pose into the slot for its tick.</summary>
        static void RecordHistory(DynamicBuffer<ShipRemoteMotorHistoryElement> history, in ShipRemoteMotorState motor)
        {
            int capacity = ShipRemoteMotorHistoryElement.Capacity;
            if (history.Length != capacity)
            {
                history.ResizeUninitialized(capacity);
                for (int i = 0; i < capacity; i++)
                    history[i] = default;
            }

            history[(int)(motor.IntegratedTick % (uint)capacity)] = new ShipRemoteMotorHistoryElement
            {
                Valid = 1,
                Tick = motor.IntegratedTick,
                X = motor.Position.x,
                Z = motor.Position.z,
                Yaw = ShipRemoteFlightSample.YawFromRotation(motor.Rotation),
                VX = motor.Velocity.x,
                VZ = motor.Velocity.z,
            };
        }

        /// <summary>Drops the ring after a snap so a stale slot cannot correct the new pose.</summary>
        static void ClearHistory(DynamicBuffer<ShipRemoteMotorHistoryElement> history)
        {
            for (int i = 0; i < history.Length; i++)
                history[i] = default;
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
            float3 delta = gap * blend;
            float deltaLen = math.length(delta);
            float maxStep = MaxDisplayCorrectSpeed * math.max(0f, dt);
            if (deltaLen > maxStep && maxStep > 0f)
                delta *= maxStep / deltaLen;
            float3 pos = motor.DisplayPosition + delta;
            pos = ToroidalMapEcs.Wrap(pos, mapW, mapH);
            pos.y = targetPos.y;
            motor.DisplayPosition = pos;
            motor.DisplayRotation = math.slerp(motor.DisplayRotation, targetRot, math.saturate(blend));
        }

        /// <summary>Writes the bled pose and the integrated velocity. Does not add PhysicsVelocity.</summary>
        static void ApplyPose(
            ref LocalTransform transform,
            ref ShipRemoteMotorState motor,
            ref ComponentLookup<ShipKinematics> kinematics,
            Entity entity)
        {
            transform.Position = motor.DisplayPosition;
            transform.Rotation = motor.DisplayRotation;

            if (!kinematics.HasComponent(entity))
                return;
            var kin = kinematics[entity];
            kin.Velocity = motor.Velocity;
            kinematics[entity] = kin;
        }
    }

    /// <summary>
    /// One predicted pose in the remote motor's ring. Client-only, not ghosted.
    /// Slot is <c>Tick % Capacity</c>. <see cref="Valid"/> is 0 until that slot is written.
    /// </summary>
    [InternalBufferCapacity(ShipRemoteMotorHistoryElement.Capacity)]
    public struct ShipRemoteMotorHistoryElement : IBufferElementData
    {
        /// <summary>Ticks kept. Matches the old replay window so a sample inside it can still be scored.</summary>
        public const int Capacity = 16;

        /// <summary>1 after <see cref="Tick"/> was written.</summary>
        public byte Valid;

        /// <summary>Integrated server tick this pose belongs to.</summary>
        public uint Tick;

        /// <summary>Canonical X at <see cref="Tick"/>.</summary>
        public float X;

        /// <summary>Canonical Z at <see cref="Tick"/>.</summary>
        public float Z;

        /// <summary>Planar yaw in radians at <see cref="Tick"/>.</summary>
        public float Yaw;

        /// <summary>Planar velocity X at <see cref="Tick"/>.</summary>
        public float VX;

        /// <summary>Planar velocity Z at <see cref="Tick"/>.</summary>
        public float VZ;
    }

    /// <summary>
    /// Client-only integrated remote pose. Not ghosted. Presentation reads the display
    /// fields; the next motor step continues from <see cref="Position"/> and
    /// <see cref="Velocity"/>, not from the bled display.
    /// </summary>
    public struct ShipRemoteMotorState : IComponentData
    {
        /// <summary>Server tick of the last flight sample this motor has already scored.</summary>
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

        /// <summary>1 while the hull is dead or waiting on a team, so presentation does not coast the wreck.</summary>
        public byte Holding;

        /// <summary>1 after the first sample has been applied.</summary>
        public byte Valid;
    }
}
