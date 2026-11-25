using TitanOrbit.Core;
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
    /// then resolves ship-vs-ship and ship-vs-asteroid overlap from the same formulas.
    /// Asteroids stay put (infinite-mass wall). The ship takes the bounce and a capped
    /// separation. Unity Physics must not depenetrate that pose afterward — the eased
    /// PhysX pop was the ghost snapshot, and it walked the hull off the predicted path.
    /// Planets and moons still bounce from the physics contact stream.
    /// On the client, a remote hull is not simulated by Unity Physics (no
    /// <c>PhysicsVelocity</c>, kinematic). This tick still bounces it off ships,
    /// asteroids, planets, and moons, and writes that pose back onto
    /// <see cref="ShipRemoteMotorState"/>. The local predicted hull takes its own
    /// mass-weighted velocity and separation, the same split the server writes, so
    /// the client motor bounces on impact instead of waiting for a snapshot.
    /// Rollback replays that bounce against the remote history and does not rewrite
    /// the live remote motor. Map size comes from
    /// <see cref="MapStateSingleton"/> / <see cref="ToroidalMapEcs"/>.
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
        NativeList<AsteroidTickBody> _rocks;
        NativeList<WorldWallBody> _walls;
        NativeList<ShipTickAsteroidContactElement> _asteroidContacts;
        NativeList<ShipTickAsteroidPoseElement> _asteroidPoses;
        NativeList<byte> _touchedRock;
        NativeList<int> _asteroidOrder;
        NativeParallelMultiHashMap<int, int> _bins;
        int _binCapacity;

        /// <summary>
        /// Client-only. Tick when a killing ram already bled this rock's resistance.
        /// Later predicted ticks must not bleed it again. The server destroys the rock
        /// in the same ram, but the client rock stays until HitRpc, so without this
        /// the client keeps slowing and the snapshot yanks the hull back.
        /// Cleared when prediction rewinds so the replay can land the one blow again.
        /// </summary>
        NativeHashMap<Entity, uint> _clientBrokenRocks;

        /// <summary>Reuse list for pruning despawned rocks. No per-tick allocation.</summary>
        NativeList<Entity> _clientBrokenDrop;

        /// <summary>Last predicted tick seen. A step backward is a rollback.</summary>
        uint _lastPredictTick;

        /// <summary>False until <see cref="_lastPredictTick"/> holds a real tick.</summary>
        bool _hasPredictTick;

        /// <summary>True for this client tick when break-tracking is active.</summary>
        bool _clientBreaksActive;

        /// <summary>Server tick being simulated this call. 0 on the server.</summary>
        uint _simTick;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<ShipTag>();
            _bodies = new NativeList<ShipTickBody>(32, Allocator.Persistent);
            _deltaV = new NativeList<float3>(32, Allocator.Persistent);
            _deltaP = new NativeList<float3>(32, Allocator.Persistent);
            _holds = new NativeList<ShipTickHoldElement>(32, Allocator.Persistent);
            _overlaps = new NativeList<ShipTickOverlapElement>(32, Allocator.Persistent);
            _rocks = new NativeList<AsteroidTickBody>(256, Allocator.Persistent);
            _walls = new NativeList<WorldWallBody>(32, Allocator.Persistent);
            _asteroidContacts = new NativeList<ShipTickAsteroidContactElement>(32, Allocator.Persistent);
            _asteroidPoses = new NativeList<ShipTickAsteroidPoseElement>(32, Allocator.Persistent);
            _touchedRock = new NativeList<byte>(32, Allocator.Persistent);
            _asteroidOrder = new NativeList<int>(32, Allocator.Persistent);
            _bins = new NativeParallelMultiHashMap<int, int>(128, Allocator.Persistent);
            _binCapacity = 128;
            _clientBrokenRocks = new NativeHashMap<Entity, uint>(32, Allocator.Persistent);
            _clientBrokenDrop = new NativeList<Entity>(8, Allocator.Persistent);

            Entity motion;
            if (!SystemAPI.TryGetSingletonEntity<ShipTickMotionTag>(out motion))
            {
                motion = state.EntityManager.CreateEntity();
                state.EntityManager.AddComponentData(motion, new ShipTickMotionTag());
                state.EntityManager.AddBuffer<ShipTickHoldElement>(motion);
                state.EntityManager.AddBuffer<ShipTickOverlapElement>(motion);
            }

            // A singleton left over from before asteroid contacts existed has no buffer yet.
            if (!state.EntityManager.HasBuffer<ShipTickAsteroidContactElement>(motion))
                state.EntityManager.AddBuffer<ShipTickAsteroidContactElement>(motion);
            if (!state.EntityManager.HasBuffer<ShipTickAsteroidPoseElement>(motion))
                state.EntityManager.AddBuffer<ShipTickAsteroidPoseElement>(motion);
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
            if (_rocks.IsCreated)
                _rocks.Dispose();
            if (_walls.IsCreated)
                _walls.Dispose();
            if (_asteroidContacts.IsCreated)
                _asteroidContacts.Dispose();
            if (_asteroidPoses.IsCreated)
                _asteroidPoses.Dispose();
            if (_touchedRock.IsCreated)
                _touchedRock.Dispose();
            if (_asteroidOrder.IsCreated)
                _asteroidOrder.Dispose();
            if (_bins.IsCreated)
                _bins.Dispose();
            if (_clientBrokenRocks.IsCreated)
                _clientBrokenRocks.Dispose();
            if (_clientBrokenDrop.IsCreated)
                _clientBrokenDrop.Dispose();
        }

        public void OnUpdate(ref SystemState state)
        {
            _holds.Clear();
            _overlaps.Clear();
            _asteroidContacts.Clear();
            _asteroidPoses.Clear();
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
            // Forward tick writes remote motors. Rollback only reads them, or a resim
            // would stack the same shove onto the live remote pose.
            bool forwardTick = true;
            uint simTick = 0;
            if (isClient &&
                SystemAPI.TryGetSingleton(out NetworkTime predictTime) &&
                predictTime.ServerTick.IsValid)
            {
                forwardTick = predictTime.IsFirstTimeFullyPredictingTick;
                simTick = predictTime.ServerTick.TickIndexForValidTick;
            }

            _simTick = simTick;
            _clientBreaksActive = isClient;
            if (isClient)
                TrackClientAsteroidBreaks(state.EntityManager, simTick);

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
            var megaLookup = SystemAPI.GetComponentLookup<MegaShipState>(true);
            var loadoutLookup = SystemAPI.GetComponentLookup<ShipLoadoutState>(true);
            var territoryLookup = SystemAPI.GetComponentLookup<ShipTerritoryBoostLatch>(true);
            var asteroidContactLookup = SystemAPI.GetComponentLookup<ShipAsteroidContactState>(false);
            var remoteMotorLookup = SystemAPI.GetComponentLookup<ShipRemoteMotorState>(false);
            var remoteHistoryLookup = SystemAPI.GetBufferLookup<ShipRemoteMotorHistoryElement>(false);

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

                // Client bots that still have a physics body must take their half of the
                // bounce. Leaving them unwritable made an equal-mass AI hull plow through.
                // A remote motor, when present, is the pose the next step continues from.
                if (isClient && BotShipIds.IsBot(networkId))
                {
                    if (!forwardTick)
                        continue;
                    bool botMotor = remoteMotorLookup.HasComponent(entity)
                                    && remoteMotorLookup[entity].Valid != 0;
                    if (botMotor)
                    {
                        ShipRemoteMotorState botPose = remoteMotorLookup[entity];
                        AddBody(
                            ref _bodies, entity, transform.ValueRO, kinematicsLookup, motorLookup,
                            shipLookup, radiusLookup, colliderLookup, networkId,
                            mobility, write: true, integrate: false, dt, mapW, mapH,
                            remote: true, usePose: true,
                            posePosition: botPose.Position, poseVelocity: botPose.Velocity);
                        continue;
                    }

                    float3 botFlight = kinematicsLookup.HasComponent(entity)
                        ? kinematicsLookup[entity].Velocity
                        : velocity.ValueRO.Linear;
                    AddBody(
                        ref _bodies, entity, transform.ValueRO, kinematicsLookup, motorLookup,
                        shipLookup, radiusLookup, colliderLookup, networkId,
                        mobility, write: true, integrate: true, dt, mapW, mapH,
                        integratedVelocity: botFlight);
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

                // Remote motor already integrated this tick. Bounce that pose here.
                // A docked hull stays put so the moon attach is not shoved off the pad.
                bool useMotor = remoteMotorLookup.HasComponent(entity)
                                && remoteMotorLookup[entity].Valid != 0;
                bool docked = moonLookup.HasComponent(entity)
                              && (moonLookup[entity].IsFullyLanded || moonLookup[entity].IsTakingOff);
                float3 motorPos = default;
                float3 motorVel = default;
                bool writeRemote = false;
                if (useMotor)
                {
                    ShipRemoteMotorState remoteMotor = remoteMotorLookup[entity];
                    motorPos = remoteMotor.Position;
                    motorVel = remoteMotor.Velocity;
                    writeRemote = forwardTick && !docked;
                    if (!forwardTick &&
                        !TryReplayRemotePose(
                            remoteHistoryLookup, entity, simTick, dt, mapW, mapH,
                            ref motorPos, ref motorVel))
                    {
                        // Ticks older than the motor have no sample. The live pose is
                        // where the hull is now, so using it would bounce the past ship.
                        // Ticks at or ahead of the motor hold that pose: the local ship
                        // predicts into a remote that is not stepped again on rollback.
                        int ahead = new NetworkTick(simTick).TicksSince(
                            new NetworkTick(remoteMotor.IntegratedTick));
                        if (ahead < 0)
                            continue;
                    }
                }
                else if (!forwardTick)
                {
                    continue;
                }

                AddBody(
                    ref _bodies, entity, transform.ValueRO, kinematicsLookup, motorLookup,
                    shipLookup, radiusLookup, colliderLookup, networkId,
                    mobility, write: writeRemote, integrate: false, dt, mapW, mapH,
                    remote: useMotor, usePose: useMotor, posePosition: motorPos, poseVelocity: motorVel);
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

            SolveNearbyPairs(mapW, mapH);

            _rocks.Clear();
            foreach (var (rockState, rockTransform, rockEntity) in SystemAPI
                         .Query<RefRO<AsteroidState>, RefRO<LocalTransform>>()
                         .WithAll<AsteroidTag>()
                         .WithNone<Prefab>()
                         .WithEntityAccess())
            {
                if (rockState.ValueRO.IsDestroyed || rockState.ValueRO.Health <= 0.01f)
                    continue;
                _rocks.Add(new AsteroidTickBody
                {
                    Entity = rockEntity,
                    Position = rockTransform.ValueRO.Position,
                    Radius = BodyCollisionMath.GetAsteroidBodyRadiusWorld(rockTransform.ValueRO.Scale),
                    Health = rockState.ValueRO.Health,
                });
            }

            var rockSettings = AsteroidSettingsCache.ResolveOrDefault();
            rockSettings.ClampValues();
            SolveNearbyAsteroids(
                mapW, mapH, rockSettings.BounceRestitution,
                megaLookup, motorLookup, shipLookup,
                loadoutLookup, territoryLookup, asteroidContactLookup);
            if (isClient)
            {
                double elapsed = SystemAPI.Time.ElapsedTime;
                if (SystemAPI.TryGetSingleton(out NetworkTime networkTime))
                {
                    int hz = 0;
                    if (SystemAPI.TryGetSingleton<ClientServerTickRate>(out var tickRate))
                        hz = tickRate.SimulationTickRate;
                    elapsed = PlanetGemMoonOrbitClock.GetElapsedSeconds(networkTime, hz, includeTickFraction: false);
                }

                SolveRemoteWorldWalls(ref state, mapW, mapH, rockSettings.BounceRestitution, elapsed, shipLookup);
            }

            for (int i = 0; i < count; i++)
            {
                ShipTickBody body = _bodies[i];
                if (body.Write == 0)
                    continue;
                if (!transformLookup.HasComponent(body.Entity) || !kinematicsLookup.HasComponent(body.Entity))
                    continue;
                // Interpolated remotes have no PhysicsVelocity. Predicted hulls still do.
                if (body.Remote == 0 && !velocityLookup.HasComponent(body.Entity))
                    continue;

                float3 dp = _deltaP[i];
                dp.y = 0f;
                float dpLen = math.length(dp);
                if (dpLen > ShipCollisionImpulseLogic.ShipShipResolveMaxSeparation)
                    dp *= ShipCollisionImpulseLogic.ShipShipResolveMaxSeparation / dpLen;

                float3 pos = body.Position + dp;
                pos.y = 0f;
                pos = ToroidalMapEcs.Wrap(pos, mapW, mapH);

                float3 vel = body.Velocity + _deltaV[i];
                vel.y = 0f;

                // Free-flight remotes keep the motor pose. Only a real hit overwrites it.
                bool separated = math.lengthsq(dp) > 1e-8f || math.lengthsq(_deltaV[i]) > 1e-6f;
                if (body.Remote != 0 && !separated)
                {
                    PublishRemoteAsteroidContact(i, body.Entity, asteroidContactLookup);
                    continue;
                }

                var lt = transformLookup[body.Entity];
                lt.Position = pos;
                transformLookup[body.Entity] = lt;

                var kin = kinematicsLookup[body.Entity];
                kin.Velocity = vel;
                kinematicsLookup[body.Entity] = kin;

                if (velocityLookup.HasComponent(body.Entity))
                {
                    var pv = velocityLookup[body.Entity];
                    pv.Linear = float3.zero;
                    pv.Angular = float3.zero;
                    velocityLookup[body.Entity] = pv;
                }

                if (body.Remote != 0 && remoteMotorLookup.HasComponent(body.Entity))
                {
                    ShipRemoteMotorState remoteMotor = remoteMotorLookup[body.Entity];
                    remoteMotor.Position = pos;
                    remoteMotor.Velocity = vel;
                    remoteMotor.DisplayPosition = pos;
                    remoteMotor.DisplayRotation = lt.Rotation;
                    // The bounce discarded the cruise offset. Do not fly the pre-hit error
                    // back through the wall. Samples from before this tick are ignored.
                    remoteMotor.PendingPosError = float3.zero;
                    remoteMotor.PendingYawError = 0f;
                    remoteMotor.PendingVelError = float3.zero;
                    remoteMotor.CorrectionVelocity = float3.zero;
                    remoteMotor.StepPosX = 0f;
                    remoteMotor.StepPosZ = 0f;
                    remoteMotor.StepYaw = 0f;
                    remoteMotor.StepVelX = 0f;
                    remoteMotor.StepVelZ = 0f;
                    remoteMotor.CorrectionTrustTick = remoteMotor.IntegratedTick;
                    remoteMotorLookup[body.Entity] = remoteMotor;
                    if (remoteHistoryLookup.HasBuffer(body.Entity))
                        PatchRemoteHistory(remoteHistoryLookup[body.Entity], remoteMotor);
                }

                if (body.Remote != 0)
                    PublishRemoteAsteroidContact(i, body.Entity, asteroidContactLookup);

                _holds.Add(new ShipTickHoldElement
                {
                    Ship = body.Entity,
                    Linear = vel,
                });

                if (i < _touchedRock.Length && _touchedRock[i] != 0)
                {
                    _asteroidPoses.Add(new ShipTickAsteroidPoseElement
                    {
                        Ship = body.Entity,
                        Position = pos,
                        Rotation = lt.Rotation,
                    });
                }
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

            if (SystemAPI.TryGetSingletonBuffer<ShipTickAsteroidContactElement>(out var asteroidContacts))
            {
                asteroidContacts.Clear();
                for (int i = 0; i < _asteroidContacts.Length; i++)
                    asteroidContacts.Add(_asteroidContacts[i]);
            }

            if (SystemAPI.TryGetSingletonBuffer<ShipTickAsteroidPoseElement>(out var asteroidPoses))
            {
                asteroidPoses.Clear();
                for (int i = 0; i < _asteroidPoses.Length; i++)
                    asteroidPoses.Add(_asteroidPoses[i]);
            }
        }

        /// <summary>
        /// Ship-ship shove only for hulls that can touch. Bins are one diameter wide, so a
        /// ship checks its own cell and the eight neighbors (wrapped on the torus). Far
        /// pairs never reach the impulse.
        /// </summary>
        void SolveNearbyPairs(float mapW, float mapH)
        {
            int count = _bodies.Length;
            if (count < 2)
                return;

            float maxR = 0.5f;
            for (int i = 0; i < count; i++)
                maxR = math.max(maxR, _bodies[i].Radius);

            float cell = math.max(8f, maxR * 2f);
            int cellsX = math.max(1, (int)math.ceil(mapW / cell));
            int cellsZ = math.max(1, (int)math.ceil(mapH / cell));
            if (count > _binCapacity)
            {
                _bins.Dispose();
                _binCapacity = count * 2;
                _bins = new NativeParallelMultiHashMap<int, int>(_binCapacity, Allocator.Persistent);
            }
            _bins.Clear();

            for (int i = 0; i < count; i++)
            {
                CellCoords(_bodies[i].Position, cell, cellsX, cellsZ, mapW, mapH, out int cx, out int cz);
                _bins.Add(cz * cellsX + cx, i);
            }

            for (int i = 0; i < count; i++)
            {
                CellCoords(_bodies[i].Position, cell, cellsX, cellsZ, mapW, mapH, out int cx, out int cz);
                for (int dz = -1; dz <= 1; dz++)
                {
                    int nz = Mod(cz + dz, cellsZ);
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int key = nz * cellsX + Mod(cx + dx, cellsX);
                        if (!_bins.TryGetFirstValue(key, out int j, out var it))
                            continue;
                        do
                        {
                            if (j > i)
                                TryShovePair(i, j, mapW, mapH);
                        }
                        while (_bins.TryGetNextValue(out j, ref it));
                    }
                }
            }
        }

        void TryShovePair(int i, int j, float mapW, float mapH)
        {
            ShipTickBody a = _bodies[i];
            ShipTickBody b = _bodies[j];
            if (a.Write == 0 && b.Write == 0)
                return;

            float3 offset = ToroidalMapEcs.ShortestOffsetXZ(b.Position, a.Position, mapW, mapH);
            offset.y = 0f;
            float3 incoming = ToroidalMapEcs.ShortestOffsetXZ(b.PrePosition, a.PrePosition, mapW, mapH);
            incoming.y = 0f;
            float reach = a.Radius + b.Radius;
            float distSq = offset.x * offset.x + offset.z * offset.z;
            float incomingSq = incoming.x * incoming.x + incoming.z * incoming.z;
            // A fast pair can swap sides in one step. The post-step normal then points
            // out the far side and the impulse lets them finish the plow.
            bool crossed = incomingSq > 1e-8f && distSq > 1e-8f && math.dot(incoming, offset) < 0f;
            if (distSq >= reach * reach && !crossed)
                return;

            float dist = math.sqrt(distSq);
            float3 normal;
            if (crossed)
            {
                float incomingDist = math.sqrt(incomingSq);
                normal = incoming / incomingDist;
                // Past the center: signed distance is negative, so separation
                // pushes both hulls back to the side they came from.
                dist = -dist;
            }
            else
            {
                normal = dist > 1e-4f ? offset / dist : new float3(1f, 0f, 0f);
            }
            ShipCollisionImpulseLogic.ComputeDeterministicShipShip(
                a.Velocity, b.Velocity, normal, a.Mass, b.Mass, reach - dist,
                out float3 dVa, out float3 dVb, out float3 dPa, out float3 dPb,
                out float closing);

            // Same split on the server and the predicting client. The local motor
            // owns its half, so the hull bounces here. A remote that is not writable
            // this tick (rollback) still supplies pose and velocity for that split.
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

        /// <summary>
        /// Asteroid bounce on the same tick as ship-vs-ship. Rocks are binned once.
        /// A writable ship tests its cell and the eight toroidal neighbors. The rock
        /// does not move. MEGA plows (overlap only). A killing blow reduces into-rock
        /// speed instead of reflecting. Every other overlap reflects with the asteroid
        /// restitution and pushes the ship out by at most one ship-ship separation cap.
        /// Neighbor rocks are applied in canonical XZ order so a multi-rock tick matches
        /// on every peer. Hash-map walk order is not that order.
        /// </summary>
        void SolveNearbyAsteroids(
            float mapW,
            float mapH,
            float restitution,
            ComponentLookup<MegaShipState> megas,
            ComponentLookup<ShipMotorConfig> motors,
            ComponentLookup<ShipState> ships,
            ComponentLookup<ShipLoadoutState> loadouts,
            ComponentLookup<ShipTerritoryBoostLatch> territory,
            ComponentLookup<ShipAsteroidContactState> contacts)
        {
            int shipsN = _bodies.Length;
            int rocksN = _rocks.Length;
            _touchedRock.Clear();
            for (int i = 0; i < shipsN; i++)
                _touchedRock.Add(0);
            if (shipsN == 0 || rocksN == 0)
                return;

            float maxReach = 8f;
            for (int i = 0; i < shipsN; i++)
                maxReach = math.max(maxReach, _bodies[i].Radius);
            for (int i = 0; i < rocksN; i++)
                maxReach = math.max(maxReach, _rocks[i].Radius);
            float cell = math.max(8f, maxReach);
            int cellsX = math.max(1, (int)math.ceil(mapW / cell));
            int cellsZ = math.max(1, (int)math.ceil(mapH / cell));
            if (rocksN > _binCapacity)
            {
                _bins.Dispose();
                _binCapacity = rocksN * 2;
                _bins = new NativeParallelMultiHashMap<int, int>(_binCapacity, Allocator.Persistent);
            }

            _bins.Clear();
            for (int i = 0; i < rocksN; i++)
            {
                CellCoords(_rocks[i].Position, cell, cellsX, cellsZ, mapW, mapH, out int cx, out int cz);
                _bins.Add(cz * cellsX + cx, i);
            }

            for (int i = 0; i < shipsN; i++)
            {
                if (_bodies[i].Write == 0)
                    continue;
                float3 shipPos = _bodies[i].Position + _deltaP[i];
                CellCoords(shipPos, cell, cellsX, cellsZ, mapW, mapH, out int cx, out int cz);
                _asteroidOrder.Clear();
                for (int dz = -1; dz <= 1; dz++)
                {
                    int nz = Mod(cz + dz, cellsZ);
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int key = nz * cellsX + Mod(cx + dx, cellsX);
                        if (!_bins.TryGetFirstValue(key, out int rockIndex, out var it))
                            continue;
                        do
                        {
                            _asteroidOrder.Add(rockIndex);
                        }
                        while (_bins.TryGetNextValue(out rockIndex, ref it));
                    }
                }

                SortAsteroidOrder(ref _asteroidOrder);
                for (int n = 0; n < _asteroidOrder.Length; n++)
                {
                    TryAsteroidPair(
                        i, _asteroidOrder[n], mapW, mapH, restitution,
                        megas, motors, ships, loadouts, territory, contacts);
                }
            }
        }

        /// <summary>
        /// Orders this ship's neighbor rocks by canonical XZ before separation.
        /// Each rock sees the shove the previous rock already wrote, so walk order
        /// is part of the result. Position matches across server and client.
        /// </summary>
        void SortAsteroidOrder(ref NativeList<int> order)
        {
            for (int i = 1; i < order.Length; i++)
            {
                int index = order[i];
                int j = i - 1;
                while (j >= 0 && AsteroidIsBefore(_rocks[index], _rocks[order[j]]))
                {
                    order[j + 1] = order[j];
                    j--;
                }

                order[j + 1] = index;
            }
        }

        static bool AsteroidIsBefore(in AsteroidTickBody a, in AsteroidTickBody b)
        {
            const float Epsilon = 0.001f;
            if (a.Position.x < b.Position.x - Epsilon)
                return true;
            if (a.Position.x > b.Position.x + Epsilon)
                return false;
            if (a.Position.z < b.Position.z - Epsilon)
                return true;
            if (a.Position.z > b.Position.z + Epsilon)
                return false;
            if (a.Entity.Index != b.Entity.Index)
                return a.Entity.Index < b.Entity.Index;
            return a.Entity.Version < b.Entity.Version;
        }

        void TryAsteroidPair(
            int shipIndex,
            int rockIndex,
            float mapW,
            float mapH,
            float restitution,
            ComponentLookup<MegaShipState> megas,
            ComponentLookup<ShipMotorConfig> motors,
            ComponentLookup<ShipState> ships,
            ComponentLookup<ShipLoadoutState> loadouts,
            ComponentLookup<ShipTerritoryBoostLatch> territory,
            ComponentLookup<ShipAsteroidContactState> contacts)
        {
            ShipTickBody ship = _bodies[shipIndex];
            AsteroidTickBody rock = _rocks[rockIndex];
            float3 shipPos = ship.Position + _deltaP[shipIndex];
            float3 offset = ToroidalMapEcs.ShortestOffsetXZ(rock.Position, shipPos, mapW, mapH);
            offset.y = 0f;
            float reach = ship.Radius + rock.Radius;
            float distSq = offset.x * offset.x + offset.z * offset.z;
            if (distSq >= reach * reach || reach <= 0f)
                return;

            // This rock's resistance was already taken on an earlier tick. The server
            // destroys it in that ram. The client copy lives until HitRpc, so a second
            // bleed here is the jerk back onto the server velocity.
            if (_clientBreaksActive && ClientRockBrokenOnEarlierTick(rock.Entity))
                return;

            float dist = math.sqrt(distSq);
            float3 normal = dist > 1e-4f ? offset / dist : new float3(1f, 0f, 0f);
            // A remote punch-through that has not cleared the rock yet must not reflect.
            // Predicted hulls clear this flag in the friction pass; remotes do not.
            if (ship.Remote != 0
                && contacts.HasComponent(ship.Entity)
                && contacts[ship.Entity].PunchThrough != 0)
            {
                _touchedRock[shipIndex] = 1;
                return;
            }

            float3 vel = ship.Velocity + _deltaV[shipIndex];
            vel.y = 0f;
            float closing = math.max(0f, -math.dot(vel, normal));
            bool mega = MegaShipCatalog.PlowsAsteroids
                        && megas.HasComponent(ship.Entity)
                        && megas[ship.Entity].IsMega;
            if (!mega)
            {
                bool wasInContact = contacts.HasComponent(ship.Entity)
                                    && contacts[ship.Entity].InContact != 0;
                float speedReduction = 0f;
                bool killingBlow = !wasInContact
                                   && closing >= ShipRammingCollisionDamageSystem.ImpactMinClosingSpeed
                                   && TryAsteroidOverkill(
                                       ship.Entity, rock.Health, math.length(vel),
                                       motors, ships, loadouts, territory, out speedReduction);
                if (killingBlow)
                {
                    float3 reduced = vel;
                    if (ShipCollisionImpulseLogic.ApplyForwardSpeedReduction(ref reduced, normal, speedReduction))
                        _deltaV[shipIndex] += reduced - vel;
                    if (contacts.HasComponent(ship.Entity))
                    {
                        ShipAsteroidContactState contact = contacts[ship.Entity];
                        contact.PunchThrough = 1;
                        contacts[ship.Entity] = contact;
                    }

                    // The server ram measures speed after this bleed. Consume the rock on the
                    // client only when that slower hit still destroys it, so the one
                    // health/damage slowdown is the whole resistance on both sides.
                    if (_clientBreaksActive
                        && TryAsteroidOverkill(
                            ship.Entity, rock.Health, math.length(reduced),
                            motors, ships, loadouts, territory, out _))
                        NoteClientAsteroidBreak(rock.Entity, _simTick);
                }
                else
                {
                    float3 bounced = vel;
                    if (ShipCollisionImpulseLogic.ApplyInfiniteMassWallImpulse(ref bounced, normal, restitution))
                        _deltaV[shipIndex] += bounced - vel;
                    float depth = math.max(0f, reach - dist) + ShipCollisionImpulseLogic.ShipShipDepenetrationSkin;
                    depth = math.min(depth, ShipCollisionImpulseLogic.ShipShipTickMaxSeparation);
                    _deltaP[shipIndex] += normal * depth;
                }
            }

            _touchedRock[shipIndex] = 1;
            _asteroidContacts.Add(new ShipTickAsteroidContactElement
            {
                Ship = ship.Entity,
                Asteroid = rock.Entity,
                NormalShipFromOther = normal,
                ClosingSpeed = closing,
            });
        }

        /// <summary>
        /// Drops break records when prediction rewinds, and forgets rocks that despawned.
        /// A rewind must replay the killing tick; records from the discarded future would
        /// skip that blow.
        /// </summary>
        void TrackClientAsteroidBreaks(EntityManager em, uint simTick)
        {
            if (_hasPredictTick)
            {
                int since = new NetworkTick(simTick).TicksSince(new NetworkTick(_lastPredictTick));
                if (since < 0)
                    _clientBrokenRocks.Clear();
            }

            _lastPredictTick = simTick;
            _hasPredictTick = true;
            if (_clientBrokenRocks.Count == 0)
                return;

            _clientBrokenDrop.Clear();
            foreach (var kv in _clientBrokenRocks)
            {
                if (!em.Exists(kv.Key))
                    _clientBrokenDrop.Add(kv.Key);
            }

            for (int i = 0; i < _clientBrokenDrop.Length; i++)
                _clientBrokenRocks.Remove(_clientBrokenDrop[i]);
        }

        /// <summary>True when this rock's killing ram was already applied on an earlier tick.</summary>
        bool ClientRockBrokenOnEarlierTick(Entity rock)
        {
            if (!_clientBrokenRocks.TryGetValue(rock, out uint brokenTick))
                return false;
            return new NetworkTick(_simTick).TicksSince(new NetworkTick(brokenTick)) > 0;
        }

        /// <summary>Remembers the earliest tick that bled this rock. A later replay keeps that tick.</summary>
        void NoteClientAsteroidBreak(Entity rock, uint tick)
        {
            if (_clientBrokenRocks.TryGetValue(rock, out uint existing)
                && new NetworkTick(tick).TicksSince(new NetworkTick(existing)) >= 0)
                return;

            if (_clientBrokenRocks.Count + 1 > _clientBrokenRocks.Capacity)
            {
                var grown = new NativeHashMap<Entity, uint>(
                    math.max(8, _clientBrokenRocks.Capacity * 2), Allocator.Persistent);
                foreach (var kv in _clientBrokenRocks)
                    grown.TryAdd(kv.Key, kv.Value);
                _clientBrokenRocks.Dispose();
                _clientBrokenRocks = grown;
            }

            _clientBrokenRocks[rock] = tick;
        }

        /// <summary>
        /// True when this first contact's ram damage exceeds the rock's health.
        /// Later ticks of the same push bounce instead, matching the old contact-enter rule.
        /// </summary>
        static bool TryAsteroidOverkill(
            Entity ship,
            float asteroidHealth,
            float currentSpeed,
            ComponentLookup<ShipMotorConfig> motors,
            ComponentLookup<ShipState> ships,
            ComponentLookup<ShipLoadoutState> loadouts,
            ComponentLookup<ShipTerritoryBoostLatch> territory,
            out float speedReduction)
        {
            speedReduction = 0f;
            if (!motors.HasComponent(ship) || !ships.HasComponent(ship))
                return false;

            float bankMul = 1f;
            if (loadouts.HasComponent(ship))
            {
                int bank = BulletBankFireResolve.ResolveFireBankIndex(loadouts[ship]);
                bankMul = BulletBankCombatLogic.GetRammingPowerMultiplier(bank);
            }

            float territoryMult = 1f;
            if (territory.HasComponent(ship) && territory[ship].LatchedMult > 1f)
                territoryMult = territory[ship].LatchedMult;

            float damage = ShipRammingCollisionDamageSystem.ComputeAsteroidImpactDamage(
                ships[ship], motors[ship], currentSpeed, territoryMult, bankMul);
            if (!(damage > 0.01f) || !(asteroidHealth < damage))
                return false;

            speedReduction = asteroidHealth / damage;
            return true;
        }

        static void CellCoords(
            float3 position,
            float cell,
            int cellsX,
            int cellsZ,
            float mapW,
            float mapH,
            out int cx,
            out int cz)
        {
            cx = Mod((int)math.floor((position.x + mapW * 0.5f) / cell), cellsX);
            cz = Mod((int)math.floor((position.z + mapH * 0.5f) / cell), cellsZ);
        }

        static int Mod(int value, int modulus)
        {
            int remainder = value % modulus;
            return remainder < 0 ? remainder + modulus : remainder;
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
            float3 integratedVelocity = default,
            bool remote = false,
            bool usePose = false,
            float3 posePosition = default,
            float3 poseVelocity = default)
        {
            float3 velocity = integratedVelocity;
            velocity.y = 0f;
            if (usePose)
            {
                velocity = poseVelocity;
                velocity.y = 0f;
            }
            else if (!integrate)
            {
                velocity = float3.zero;
                if (kinematics.HasComponent(entity))
                {
                    velocity = kinematics[entity].Velocity;
                    velocity.y = 0f;
                }
            }

            float3 start = usePose ? posePosition : transform.Position;
            start.y = 0f;
            float3 position = start;
            if (usePose)
            {
                // The remote motor already stepped this tick. The start pose is one
                // step back so a center that crossed still bounces on the way in.
                start = ToroidalMapEcs.Wrap(posePosition - velocity * dt, mapW, mapH);
                position = posePosition;
                position.y = 0f;
            }
            else if (integrate)
            {
                position += velocity * dt;
                position.y = 0f;
                position = ToroidalMapEcs.Wrap(position, mapW, mapH);
            }

            bodies.Add(new ShipTickBody
            {
                Entity = entity,
                Position = position,
                PrePosition = start,
                Velocity = velocity,
                Mass = RammingMass(entity, motors, ships, mobility),
                Radius = HullRadius(entity, transform.Scale, radii, colliders),
                NetworkId = networkId,
                Write = write ? (byte)1 : (byte)0,
                Remote = remote ? (byte)1 : (byte)0,
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

        /// <summary>
        /// Planet and moon circles for remote hulls. Unity Physics does not move those
        /// kinematic ghosts, so the same wall reflect the predicted hull gets from the
        /// contact stream is applied here. Map size is the caller's resolved torus.
        /// </summary>
        /// <param name="state">Required so this helper's planet query can complete its dependency.</param>
        void SolveRemoteWorldWalls(
            ref SystemState state,
            float mapW,
            float mapH,
            float restitution,
            double elapsed,
            ComponentLookup<ShipState> ships)
        {
            _ = state.EntityManager;
            bool anyRemote = false;
            for (int i = 0; i < _bodies.Length; i++)
            {
                if (_bodies[i].Remote != 0 && _bodies[i].Write != 0)
                {
                    anyRemote = true;
                    break;
                }
            }

            if (!anyRemote)
                return;

            _walls.Clear();
            foreach (var (planet, transform, moon) in SystemAPI
                         .Query<RefRO<PlanetState>, RefRO<LocalTransform>, RefRO<PlanetGemMoonState>>()
                         .WithAll<PlanetTag>())
            {
                float planetSize = math.max(0.25f, transform.ValueRO.Scale);
                bool home = planet.ValueRO.IsHomePlanet;
                float3 planetPos = transform.ValueRO.Position;
                _walls.Add(new WorldWallBody
                {
                    Position = planetPos,
                    BodyRadius = BodyCollisionMath.GetPlanetBodyRadiusWorld(planetSize),
                    ShieldRadius = 0f,
                    Owner = TeamId.None,
                });

                float shield = moon.ValueRO.CurrentShield > 0.001f
                    ? PlanetGemMoonMath.GetMoonShieldOuterRadiusWorld(planetSize, home)
                    : 0f;
                _walls.Add(new WorldWallBody
                {
                    Position = planetPos + PlanetGemMoonMath.GetMoonOrbitOffset(
                        planetSize,
                        planet.ValueRO.PlanetLevel,
                        home,
                        planet.ValueRO.PlanetId,
                        elapsed),
                    BodyRadius = PlanetGemMoonMath.GetMoonBodyRadiusWorld(planetSize, home),
                    ShieldRadius = shield,
                    Owner = planet.ValueRO.Ownership,
                });
            }

            for (int i = 0; i < _bodies.Length; i++)
            {
                if (_bodies[i].Remote == 0 || _bodies[i].Write == 0)
                    continue;

                TeamId team = ships.HasComponent(_bodies[i].Entity)
                    ? ships[_bodies[i].Entity].Team
                    : TeamId.None;
                for (int w = 0; w < _walls.Length; w++)
                    TryWorldWall(i, _walls[w], team, mapW, mapH, restitution);
            }
        }

        void TryWorldWall(
            int shipIndex,
            in WorldWallBody wall,
            TeamId team,
            float mapW,
            float mapH,
            float restitution)
        {
            bool hostileShield = wall.ShieldRadius > wall.BodyRadius
                                 && wall.Owner != TeamId.None
                                 && wall.Owner != team;
            float radius = hostileShield ? wall.ShieldRadius : wall.BodyRadius;
            float3 shipPos = _bodies[shipIndex].Position + _deltaP[shipIndex];
            float3 offset = ToroidalMapEcs.ShortestOffsetXZ(wall.Position, shipPos, mapW, mapH);
            offset.y = 0f;
            float reach = _bodies[shipIndex].Radius + radius;
            float distSq = offset.x * offset.x + offset.z * offset.z;
            if (distSq >= reach * reach || reach <= 0f)
                return;

            float dist = math.sqrt(distSq);
            float3 normal = dist > 1e-4f ? offset / dist : new float3(1f, 0f, 0f);
            float3 vel = _bodies[shipIndex].Velocity + _deltaV[shipIndex];
            vel.y = 0f;
            float3 bounced = vel;
            if (ShipCollisionImpulseLogic.ApplyInfiniteMassWallImpulse(ref bounced, normal, restitution))
                _deltaV[shipIndex] += bounced - vel;
            float depth = math.max(0f, reach - dist) + ShipCollisionImpulseLogic.ShipShipDepenetrationSkin;
            depth = math.min(depth, ShipCollisionImpulseLogic.ShipShipTickMaxSeparation);
            _deltaP[shipIndex] += normal * depth;
        }

        /// <summary>
        /// Remotes are not in the Simulate friction pass, so the next motor step would
        /// never see the rock normal. Punch-through stays clear of that stick until the
        /// hull leaves the rock.
        /// </summary>
        void PublishRemoteAsteroidContact(
            int shipIndex,
            Entity ship,
            ComponentLookup<ShipAsteroidContactState> contacts)
        {
            if (!contacts.HasComponent(ship))
                return;

            ShipAsteroidContactState contact = contacts[ship];
            bool touched = shipIndex < _touchedRock.Length && _touchedRock[shipIndex] != 0;
            if (!touched)
            {
                contacts[ship] = default;
                return;
            }

            if (contact.PunchThrough != 0)
            {
                contact.InContact = 0;
                contacts[ship] = contact;
                return;
            }

            float3 normal = new float3(0f, 0f, 1f);
            for (int c = _asteroidContacts.Length - 1; c >= 0; c--)
            {
                if (_asteroidContacts[c].Ship != ship)
                    continue;
                normal = _asteroidContacts[c].NormalShipFromOther;
                break;
            }

            normal.y = 0f;
            contact.InContact = 1;
            contact.OutwardNormal = math.lengthsq(normal) > 1e-8f ? math.normalize(normal) : new float3(0f, 0f, 1f);
            contacts[ship] = contact;
        }

        /// <summary>
        /// Pose a remote hull had at the start of <paramref name="tick"/>, before that
        /// tick's bounce. History keeps the post-bounce pose, so this steps forward one
        /// tick from the previous slot. Map size is the caller's resolved torus.
        /// </summary>
        static bool TryReplayRemotePose(
            BufferLookup<ShipRemoteMotorHistoryElement> historyLookup,
            Entity entity,
            uint tick,
            float dt,
            float mapW,
            float mapH,
            ref float3 position,
            ref float3 velocity)
        {
            if (tick == 0 || dt <= 0f || !historyLookup.HasBuffer(entity))
                return false;

            DynamicBuffer<ShipRemoteMotorHistoryElement> history = historyLookup[entity];
            int capacity = ShipRemoteMotorHistoryElement.Capacity;
            if (history.Length != capacity)
                return false;

            uint prev = tick - 1;
            ShipRemoteMotorHistoryElement entry = history[(int)(prev % (uint)capacity)];
            if (entry.Valid == 0 || entry.Tick != prev)
                return false;

            velocity = new float3(entry.VX, 0f, entry.VZ);
            position = new float3(entry.X, 0f, entry.Z) + velocity * dt;
            position.y = 0f;
            position = ToroidalMapEcs.Wrap(position, mapW, mapH);
            return true;
        }

        /// <summary>
        /// Rewrites the history slot for this integrated tick so the next flight sample
        /// scores the bounced pose, not the pose from before the wall.
        /// </summary>
        static void PatchRemoteHistory(DynamicBuffer<ShipRemoteMotorHistoryElement> history, in ShipRemoteMotorState motor)
        {
            int capacity = ShipRemoteMotorHistoryElement.Capacity;
            if (history.Length != capacity)
                return;

            int slot = (int)(motor.IntegratedTick % (uint)capacity);
            ShipRemoteMotorHistoryElement entry = history[slot];
            if (entry.Valid == 0 || entry.Tick != motor.IntegratedTick)
                return;

            entry.X = motor.Position.x;
            entry.Z = motor.Position.z;
            entry.VX = motor.Velocity.x;
            entry.VZ = motor.Velocity.z;
            // This slot's pose is the bounce, not the cruise step that arrived here.
            entry.AppliedX = 0f;
            entry.AppliedZ = 0f;
            entry.AppliedYaw = 0f;
            entry.AppliedVX = 0f;
            entry.AppliedVZ = 0f;
            history[slot] = entry;
        }

        struct ShipTickBody
        {
            public Entity Entity;
            public float3 Position;
            /// <summary>Pose at the start of this tick, before <see cref="Position"/>.</summary>
            public float3 PrePosition;
            public float3 Velocity;
            public float Mass;
            public float Radius;
            public int NetworkId;
            public byte Write;
            /// <summary>1 for a client remote motor hull. 0 for a predicted or server hull.</summary>
            public byte Remote;
        }

        struct AsteroidTickBody
        {
            public Entity Entity;
            public float3 Position;
            public float Radius;
            public float Health;
        }

        struct WorldWallBody
        {
            public float3 Position;
            public float BodyRadius;
            public float ShieldRadius;
            public TeamId Owner;
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
    /// One analytic ship-vs-asteroid overlap this tick. Replaces the PhysX asteroid
    /// event so ram, friction, and plow see the same pair the kinematic tick bounced.
    /// </summary>
    public struct ShipTickAsteroidContactElement : IBufferElementData
    {
        public Entity Ship;
        public Entity Asteroid;
        public float3 NormalShipFromOther;
        public float ClosingSpeed;
    }

    /// <summary>
    /// Pose the kinematic tick wrote for a ship that touched a rock. Physics must not
    /// replace it — that replacement was the snapshot the client snapped back to.
    /// </summary>
    public struct ShipTickAsteroidPoseElement : IBufferElementData
    {
        public Entity Ship;
        public float3 Position;
        public quaternion Rotation;
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
    /// After physics, put asteroid-touching hulls back on the pose the kinematic tick wrote.
    /// A PhysX depenetration must not become the ghost snapshot.
    /// </summary>
    [UpdateInGroup(typeof(AfterPhysicsSystemGroup), OrderFirst = true)]
    [UpdateBefore(typeof(ShipCollisionBounceSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    public partial struct ShipTickAsteroidPoseLockSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<ShipTickMotionTag>();
        }

        public void OnUpdate(ref SystemState state)
        {
            if (state.World.IsClient() && ClientJoinSettleCache.ShouldSkipShipSimulation)
                return;
            if (!SystemAPI.TryGetSingletonBuffer<ShipTickAsteroidPoseElement>(out var poses) || poses.Length == 0)
                return;

            // OrderFirst in AfterPhysics, before ShipPhysicsContactCollectSystem syncs the step.
            // ExportDynamicBodiesJob is still the registered writer of LocalTransform. A live
            // match fills this buffer as soon as a hull touches a rock; reading it on the main
            // thread without completing that job aborts client prediction.
            state.Dependency.Complete();
            state.EntityManager.CompleteDependencyBeforeRW<LocalTransform>();

            var transforms = SystemAPI.GetComponentLookup<LocalTransform>(false);
            for (int i = 0; i < poses.Length; i++)
            {
                ShipTickAsteroidPoseElement pose = poses[i];
                if (!transforms.HasComponent(pose.Ship))
                    continue;
                LocalTransform lt = transforms[pose.Ship];
                lt.Position = pose.Position;
                lt.Rotation = pose.Rotation;
                transforms[pose.Ship] = lt;
            }
        }
    }

    /// <summary>
    /// Copies this tick's analytic overlaps into the classified contact buffer.
    /// PhysX asteroid events are dropped so ram and friction follow the kinematic pairs.
    /// Ship-vs-ship copies stay server-only. Asteroid copies run on the predicting client too.
    /// </summary>
    [UpdateInGroup(typeof(AfterPhysicsSystemGroup))]
    [UpdateAfter(typeof(ShipPhysicsContactCollectSystem))]
    [UpdateBefore(typeof(ShipRamPhysicsContactCollectSystem))]
    [UpdateBefore(typeof(ShipCollisionBounceSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    public partial struct ShipTickOverlapPublishSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<ShipTickMotionTag>();
            state.RequireForUpdate<ShipPhysicsContactQueueTag>();
        }

        public void OnUpdate(ref SystemState state)
        {
            if (state.World.IsClient() && ClientJoinSettleCache.ShouldSkipShipSimulation)
                return;
            if (!SystemAPI.TryGetSingletonBuffer<ShipPhysicsContactElement>(out var pairs))
                return;

            for (int i = pairs.Length - 1; i >= 0; i--)
            {
                if (pairs[i].Kind == ShipPhysicsContactKind.Asteroid)
                    pairs.RemoveAt(i);
            }

            if (!state.World.IsClient()
                && SystemAPI.TryGetSingletonBuffer<ShipTickOverlapElement>(out var overlaps))
            {
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

            if (!SystemAPI.TryGetSingletonBuffer<ShipTickAsteroidContactElement>(out var asteroidContacts))
                return;

            for (int i = 0; i < asteroidContacts.Length; i++)
            {
                ShipTickAsteroidContactElement contact = asteroidContacts[i];
                pairs.Add(new ShipPhysicsContactElement
                {
                    Ship = contact.Ship,
                    Other = contact.Asteroid,
                    NormalShipFromOther = contact.NormalShipFromOther,
                    ClosingSpeed = contact.ClosingSpeed,
                    Kind = ShipPhysicsContactKind.Asteroid,
                });
            }
        }
    }
}
