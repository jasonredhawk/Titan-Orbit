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
    /// On the client, an interpolated enemy does not take its half of the position
    /// shove — that half is left to the server snapshot so prediction does not walk
    /// the local hull out and get pulled back. Map size comes from
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
        NativeList<ShipTickAsteroidContactElement> _asteroidContacts;
        NativeList<ShipTickAsteroidPoseElement> _asteroidPoses;
        NativeList<byte> _touchedRock;
        NativeParallelMultiHashMap<int, int> _bins;
        int _binCapacity;
        bool _clientOneSidedOverlap;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<ShipTag>();
            _bodies = new NativeList<ShipTickBody>(32, Allocator.Persistent);
            _deltaV = new NativeList<float3>(32, Allocator.Persistent);
            _deltaP = new NativeList<float3>(32, Allocator.Persistent);
            _holds = new NativeList<ShipTickHoldElement>(32, Allocator.Persistent);
            _overlaps = new NativeList<ShipTickOverlapElement>(32, Allocator.Persistent);
            _rocks = new NativeList<AsteroidTickBody>(256, Allocator.Persistent);
            _asteroidContacts = new NativeList<ShipTickAsteroidContactElement>(32, Allocator.Persistent);
            _asteroidPoses = new NativeList<ShipTickAsteroidPoseElement>(32, Allocator.Persistent);
            _touchedRock = new NativeList<byte>(32, Allocator.Persistent);
            _bins = new NativeParallelMultiHashMap<int, int>(128, Allocator.Persistent);
            _binCapacity = 128;

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

            if (!SystemAPI.TryGetSingletonEntity<LocalShipShipOverlapLatch>(out _))
            {
                var latchEntity = state.EntityManager.CreateEntity();
                state.EntityManager.AddComponentData(latchEntity, new LocalShipShipOverlapLatch());
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
            if (_rocks.IsCreated)
                _rocks.Dispose();
            if (_asteroidContacts.IsCreated)
                _asteroidContacts.Dispose();
            if (_asteroidPoses.IsCreated)
                _asteroidPoses.Dispose();
            if (_touchedRock.IsCreated)
                _touchedRock.Dispose();
            if (_bins.IsCreated)
                _bins.Dispose();
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

            _bodies.Clear();
            _clientOneSidedOverlap = false;
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

            if (isClient)
                PublishClientShipOverlapLatch(ref state);

            FlushScratch(ref state);
        }

        /// <summary>
        /// Client display eases while this is set. A one-sided shove no longer walks
        /// the hull out, but the snapshot can still step the pose. The latch tells
        /// presentation not to raw-follow that step.
        /// </summary>
        void PublishClientShipOverlapLatch(ref SystemState state)
        {
            if (!SystemAPI.TryGetSingletonRW<LocalShipShipOverlapLatch>(out var latch))
                return;

            double now = SystemAPI.Time.ElapsedTime;
            if (_clientOneSidedOverlap)
            {
                latch.ValueRW.Active = 1;
                latch.ValueRW.LastTime = now;
                return;
            }

            if (latch.ValueRO.Active != 0 && now - latch.ValueRO.LastTime > 0.15)
                latch.ValueRW.Active = 0;
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
            float reach = a.Radius + b.Radius;
            float distSq = offset.x * offset.x + offset.z * offset.z;
            if (distSq >= reach * reach)
                return;

            float dist = math.sqrt(distSq);
            float3 normal = dist > 1e-4f ? offset / dist : new float3(1f, 0f, 0f);
            ShipCollisionImpulseLogic.ComputeDeterministicShipShip(
                a.Velocity, b.Velocity, normal, a.Mass, b.Mass, reach - dist,
                out float3 dVa, out float3 dVb, out float3 dPa, out float3 dPb,
                out float closing);

            // The server writes both halves. A predicting client only owns the local
            // hull; the enemy stays on the interpolated pose. Applying the local
            // position half anyway walks the hull out, and the next snapshot pulls
            // it back. Keep the velocity impulse. Leave the position shove to the
            // server pose while that pair is one-sided.
            bool oneSided = a.Write != b.Write;
            if (oneSided)
                _clientOneSidedOverlap = true;

            if (a.Write != 0)
            {
                _deltaV[i] += dVa;
                if (!oneSided)
                    _deltaP[i] += dPa;
            }

            if (b.Write != 0)
            {
                _deltaV[j] += dVb;
                if (!oneSided)
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
                            TryAsteroidPair(
                                i, rockIndex, mapW, mapH, restitution,
                                megas, motors, ships, loadouts, territory, contacts);
                        }
                        while (_bins.TryGetNextValue(out rockIndex, ref it));
                    }
                }
            }
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

            float dist = math.sqrt(distSq);
            float3 normal = dist > 1e-4f ? offset / dist : new float3(1f, 0f, 0f);
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

        struct AsteroidTickBody
        {
            public Entity Entity;
            public float3 Position;
            public float Radius;
            public float Health;
        }
    }

    /// <summary>Singleton for the per-tick hold and ship-ship overlap buffers.</summary>
    public struct ShipTickMotionTag : IComponentData
    {
    }

    /// <summary>
    /// Client-only. Active while the local hull is overlapping an interpolated enemy,
    /// plus a short tail so the display ease can finish.
    /// </summary>
    public struct LocalShipShipOverlapLatch : IComponentData
    {
        public byte Active;
        public double LastTime;
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
            if (!SystemAPI.TryGetSingletonBuffer<ShipTickAsteroidPoseElement>(out var poses))
                return;

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
