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
    /// from the server at the sample tick. Cruise-sized misses are stored as a pending
    /// error and flown out by the motor (extra velocity, a slow velocity blend, a capped
    /// yaw rate). The history ring is not pre-shifted: it keeps the pose the motor
    /// actually had, and each slot records the correction applied on the step that
    /// reached it so the next sample replaces the pending error with whatever is left.
    /// It does not rewind to the sample and replay the whole gap: that replay used only
    /// the newest input, so a late snapshot (common once many hulls share a send chunk)
    /// yanked every ship in the tile on the same frame. A collision-sized sample
    /// (velocity jump or a several-unit miss) adopts the server pose and clears the
    /// pending error, because a bounce is not a drift. The deterministic tick then
    /// bounces the integrated pose off ships, asteroids, planets, and moons. Does not
    /// enable <see cref="Simulate"/> or <see cref="PredictedGhost"/>. Runs once on the
    /// newest predicting tick, before the local drive. Map size comes from
    /// <see cref="MapStateSingleton"/> / <see cref="ToroidalMapEcs"/>.
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

        /// <summary>
        /// Seconds of motor time used to turn a cruise position or yaw error into a rate.
        /// About a third of a second, so the hull flies onto the server path.
        /// </summary>
        public const float CorrectTime = 0.33f;

        /// <summary>Cap on the extra planar velocity that closes position error (world units / second).</summary>
        public const float MaxPositionCorrectSpeed = 8f;

        /// <summary>
        /// How fast a velocity error blends into the motor (1/seconds). Slow so the
        /// one-tick render lead does not pop when a sample arrives.
        /// </summary>
        public const float VelocityCorrectSharpness = 3f;

        /// <summary>Cap on yaw correction (radians / second).</summary>
        public const float MaxYawCorrectRate = 1.5f;

        /// <summary>Gaps larger than this snap. Matches the old remote presentation snap.</summary>
        public const float SnapDistance = 24f;

        /// <summary>
        /// Velocity change above this, between the predicted pose and the server sample,
        /// is a bounce. Thrust over a short gap stays under it.
        /// </summary>
        public const float CollisionVelocityDelta = 8f;

        /// <summary>
        /// Position miss above this is a wall correction. Cruise drift over a few ticks stays under it.
        /// </summary>
        public const float CollisionPositionDelta = 6f;

        /// <summary>Require a flight sample before scheduling.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<ShipRemoteFlightSample>();
        }

        /// <summary>
        /// Steps each live remote at most <see cref="MaxCatchUpSteps"/> from its own
        /// integrated tick, then writes that motor pose onto <see cref="LocalTransform"/>
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
            var asteroidContacts = SystemAPI.GetComponentLookup<ShipAsteroidContactState>(true);

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
                bool collisionSnap = false;
                var asteroid = asteroidContacts.HasComponent(entity)
                    ? asteroidContacts[entity]
                    : default;
                // Punch-through keeps the remaining into-rock speed. Contact stick would cancel it.
                if (asteroid.PunchThrough != 0)
                    asteroid.InContact = 0;

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
                            // Error at the sample tick against the pose the motor actually had.
                            // A respawn is a large error and still lands on the spawn, not on the wreck.
                            float3 histPos = new float3(hist.X, 0f, hist.Z);
                            float3 err = ToroidalMapEcs.ShortestOffsetXZ(histPos, flight.Position, mapW, mapH);
                            err.y = 0f;
                            float yawErr = DeltaYaw(hist.Yaw, flight.Yaw);
                            float3 histVel = new float3(hist.VX, 0f, hist.VZ);
                            float3 velErr = flight.Velocity - histVel;
                            velErr.y = 0f;
                            // A bounce is not a constant offset. The hull has already
                            // tunneled past the wall, so fold in the post-hit sample
                            // extrapolated to this tick and show that pose immediately.
                            if (IsCollisionCorrection(histVel, flight.Velocity, math.length(err)))
                            {
                                float lead = math.max(0, gap) * dt;
                                float3 extrapolated = ToroidalMapEcs.Wrap(
                                    flight.Position + flight.Velocity * lead, mapW, mapH);
                                float3 adopt = ToroidalMapEcs.ShortestOffsetXZ(
                                    motor.Position, extrapolated, mapW, mapH);
                                adopt.y = 0f;
                                float3 adoptVel = flight.Velocity - motor.Velocity;
                                adoptVel.y = 0f;
                                float adoptYaw = DeltaYaw(
                                    ShipRemoteFlightSample.YawFromRotation(motor.Rotation), flight.Yaw);
                                ApplySampleError(
                                    ref motor, history, adopt, adoptYaw, adoptVel, flight.Tick, mapW, mapH);
                                collisionSnap = true;
                            }
                            else
                            {
                                StoreCruiseError(ref motor, history, err, yawErr, velErr, flight.Tick, subtractApplied: true);
                            }
                        }
                        else
                        {
                            // No ring entry (sample older than 16 ticks). Keep flying unless
                            // the constant-velocity guess is a respawn-sized miss or a bounce.
                            float3 expected = flight.Position;
                            if (gap > 0)
                                expected = ToroidalMapEcs.Wrap(flight.Position + flight.Velocity * (gap * dt), mapW, mapH);
                            float3 miss = ToroidalMapEcs.ShortestOffsetXZ(motor.Position, expected, mapW, mapH);
                            float missLen = math.length(miss);
                            bool collision = IsCollisionCorrection(motor.Velocity, flight.Velocity, missLen);
                            if (missLen > SnapDistance || collision)
                            {
                                AssignSamplePose(ref motor, in flight, shipState.ValueRO, in moon, in orbit);
                                motor.Position = expected;
                                motor.IntegratedTick = networkTime.ServerTick.TickIndexForValidTick;
                                ClearHistory(history);
                                steps = 0;
                                collisionSnap = collision;
                            }
                            else
                            {
                                float yawErr = DeltaYaw(
                                    ShipRemoteFlightSample.YawFromRotation(motor.Rotation), flight.Yaw);
                                float3 velErr = flight.Velocity - motor.Velocity;
                                velErr.y = 0f;
                                // miss is server-extrapolated versus the live pose, so it is already
                                // the residual. Do not subtract steps that moved this pose.
                                StoreCruiseError(ref motor, history, miss, yawErr, velErr, flight.Tick, subtractApplied: false);
                            }
                        }
                    }
                }

                // The pose at IntegratedTick has to be in the ring before we step,
                // or the next sample for this tick has nothing to score against.
                // Keep the correction already recorded for this tick.
                if (!dead)
                    RecordHistory(history, in motor, writeApplied: false);

                var input = flight.ToFlightInput();
                for (int s = 0; s < steps; s++)
                {
                    Advance(
                        ref motor, in input, in motorConfig, in shock, in turret,
                        planets.AsArray(), territory, homeLevels, mobility,
                        dt, mapW, mapH, elapsed, scale, radius, isMega, pointGuns, in asteroid);
                    var integrated = new NetworkTick(motor.IntegratedTick);
                    integrated.Add(1);
                    motor.IntegratedTick = integrated.TickIndexForValidTick;
                    RecordHistory(history, in motor, writeApplied: true);
                }

                motor.Holding = (byte)(dead ? 1 : 0);
                motor.IntegratedUnscaledTime = now;
                PublishDisplay(ref motor, motor.Position, motor.Rotation, mapW, mapH, dead || acquire || collisionSnap);
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
            ClearCruiseCorrection(ref motor);
            motor.CorrectionTrustTick = 0;
        }

        /// <summary>
        /// Replaces the pending cruise correction with the residual at this sample.
        /// History positions stay as the motor flew them. Correction already applied on
        /// later ticks is subtracted so this sample does not fly that distance again.
        /// A sample from before a client-side bounce is ignored: that pose was discarded.
        /// </summary>
        static void StoreCruiseError(
            ref ShipRemoteMotorState motor,
            DynamicBuffer<ShipRemoteMotorHistoryElement> history,
            float3 posError,
            float yawError,
            float3 velError,
            uint sampleTick,
            bool subtractApplied)
        {
            if (motor.CorrectionTrustTick != 0)
            {
                int sinceTrust = new NetworkTick(sampleTick).TicksSince(new NetworkTick(motor.CorrectionTrustTick));
                if (sinceTrust < 0)
                    return;
            }

            float3 appliedPos = float3.zero;
            float appliedYaw = 0f;
            float3 appliedVel = float3.zero;
            if (subtractApplied)
            {
                SumAppliedAfter(
                    history, sampleTick, motor.IntegratedTick,
                    out appliedPos, out appliedYaw, out appliedVel);
            }

            float3 pendingPos = posError - appliedPos;
            pendingPos.y = 0f;
            motor.PendingPosError = pendingPos;
            motor.PendingYawError = WrapYaw(yawError - appliedYaw);
            float3 pendingVel = velError - appliedVel;
            pendingVel.y = 0f;
            motor.PendingVelError = pendingVel;
        }

        /// <summary>
        /// Sums correction applied on history ticks strictly after <paramref name="sampleTick"/>
        /// and not after <paramref name="integratedTick"/>. Those steps are in the live pose
        /// and not in the sample-tick position.
        /// </summary>
        static void SumAppliedAfter(
            DynamicBuffer<ShipRemoteMotorHistoryElement> history,
            uint sampleTick,
            uint integratedTick,
            out float3 pos,
            out float yaw,
            out float3 vel)
        {
            pos = float3.zero;
            yaw = 0f;
            vel = float3.zero;
            int capacity = ShipRemoteMotorHistoryElement.Capacity;
            if (history.Length != capacity)
                return;

            for (int i = 0; i < capacity; i++)
            {
                ShipRemoteMotorHistoryElement entry = history[i];
                if (entry.Valid == 0)
                    continue;
                int afterSample = new NetworkTick(entry.Tick).TicksSince(new NetworkTick(sampleTick));
                if (afterSample <= 0 || afterSample >= capacity)
                    continue;
                int untilNow = new NetworkTick(integratedTick).TicksSince(new NetworkTick(entry.Tick));
                if (untilNow < 0)
                    continue;
                pos.x += entry.AppliedX;
                pos.z += entry.AppliedZ;
                yaw += entry.AppliedYaw;
                vel.x += entry.AppliedVX;
                vel.z += entry.AppliedVZ;
            }
        }

        /// <summary>Drops a cruise correction. Used on acquire, death, and a hard adopt.</summary>
        static void ClearCruiseCorrection(ref ShipRemoteMotorState motor)
        {
            motor.PendingPosError = float3.zero;
            motor.PendingYawError = 0f;
            motor.PendingVelError = float3.zero;
            motor.CorrectionVelocity = float3.zero;
            motor.StepPosX = 0f;
            motor.StepPosZ = 0f;
            motor.StepYaw = 0f;
            motor.StepVelX = 0f;
            motor.StepVelZ = 0f;
        }

        /// <summary>
        /// Adds the measured sample error to the live pose and to every history entry
        /// from that tick forward, so the next sample does not apply the same offset again.
        /// Collision adopts only. Cruise misses use <see cref="StoreCruiseError"/>.
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
            ClearCruiseCorrection(ref motor);

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
                // The shifted pose already includes this adopt. A leftover step delta
                // would be subtracted again on the next cruise sample.
                entry.AppliedX = 0f;
                entry.AppliedZ = 0f;
                entry.AppliedYaw = 0f;
                entry.AppliedVX = 0f;
                entry.AppliedVZ = 0f;
                history[i] = entry;
            }
        }

        /// <summary>Signed shortest yaw delta, radians.</summary>
        static float DeltaYaw(float from, float to)
        {
            return WrapYaw(to - from);
        }

        /// <summary>
        /// True when the server sample disagrees like a wall hit rather than thrust drift.
        /// </summary>
        static bool IsCollisionCorrection(float3 fromVelocity, float3 toVelocity, float positionError)
        {
            if (positionError > CollisionPositionDelta)
                return true;
            float3 delta = toVelocity - fromVelocity;
            delta.y = 0f;
            return math.length(delta) > CollisionVelocityDelta;
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

        /// <summary>
        /// Writes the integrated pose into the slot for its tick.
        /// <paramref name="writeApplied"/> stores the correction from the step that just
        /// arrived at this tick. A rewrite of the current tick keeps the delta already there.
        /// </summary>
        static void RecordHistory(
            DynamicBuffer<ShipRemoteMotorHistoryElement> history,
            in ShipRemoteMotorState motor,
            bool writeApplied)
        {
            int capacity = ShipRemoteMotorHistoryElement.Capacity;
            if (history.Length != capacity)
            {
                history.ResizeUninitialized(capacity);
                for (int i = 0; i < capacity; i++)
                    history[i] = default;
            }

            int slot = (int)(motor.IntegratedTick % (uint)capacity);
            ShipRemoteMotorHistoryElement previous = history[slot];
            float appliedX = motor.StepPosX;
            float appliedZ = motor.StepPosZ;
            float appliedYaw = motor.StepYaw;
            float appliedVx = motor.StepVelX;
            float appliedVz = motor.StepVelZ;
            if (!writeApplied && previous.Valid != 0 && previous.Tick == motor.IntegratedTick)
            {
                appliedX = previous.AppliedX;
                appliedZ = previous.AppliedZ;
                appliedYaw = previous.AppliedYaw;
                appliedVx = previous.AppliedVX;
                appliedVz = previous.AppliedVZ;
            }

            history[slot] = new ShipRemoteMotorHistoryElement
            {
                Valid = 1,
                Tick = motor.IntegratedTick,
                X = motor.Position.x,
                Z = motor.Position.z,
                Yaw = ShipRemoteFlightSample.YawFromRotation(motor.Rotation),
                VX = motor.Velocity.x,
                VZ = motor.Velocity.z,
                AppliedX = appliedX,
                AppliedZ = appliedZ,
                AppliedYaw = appliedYaw,
                AppliedVX = appliedVx,
                AppliedVZ = appliedVz,
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
            bool pointGuns,
            in ShipAsteroidContactState asteroidContact)
        {
            if (turret.IsControlling || shock.IsActive(elapsed))
            {
                motor.Velocity = float3.zero;
                motor.Ship.OverdriveBurstActive = false;
                // Pinned: do not keep last tick's correction in the render lead or the history slot.
                motor.CorrectionVelocity = float3.zero;
                motor.StepPosX = 0f;
                motor.StepPosZ = 0f;
                motor.StepYaw = 0f;
                motor.StepVelX = 0f;
                motor.StepVelZ = 0f;
                return;
            }

            var transform = LocalTransform.FromPositionRotationScale(motor.Position, motor.Rotation, math.max(0.01f, scale));
            var velocity = new PhysicsVelocity { Linear = motor.Velocity };
            velocity.Linear.y = 0f;
            var damping = default(PhysicsDamping);
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
                asteroidContact,
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
            ApplyCruiseCorrection(ref motor, dt, mapW, mapH);
        }

        /// <summary>
        /// Flies a cruise sample error out after the shared motor step. Position error
        /// becomes a capped extra velocity in this same integration. Velocity error blends
        /// in slowly. Yaw error becomes a capped yaw rate. The pending vectors shrink by
        /// exactly the amount applied, position via the toroidal shortest offset, so the
        /// next sample does not apply that distance again. Dead, team-select, and a pinned
        /// moon dock skip the bias. The extra velocity is not written into
        /// <see cref="ShipRemoteMotorState.Velocity"/> — that stays the flight model so the
        /// next velocity sample is not fighting a correction we baked in.
        /// </summary>
        static void ApplyCruiseCorrection(ref ShipRemoteMotorState motor, float dt, float mapW, float mapH)
        {
            motor.StepPosX = 0f;
            motor.StepPosZ = 0f;
            motor.StepYaw = 0f;
            motor.StepVelX = 0f;
            motor.StepVelZ = 0f;
            motor.CorrectionVelocity = float3.zero;

            if (dt <= 0f)
                return;
            if (motor.Ship.IsDead || motor.Ship.AwaitingTeamSelection)
                return;
            if (motor.MoonDock.IsFullyLanded || motor.MoonDock.IsTakingOff)
                return;

            float gain = 1f / math.max(CorrectTime, 1e-4f);

            float3 posErr = motor.PendingPosError;
            posErr.y = 0f;
            float errLen = math.length(posErr);
            if (errLen > 1e-5f)
            {
                float3 target = ToroidalMapEcs.Wrap(motor.Position + posErr, mapW, mapH);
                float3 corrVel = posErr * gain;
                float corrSpeed = math.length(corrVel);
                if (corrSpeed > MaxPositionCorrectSpeed)
                    corrVel *= MaxPositionCorrectSpeed / corrSpeed;
                float3 posStep = corrVel * dt;
                float stepLen = math.length(posStep);
                if (stepLen > errLen && stepLen > 1e-8f)
                    posStep *= errLen / stepLen;

                motor.Position = ToroidalMapEcs.Wrap(motor.Position + posStep, mapW, mapH);
                float3 remain = ToroidalMapEcs.ShortestOffsetXZ(motor.Position, target, mapW, mapH);
                remain.y = 0f;
                motor.PendingPosError = remain;
                motor.CorrectionVelocity = posStep / dt;
                motor.StepPosX = posStep.x;
                motor.StepPosZ = posStep.z;
            }

            float vBlend = 1f - math.exp(-VelocityCorrectSharpness * math.max(0f, dt));
            float3 velErr = motor.PendingVelError;
            velErr.y = 0f;
            float3 velStep = velErr * vBlend;
            motor.Velocity += velStep;
            motor.Velocity.y = 0f;
            float3 velRemain = velErr - velStep;
            velRemain.y = 0f;
            motor.PendingVelError = velRemain;
            motor.StepVelX = velStep.x;
            motor.StepVelZ = velStep.z;

            float yawErr = motor.PendingYawError;
            if (math.abs(yawErr) > 1e-6f)
            {
                float yawRate = math.clamp(yawErr * gain, -MaxYawCorrectRate, MaxYawCorrectRate);
                float yawStep = yawRate * dt;
                if (math.abs(yawStep) > math.abs(yawErr))
                    yawStep = yawErr;
                float yaw = ShipRemoteFlightSample.YawFromRotation(motor.Rotation) + yawStep;
                motor.Rotation = quaternion.RotateY(WrapYaw(yaw));
                motor.PendingYawError = WrapYaw(yawErr - yawStep);
                motor.StepYaw = yawStep;
            }
        }

        /// <summary>
        /// Copies the motor pose onto the visible hull. Cruise error is already in that
        /// pose, so there is no second chase. Acquire, death, collision, a toroidal wrap,
        /// and a gap past <see cref="SnapDistance"/> are the same copy — the mesh does not
        /// lerp across the map.
        /// </summary>
        static void PublishDisplay(
            ref ShipRemoteMotorState motor,
            float3 targetPos,
            quaternion targetRot,
            float mapW,
            float mapH,
            bool hardSnap)
        {
            bool wrap = motor.Valid != 0 && ToroidalMapEcs.IsWrapJump(motor.DisplayPosition, targetPos, mapW, mapH);
            float gapLen = motor.Valid == 0
                ? 0f
                : math.length(ToroidalMapEcs.ShortestOffsetXZ(motor.DisplayPosition, targetPos, mapW, mapH));
            // Both arms assign. The branch stays so a wrap or a 24u gap is an explicit snap
            // and hardSnap (acquire, death, collision) is not a dead write.
            if (motor.Valid == 0 || hardSnap || wrap || gapLen > SnapDistance)
            {
                motor.DisplayPosition = targetPos;
                motor.DisplayRotation = targetRot;
                return;
            }

            motor.DisplayPosition = targetPos;
            motor.DisplayRotation = targetRot;
        }

        /// <summary>
        /// Writes the bled pose and the integrated velocity. Does not add PhysicsVelocity.
        /// Leaves <see cref="LocalTransform.Scale"/> alone — tier size comes from
        /// <c>ShipStatApplySystem</c>.
        /// </summary>
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

        /// <summary>Position correction applied on the step that arrived at <see cref="Tick"/>, X.</summary>
        public float AppliedX;

        /// <summary>Position correction applied on the step that arrived at <see cref="Tick"/>, Z.</summary>
        public float AppliedZ;

        /// <summary>Yaw correction applied on the step that arrived at <see cref="Tick"/>, radians.</summary>
        public float AppliedYaw;

        /// <summary>Velocity correction blended in on the step that arrived at <see cref="Tick"/>, X.</summary>
        public float AppliedVX;

        /// <summary>Velocity correction blended in on the step that arrived at <see cref="Tick"/>, Z.</summary>
        public float AppliedVZ;
    }

    /// <summary>
    /// Client-only integrated remote pose. Not ghosted. Presentation reads the display
    /// fields, which match <see cref="Position"/> in cruise. The next motor step continues
    /// from <see cref="Position"/> and <see cref="Velocity"/>.
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

        /// <summary>Integrated planar velocity. Flight model only; cruise position correction is separate.</summary>
        public float3 Velocity;

        /// <summary>
        /// Position error still to fly out, as a toroidal offset from <see cref="Position"/>
        /// toward the server. Replaced by each cruise sample, not stacked.
        /// </summary>
        public float3 PendingPosError;

        /// <summary>Yaw error still to fly out, radians, shortest direction.</summary>
        public float PendingYawError;

        /// <summary>Velocity error still to blend in.</summary>
        public float3 PendingVelError;

        /// <summary>
        /// Extra planar velocity applied for the position error on the last step.
        /// Presentation adds this to the one-tick lead. Not part of <see cref="Velocity"/>.
        /// </summary>
        public float3 CorrectionVelocity;

        /// <summary>
        /// Client tick of the last bounce that discarded the integrated pose. Cruise samples
        /// older than this are not stored; their history no longer matches the live hull.
        /// 0 until that happens.
        /// </summary>
        public uint CorrectionTrustTick;

        /// <summary>Position correction applied by the last <see cref="ShipRemoteMotorSystem"/> step, X.</summary>
        public float StepPosX;

        /// <summary>Position correction applied by the last step, Z.</summary>
        public float StepPosZ;

        /// <summary>Yaw correction applied by the last step, radians.</summary>
        public float StepYaw;

        /// <summary>Velocity correction applied by the last step, X.</summary>
        public float StepVelX;

        /// <summary>Velocity correction applied by the last step, Z.</summary>
        public float StepVelZ;

        /// <summary>Motor pose written to the ghost transform. Matches <see cref="Position"/> in cruise.</summary>
        public float3 DisplayPosition;

        /// <summary>Motor rotation written to the ghost transform.</summary>
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
