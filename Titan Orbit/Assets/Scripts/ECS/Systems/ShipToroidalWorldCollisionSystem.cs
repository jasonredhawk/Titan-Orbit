using TitanOrbit;
using TitanOrbit.Data;
using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Physics;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// One torus hull-collision path: ship↔planet / asteroid / moon and ship↔ship
    /// (same-tile and seams). PhysX integrates position only — it does not bounce hulls.
    /// This job owns bounce, grip, contact-reject stamps, and ram queue so server and
    /// predicted client write the same mass-share. Client skips map gathers under
    /// <see cref="ClientJoinSettleCache.ShouldSkipMapBodyQueries"/> and remote-ship gathers
    /// under <see cref="ClientJoinSettleCache.ShouldSkipShipEntityQueries"/>.
    /// Pipeline: Drive → PhysX integrate → Wrap → ToroidalWorldCollision (this) → Planar →
    /// KinematicsSync. World bodies are hashed into toroidal cells so this is not
    /// O(ships × asteroids) every predicted tick.
    /// </summary>
    // OrderLast: after default-slot PhysicsSystemGroup. Avoid UpdateAfter(PhysicsSystemGroup) —
    // ClientWorld sorter warns when that group is not a PredictedFixedStep sibling.
    [UpdateInGroup(typeof(PredictedFixedStepSimulationSystemGroup), OrderLast = true)]
    [UpdateBefore(typeof(ShipPlanarPhysicsConstraintSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    public partial struct ShipToroidalWorldCollisionSystem : ISystem
    {
        /// <summary>One static/kinematic world sphere used as an obstacle for ship resolve.</summary>
        struct WorldSphere
        {
            /// <summary>Sim center (logical / wrapped — not display-tiled).</summary>
            public float3 Position;

            /// <summary>World-space collision radius.</summary>
            public float Radius;

            /// <summary>Obstacle entity (asteroid or planet) — used for server ramming queue.</summary>
            public Entity Entity;

            /// <summary>1 when this sphere is a living asteroid (ramming damage); 0 for planets.</summary>
            public byte IsAsteroid;

            /// <summary>Designer Size for asteroid virtual collision mass (0 for planets).</summary>
            public float AsteroidSize;

            /// <summary>
            /// Planet <c>LocalTransform.Scale</c> for orbit-ring keep-out (0 for asteroids).
            /// </summary>
            public float PlanetScale;
        }

        /// <summary>One ship snapshot for world + ship↔ship resolve (predicted or interpolated).</summary>
        struct ShipSphere
        {
            public Entity Entity;
            public float3 Position;
            public float3 Velocity;
            public float Radius;
            public float CollisionMass;
            public byte IsMega;
            public bool Dirty;
            /// <summary>1 while forced moon takeoff owns pose — skip planet/moon keep-out.</summary>
            public byte TakingOff;
            /// <summary>
            /// 1 when this hull is in the predicted physics world (<see cref="Simulate"/>).
            /// 0 = interpolated remote — kinematic obstacle for the local ship.
            /// </summary>
            public byte Simulated;
            /// <summary>1 when this tick's ship↔ship normal should be written back.</summary>
            public byte WriteContact;
            /// <summary>Outward XZ normal for <see cref="ShipShipContactState"/>.</summary>
            public float3 ContactNormal;
            /// <summary>1 when this tick's asteroid contact should be written back.</summary>
            public byte WriteAsteroidContact;
            /// <summary>Outward XZ normal for <see cref="ShipAsteroidContactState"/>.</summary>
            public float3 AsteroidContactNormal;
        }

        /// <summary>
        /// Caches that at least one ship exists before we allocate obstacle lists.
        /// </summary>
        public void OnCreate(ref SystemState state)
        {
            // Wrap is off — Unity Physics owns hull contacts. Keep this system for a later torus return.
            state.Enabled = false;
            state.RequireForUpdate<ShipTag>();
        }

        /// <summary>
        /// Collects world spheres once, then Burst-resolves each simulated living ship against them
        /// with toroidal math when Unity Physics cannot see the contact (different map tile).
        /// Ship↔ship: one mass-share path for every overlapping pair (server writes both;
        /// client writes the predicted hull only).
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // --- Map size ---
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

            // --- Designer asteroid bounce mass / restitution ---
            var asteroidSettings = TitanOrbit.Data.AsteroidSettingsCache.ResolveOrDefault();
            asteroidSettings.ClampValues();
            float asteroidFriction = asteroidSettings.Friction;
            float asteroidMassPerSize = asteroidSettings.CollisionMassPerSize;
            float asteroidBounceRestitution = asteroidSettings.BounceRestitution;

            float fixedDt = SystemAPI.Time.DeltaTime;
            if (fixedDt <= 0f)
                fixedDt = 1f / 60f;

            var planetStateLookup = SystemAPI.GetComponentLookup<PlanetState>(true);
            var culledLookup = SystemAPI.GetComponentLookup<AsteroidClientCulledTag>(true);
            var simulateLookup = SystemAPI.GetComponentLookup<Simulate>(true);
            var kinematicsLookup = SystemAPI.GetComponentLookup<ShipKinematics>(true);
            var velocityLookup = SystemAPI.GetComponentLookup<PhysicsVelocity>(false);
            var transformLookup = SystemAPI.GetComponentLookup<LocalTransform>(false);
            var contactLookup = SystemAPI.GetComponentLookup<ShipShipContactState>(false);
            var asteroidContactLookup = SystemAPI.GetComponentLookup<ShipAsteroidContactState>(false);

            // --- Clear last tick's reject caches (drive reads these next fixed step) ---
            foreach (var contact in SystemAPI
                         .Query<RefRW<ShipShipContactState>>()
                         .WithAll<ShipTag, Simulate>())
                contact.ValueRW = default;
            foreach (var contact in SystemAPI
                         .Query<RefRW<ShipAsteroidContactState>>()
                         .WithAll<ShipTag, Simulate>())
                contact.ValueRW = default;

            bool skipMap = state.World.IsClient() && ClientJoinSettleCache.ShouldSkipMapBodyQueries;
            bool skipRemoteShips = state.World.IsClient() &&
                                   ClientJoinSettleCache.ShouldSkipShipEntityQueries;

            // --- Gather obstacles (no nested SystemAPI.Query) ---
            var obstacles = new NativeList<WorldSphere>(128, Allocator.TempJob);

            if (!skipMap)
            foreach (var (planetTransform, planetEntity) in SystemAPI
                         .Query<RefRO<LocalTransform>>()
                         .WithAll<PlanetTag>()
                         .WithEntityAccess())
            {
                obstacles.Add(new WorldSphere
                {
                    Position = planetTransform.ValueRO.Position,
                    Radius = BodyCollisionMath.GetPlanetBodyRadiusWorld(planetTransform.ValueRO.Scale),
                    Entity = planetEntity,
                    IsAsteroid = 0,
                    AsteroidSize = 0f,
                    PlanetScale = math.max(0.25f, planetTransform.ValueRO.Scale),
                });
            }

            // --- Asteroids (skip dead / client-culled ghosts) ---
            if (!skipMap)
            foreach (var (asteroidTransform, asteroidState, entity) in SystemAPI
                         .Query<RefRO<LocalTransform>, RefRO<AsteroidState>>()
                         .WithAll<AsteroidTag>()
                         .WithEntityAccess())
            {
                if (asteroidState.ValueRO.IsDestroyed || !(asteroidState.ValueRO.Health > 0.01f))
                    continue;
                if (culledLookup.HasComponent(entity))
                    continue;
                if (TitanOrbitDebugFlags.IsolateDisableAsteroidShipCollision)
                    continue;

                obstacles.Add(new WorldSphere
                {
                    Position = asteroidTransform.ValueRO.Position,
                    Radius = BodyCollisionMath.GetAsteroidBodyRadiusWorld(asteroidTransform.ValueRO.Scale),
                    Entity = entity,
                    IsAsteroid = 1,
                    AsteroidSize = math.max(0.01f, asteroidState.ValueRO.Size),
                });
            }

            // --- Gem-moon snapshots ---
            var moons = new NativeList<MoonObstacle>(16, Allocator.TempJob);
            double elapsed = 0.0;
            int hz = 0;
            if (SystemAPI.TryGetSingleton<ClientServerTickRate>(out var tickRate))
                hz = tickRate.SimulationTickRate;
            if (SystemAPI.TryGetSingleton<NetworkTime>(out var networkTime))
                elapsed = PlanetGemMoonOrbitClock.GetElapsedSeconds(networkTime, hz, includeTickFraction: false);
            else
                elapsed = state.World.Time.ElapsedTime;

            if (!skipMap)
            foreach (var (moonTransform, planetRef) in SystemAPI
                         .Query<RefRO<LocalTransform>, RefRO<PlanetGemMoonColliderPlanetRef>>()
                         .WithAll<PlanetGemMoonColliderTag>())
            {
                Entity planetEntity = planetRef.ValueRO.PlanetEntity;
                if (planetEntity == Entity.Null ||
                    !planetStateLookup.HasComponent(planetEntity) ||
                    !transformLookup.HasComponent(planetEntity))
                    continue;

                var planetStateData = planetStateLookup[planetEntity];
                var planetLt = transformLookup[planetEntity];
                float planetScale = math.max(0.25f, planetLt.Scale);

                moons.Add(new MoonObstacle
                {
                    CanonicalPosition = moonTransform.ValueRO.Position,
                    PlanetPosition = planetLt.Position,
                    PlanetScale = planetScale,
                    PlanetLevel = planetStateData.PlanetLevel,
                    PlanetId = planetStateData.PlanetId,
                    Radius = PlanetGemMoonMath.GetMoonBodyRadiusWorld(
                        planetScale, planetStateData.IsHomePlanet),
                });
            }

            // --- Ships: predicted hulls + interpolated remotes (client needs remotes as obstacles) ---
            var ships = new NativeList<ShipSphere>(16, Allocator.TempJob);
            if (skipRemoteShips)
            {
                foreach (var (transform, shipState, motor, mega, moonDock, shipEntity) in SystemAPI
                             .Query<RefRO<LocalTransform>, RefRO<ShipState>, RefRO<ShipMotorConfig>,
                                 RefRO<MegaShipState>, RefRO<ShipMoonDockState>>()
                             .WithAll<ShipTag, Simulate>()
                             .WithEntityAccess())
                {
                    float3 lin = velocityLookup.HasComponent(shipEntity)
                        ? velocityLookup[shipEntity].Linear
                        : float3.zero;
                    TryAddShipSphere(
                        ref ships, shipEntity, transform.ValueRO, lin,
                        shipState.ValueRO, motor.ValueRO, mega.ValueRO,
                        moonDock.ValueRO, simulated: true);
                }
            }
            else
            {
                foreach (var (transform, shipState, motor, mega, moonDock, shipEntity) in SystemAPI
                             .Query<RefRO<LocalTransform>, RefRO<ShipState>, RefRO<ShipMotorConfig>,
                                 RefRO<MegaShipState>, RefRO<ShipMoonDockState>>()
                             .WithAll<ShipTag>()
                             .WithEntityAccess())
                {
                    bool simulated = simulateLookup.HasComponent(shipEntity)
                                     && simulateLookup.IsComponentEnabled(shipEntity);
                    float3 lin = velocityLookup.HasComponent(shipEntity)
                        ? velocityLookup[shipEntity].Linear
                        : float3.zero;
                    if (!simulated && kinematicsLookup.HasComponent(shipEntity))
                        lin = kinematicsLookup[shipEntity].Velocity;
                    TryAddShipSphere(
                        ref ships, shipEntity, transform.ValueRO, lin,
                        shipState.ValueRO, motor.ValueRO, mega.ValueRO,
                        moonDock.ValueRO, simulated);
                }
            }

            if (obstacles.Length == 0 && moons.Length == 0 && ships.Length == 0)
            {
                obstacles.Dispose();
                moons.Dispose();
                ships.Dispose();
                return;
            }

            bool enqueueRam = state.World.IsServer() &&
                              SystemAPI.TryGetSingletonBuffer<PendingRamContactElement>(out _);
            var ramEvents = new NativeList<PendingRamContactElement>(16, Allocator.TempJob);
            var plowRocks = new NativeList<Entity>(8, Allocator.TempJob);

            // Broadphase: hash world bodies so each ship tests nearby cells, not the whole map.
            // [TITAN-ORBIT] After the wrap rebuild this system owns same-tile AND seams. A linear
            // scan of every asteroid × every predicted tick (including NetCode resim) tanks FPS.
            var obstacleHash = ObstacleCellHash.Build(obstacles.AsArray(), mapW, mapH, Allocator.TempJob);

            // Run() still goes through the job scheduler — lists must be TempJob, not Temp.
            new ResolveAndWriteJob
            {
                Ships = ships,
                Obstacles = obstacles.AsArray(),
                Moons = moons.AsArray(),
                ObstacleCells = obstacleHash.Cells,
                CellsX = obstacleHash.CellsX,
                CellsZ = obstacleHash.CellsZ,
                UseObstacleHash = obstacleHash.Valid,
                RamEvents = ramEvents,
                PlowRocks = plowRocks,
                Transforms = transformLookup,
                Velocities = velocityLookup,
                Contacts = contactLookup,
                AsteroidContacts = asteroidContactLookup,
                MapW = mapW,
                MapH = mapH,
                FixedDt = fixedDt,
                AsteroidFriction = asteroidFriction,
                AsteroidMassPerSize = asteroidMassPerSize,
                AsteroidBounceRestitution = asteroidBounceRestitution,
                Elapsed = elapsed,
                EnqueueRam = enqueueRam ? (byte)1 : (byte)0,
            }.Run();

            obstacleHash.Dispose();

            if (enqueueRam &&
                ramEvents.Length > 0 &&
                SystemAPI.TryGetSingletonBuffer(out DynamicBuffer<PendingRamContactElement> ramQueue))
            {
                for (int i = 0; i < ramEvents.Length; i++)
                    ramQueue.Add(ramEvents[i]);
            }

            if (state.World.IsClient() && plowRocks.Length > 0)
            {
                var seen = new NativeHashSet<Entity>(plowRocks.Length, Allocator.Temp);
                for (int i = 0; i < plowRocks.Length; i++)
                {
                    Entity rock = plowRocks[i];
                    if (!seen.Add(rock))
                        continue;
                    ClientLocalAsteroidCombatSync.SoftDestroyLocalAsteroidEntity(
                        state.EntityManager, rock);
                }

                seen.Dispose();
            }

            ramEvents.Dispose();
            plowRocks.Dispose();
            obstacles.Dispose();
            moons.Dispose();
            ships.Dispose();
        }

        /// <summary>
        /// Appends one living ship to the resolve list. Skips dead / team-select hulls.
        /// Radius is scale-based (no compound AABB).
        /// </summary>
        static void TryAddShipSphere(
            ref NativeList<ShipSphere> ships,
            Entity shipEntity,
            in LocalTransform transform,
            float3 linearVelocity,
            in ShipState shipState,
            in ShipMotorConfig motor,
            in MegaShipState mega,
            in ShipMoonDockState moonDock,
            bool simulated)
        {
            if (shipState.IsDead || shipState.AwaitingTeamSelection)
                return;

            float baseMass = motor.Mass > 0f ? motor.Mass : ShipMassLogic.DefaultBaseMass;
            float collisionMass = ShipMassLogic.ComputeRammingMass(
                motor.HullMassReference,
                shipState.MaxHealth,
                motor.ChassisReferenceHealth,
                shipState.CurrentGems,
                baseMass,
                shipState.CurrentPeople);
            if (mega.IsMega)
                collisionMass = math.max(collisionMass, MegaShipCatalog.MinHullCollisionMass);

            ships.Add(new ShipSphere
            {
                Entity = shipEntity,
                Position = transform.Position,
                Velocity = linearVelocity,
                Radius = BodyCollisionMath.GetShipHullRadiusWorld(transform.Scale),
                CollisionMass = collisionMass,
                IsMega = mega.IsMega ? (byte)1 : (byte)0,
                Dirty = false,
                TakingOff = moonDock.IsTakingOff ? (byte)1 : (byte)0,
                Simulated = simulated ? (byte)1 : (byte)0,
                WriteContact = 0,
                ContactNormal = float3.zero,
                WriteAsteroidContact = 0,
                AsteroidContactNormal = float3.zero,
            });
        }

        /// <summary>
        /// Per-planet gem-moon obstacle: canonical collider pose plus data to rebuild a Near copy
        /// for each ship on a different map tile.
        /// </summary>
        struct MoonObstacle
        {
            /// <summary>Kinematic collider LocalTransform (canonical tile).</summary>
            public float3 CanonicalPosition;

            /// <summary>Parent planet logical position (canonical).</summary>
            public float3 PlanetPosition;

            /// <summary>Planet uniform scale (world radius proxy).</summary>
            public float PlanetScale;

            /// <summary>Planet level (ring radii API).</summary>
            public int PlanetLevel;

            /// <summary>Planet id — seeds moon orbit phase.</summary>
            public int PlanetId;

            /// <summary>Moon hull radius in world units (home scale already applied).</summary>
            public float Radius;
        }

        /// <summary>
        /// Toroidal XZ cell hash of <see cref="WorldSphere"/> indices. Each body is stamped
        /// into every cell its radius covers so a ship query only needs its own hull radius.
        /// </summary>
        struct ObstacleCellHash
        {
            /// <summary>Cell edge in world units (combat bodies are larger than gems).</summary>
            public const float CellSize = 32f;

            /// <summary>Pad so a fast hull still shares a cell with a rock on the rim.</summary>
            public const float QueryPad = 2f;

            public NativeParallelMultiHashMap<int, int> Cells;
            public int CellsX;
            public int CellsZ;
            public byte Valid;

            /// <summary>
            /// Builds the hash. Empty / tiny maps stay invalid so the job linear-scans.
            /// </summary>
            public static ObstacleCellHash Build(
                NativeArray<WorldSphere> obstacles,
                float mapW,
                float mapH,
                Allocator allocator)
            {
                var hash = new ObstacleCellHash
                {
                    Cells = new NativeParallelMultiHashMap<int, int>(
                        math.max(obstacles.Length * 8, 8), allocator),
                    Valid = 0,
                };

                if (obstacles.Length == 0 || !ToroidalMapEcs.IsValidMapSize(mapW, mapH))
                    return hash;

                hash.CellsX = math.max(1, (int)math.ceil(mapW / CellSize));
                hash.CellsZ = math.max(1, (int)math.ceil(mapH / CellSize));
                hash.Valid = 1;

                for (int i = 0; i < obstacles.Length; i++)
                {
                    WorldSphere body = obstacles[i];
                    // Planets need orbit-ring keep-out, which sits just outside the body sphere.
                    float stampR = body.Radius;
                    if (body.IsAsteroid == 0 && body.PlanetScale > 0.25f)
                        stampR = math.max(stampR, body.PlanetScale * 0.65f);
                    Stamp(ref hash, body.Position, stampR, i, mapW, mapH);
                }

                return hash;
            }

            /// <summary>Unique obstacle indices in cells overlapping the ship hull.</summary>
            public void Gather(
                float3 pos,
                float radius,
                float mapW,
                float mapH,
                NativeList<int> dst,
                NativeHashSet<int> seen)
            {
                dst.Clear();
                seen.Clear();
                if (Valid == 0 || !Cells.IsCreated || radius <= 0f)
                    return;

                int cellR = (int)math.ceil((radius + QueryPad) / CellSize) + 1;
                int baseX = CellAxis(pos.x, mapW, CellsX);
                int baseZ = CellAxis(pos.z, mapH, CellsZ);

                for (int dz = -cellR; dz <= cellR; dz++)
                {
                    int cz = WrapCell(baseZ + dz, CellsZ);
                    for (int dx = -cellR; dx <= cellR; dx++)
                    {
                        int key = WrapCell(baseX + dx, CellsX) + cz * CellsX;
                        if (!Cells.TryGetFirstValue(key, out int idx, out var it))
                            continue;
                        do
                        {
                            if (seen.Add(idx))
                                dst.Add(idx);
                        } while (Cells.TryGetNextValue(out idx, ref it));
                    }
                }
            }

            public void Dispose()
            {
                if (Cells.IsCreated)
                    Cells.Dispose();
                Valid = 0;
            }

            static void Stamp(
                ref ObstacleCellHash hash,
                float3 pos,
                float radius,
                int index,
                float mapW,
                float mapH)
            {
                int cellR = (int)math.ceil((radius + QueryPad) / CellSize);
                int baseX = CellAxis(pos.x, mapW, hash.CellsX);
                int baseZ = CellAxis(pos.z, mapH, hash.CellsZ);
                if (cellR <= 0)
                {
                    hash.Cells.Add(baseX + baseZ * hash.CellsX, index);
                    return;
                }

                for (int dz = -cellR; dz <= cellR; dz++)
                {
                    int cz = WrapCell(baseZ + dz, hash.CellsZ);
                    for (int dx = -cellR; dx <= cellR; dx++)
                        hash.Cells.Add(WrapCell(baseX + dx, hash.CellsX) + cz * hash.CellsX, index);
                }
            }

            static int CellAxis(float coord, float mapSize, int cellCount)
            {
                // 1D wrap into [0, mapSize) then floor onto a cell. Same period as ToroidalMapEcs.Wrap.
                float u = coord + mapSize * 0.5f;
                u = math.fmod(u, mapSize);
                if (u < 0f)
                    u += mapSize;
                int c = (int)math.floor(u / CellSize);
                return math.clamp(c, 0, cellCount - 1);
            }

            static int WrapCell(int c, int count)
            {
                if (count <= 0)
                    return 0;
                int m = c % count;
                return m < 0 ? m + count : m;
            }
        }

        /// <summary>
        /// Burst resolve of ship↔world and ship↔ship, then write-back of simulated hulls.
        /// Skips transform/velocity writes when the delta is sub-pixel so prediction
        /// does not resimulate this gather every frame while ships grind.
        /// </summary>
        [BurstCompile]
        struct ResolveAndWriteJob : IJob
        {
            /// <summary>Skip writing position when the shove is smaller than 1 mm.</summary>
            const float PosWriteEpsSq = 1e-6f;

            /// <summary>Skip writing velocity when the change is smaller than 1 cm/s.</summary>
            const float VelWriteEpsSq = 1e-4f;

            public NativeList<ShipSphere> Ships;
            [ReadOnly] public NativeArray<WorldSphere> Obstacles;
            [ReadOnly] public NativeArray<MoonObstacle> Moons;
            [ReadOnly] public NativeParallelMultiHashMap<int, int> ObstacleCells;
            public int CellsX;
            public int CellsZ;
            public byte UseObstacleHash;
            public NativeList<PendingRamContactElement> RamEvents;
            public NativeList<Entity> PlowRocks;
            public ComponentLookup<LocalTransform> Transforms;
            public ComponentLookup<PhysicsVelocity> Velocities;
            public ComponentLookup<ShipShipContactState> Contacts;
            public ComponentLookup<ShipAsteroidContactState> AsteroidContacts;
            public float MapW;
            public float MapH;
            public float FixedDt;
            public float AsteroidFriction;
            public float AsteroidMassPerSize;
            public float AsteroidBounceRestitution;
            public double Elapsed;
            public byte EnqueueRam;

            public void Execute()
            {
                ResolveShipsVsWorld();
                ResolveShipVsShip();
                WriteSimulatedShips();
            }

            void ResolveShipsVsWorld()
            {
                var nearby = new NativeList<int>(32, Allocator.Temp);
                var seen = new NativeHashSet<int>(32, Allocator.Temp);
                var hash = new ObstacleCellHash
                {
                    Cells = ObstacleCells,
                    CellsX = CellsX,
                    CellsZ = CellsZ,
                    Valid = UseObstacleHash,
                };

                for (int s = 0; s < Ships.Length; s++)
                {
                    ShipSphere ship = Ships[s];
                    if (ship.Simulated == 0)
                        continue;

                    float3 shipPos = ship.Position;
                    float3 shipVel = ship.Velocity;
                    bool anyHit = false;

                    if (UseObstacleHash != 0)
                        hash.Gather(shipPos, ship.Radius, MapW, MapH, nearby, seen);

                    int n = UseObstacleHash != 0 ? nearby.Length : Obstacles.Length;
                    for (int nIdx = 0; nIdx < n; nIdx++)
                    {
                        int i = UseObstacleHash != 0 ? nearby[nIdx] : nIdx;
                        WorldSphere body = Obstacles[i];
                        float3 posBefore = shipPos;
                        float3 velBefore = shipVel;
                        if (MegaShipCatalog.PlowsAsteroids
                            && ship.IsMega != 0
                            && body.IsAsteroid != 0)
                        {
                            if (ShipToroidalWorldCollisionLogic.TryGetCrossSeamWorldSphereOverlap(
                                    shipPos, shipVel, ship.Radius,
                                    body.Position, body.Radius,
                                    MapW, MapH,
                                    out float3 plowNormal, out float plowClosing))
                            {
                                if (body.Entity != Entity.Null)
                                    PlowRocks.Add(body.Entity);
                                if (EnqueueRam != 0 && body.Entity != Entity.Null)
                                {
                                    RamEvents.Add(new PendingRamContactElement
                                    {
                                        Ship = ship.Entity,
                                        Other = body.Entity,
                                        OtherIsShip = 0,
                                        NormalShipFromOther = plowNormal,
                                        ClosingSpeed = plowClosing,
                                        EstimatedImpulse = plowClosing * 10f,
                                    });
                                }
                            }

                            continue;
                        }

                        float bodyFriction = body.IsAsteroid != 0 ? AsteroidFriction : 0f;
                        float bodyMass = body.IsAsteroid != 0
                            ? ShipCollisionImpulseLogic.ComputeAsteroidCollisionMass(
                                body.AsteroidSize, AsteroidMassPerSize)
                            : 0f;
                        float restitution = body.IsAsteroid != 0
                            ? AsteroidBounceRestitution
                            : ShipToroidalWorldCollisionLogic.WorldRestitution;

                        if (ship.TakingOff != 0 && body.IsAsteroid == 0)
                            continue;

                        bool megaVsPlanet = ship.IsMega != 0 && body.IsAsteroid == 0;
                        float planetKeepOut = megaVsPlanet
                            ? PlanetOrbitMath.GetPlanetCollisionKeepOut(
                                ship.Radius, body.Radius, body.PlanetScale)
                            : 0f;

                        if (ShipToroidalWorldCollisionLogic.TryResolveShipVsWorldSphere(
                                ref shipPos, ref shipVel, ship.Radius,
                                body.Position, body.Radius,
                                MapW, MapH, restitution,
                                bodyFriction, FixedDt,
                                ship.CollisionMass, bodyMass,
                                resolveSameTile: megaVsPlanet,
                                maxKeepOut: planetKeepOut))
                        {
                            anyHit = true;
                            if (body.IsAsteroid != 0 && ship.IsMega == 0)
                            {
                                float3 offsetHit = ToroidalMapEcs.ShortestOffsetXZ(
                                    posBefore, body.Position, MapW, MapH);
                                float distHit = math.length(offsetHit);
                                // Outward = body → ship (motor reject cannot push into the rock).
                                float3 outward = distHit > 1e-5f
                                    ? -offsetHit / distHit
                                    : new float3(0f, 0f, 1f);
                                ship.WriteAsteroidContact = 1;
                                ship.AsteroidContactNormal = outward;
                            }

                            if (EnqueueRam != 0 && body.IsAsteroid != 0 && body.Entity != Entity.Null)
                            {
                                float3 offset = ToroidalMapEcs.ShortestOffsetXZ(posBefore, body.Position, MapW, MapH);
                                float dist = math.length(offset);
                                float3 outward = dist > 1e-5f
                                    ? offset / dist
                                    : new float3(0f, 0f, 1f);
                                float3 planarVel = new float3(velBefore.x, 0f, velBefore.z);
                                float closing = math.max(0f, -math.dot(planarVel, outward));
                                RamEvents.Add(new PendingRamContactElement
                                {
                                    Ship = ship.Entity,
                                    Other = body.Entity,
                                    OtherIsShip = 0,
                                    NormalShipFromOther = outward,
                                    ClosingSpeed = closing,
                                    EstimatedImpulse = closing * 10f,
                                });
                            }
                        }
                    }

                    for (int i = 0; i < Moons.Length; i++)
                    {
                        if (ship.TakingOff != 0)
                            break;

                        MoonObstacle moon = Moons[i];
                        float3 moonNear = PlanetOrbitMath.GetMoonWorldPositionNear(
                            shipPos,
                            moon.PlanetPosition,
                            moon.PlanetScale,
                            moon.PlanetLevel,
                            moon.PlanetId,
                            Elapsed,
                            MapW,
                            MapH);
                        if (ShipToroidalWorldCollisionLogic.TryResolveShipVsNearWorldSphere(
                                ref shipPos, ref shipVel, ship.Radius,
                                moonNear, moon.Radius,
                                ShipToroidalWorldCollisionLogic.WorldRestitution))
                        {
                            anyHit = true;
                        }
                    }

                    if (!anyHit)
                        continue;

                    ship.Position = shipPos;
                    ship.Velocity = shipVel;
                    ship.Dirty = true;
                    Ships[s] = ship;
                }

                nearby.Dispose();
                seen.Dispose();
            }

            void ResolveShipVsShip()
            {
                const float slack = ShipToroidalWorldCollisionLogic.ShipShipRadiusSlack;
                for (int i = 0; i < Ships.Length; i++)
                {
                    for (int j = i + 1; j < Ships.Length; j++)
                    {
                        ShipSphere a = Ships[i];
                        ShipSphere b = Ships[j];
                        bool aSim = a.Simulated != 0;
                        bool bSim = b.Simulated != 0;
                        if (!aSim && !bSim)
                            continue;

                        bool bothSimulated = aSim && bSim;
                        int writeI = i;
                        int writeJ = j;
                        if (!bothSimulated && !aSim)
                        {
                            (a, b) = (b, a);
                            writeI = j;
                            writeJ = i;
                        }

                        float3 posA = a.Position;
                        float3 velA = a.Velocity;
                        float3 posB = b.Position;
                        float3 velB = b.Velocity;

                        if (!ShipToroidalWorldCollisionLogic.TryResolveShipVsShip(
                                ref posA, ref velA, a.Radius * slack, a.CollisionMass,
                                ref posB, ref velB, b.Radius * slack, b.CollisionMass,
                                MapW, MapH,
                                ShipCollisionImpulseLogic.DefaultShipShipRestitution,
                                writePositionB: bothSimulated,
                                out float3 normalAFromB,
                                out float closing))
                            continue;

                        a.Position = posA;
                        a.Velocity = velA;
                        a.Dirty = true;
                        StampContact(ref a, normalAFromB);
                        Ships[writeI] = a;
                        if (bothSimulated)
                        {
                            b.Position = posB;
                            b.Velocity = velB;
                            b.Dirty = true;
                            StampContact(ref b, -normalAFromB);
                            Ships[writeJ] = b;
                        }

                        if (EnqueueRam == 0)
                            continue;

                        RamEvents.Add(new PendingRamContactElement
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
            }

            static void StampContact(ref ShipSphere ship, float3 outwardNormal)
            {
                outwardNormal.y = 0f;
                if (math.lengthsq(outwardNormal) > 1e-8f)
                    outwardNormal = math.normalize(outwardNormal);
                else
                    outwardNormal = new float3(1f, 0f, 0f);
                ship.WriteContact = 1;
                ship.ContactNormal = outwardNormal;
            }

            void WriteSimulatedShips()
            {
                for (int s = 0; s < Ships.Length; s++)
                {
                    ShipSphere ship = Ships[s];
                    if (ship.Simulated == 0)
                        continue;

                    if (ship.WriteContact != 0 && Contacts.HasComponent(ship.Entity))
                    {
                        Contacts[ship.Entity] = new ShipShipContactState
                        {
                            InContact = 1,
                            OutwardNormal = ship.ContactNormal,
                        };
                    }

                    if (ship.WriteAsteroidContact != 0 && AsteroidContacts.HasComponent(ship.Entity))
                    {
                        AsteroidContacts[ship.Entity] = new ShipAsteroidContactState
                        {
                            InContact = 1,
                            OutwardNormal = ship.AsteroidContactNormal,
                        };
                    }

                    if (!ship.Dirty)
                        continue;

                    if (Transforms.HasComponent(ship.Entity))
                    {
                        var lt = Transforms[ship.Entity];
                        if (math.distancesq(lt.Position, ship.Position) > PosWriteEpsSq)
                        {
                            lt.Position = ship.Position;
                            Transforms[ship.Entity] = lt;
                        }
                    }

                    if (Velocities.HasComponent(ship.Entity))
                    {
                        var pv = Velocities[ship.Entity];
                        if (math.distancesq(pv.Linear, ship.Velocity) > VelWriteEpsSq)
                        {
                            pv.Linear = ship.Velocity;
                            Velocities[ship.Entity] = pv;
                        }
                    }
                }
            }
        }
    }
}
