using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;
using Random = Unity.Mathematics.Random;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server initialization: creates match singletons (team state, map state, bullet buffers)
    /// on first world boot. Runs once in InitializationSystemGroup before map generation.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial struct GameBootstrapSystem : ISystem
    {
        /// <summary>
        /// [ECS/DOTS] Creates match singletons and shared buffers once per server world.
        /// Skips if another bootstrap path already inserted <see cref="TeamStateSingleton"/>.
        /// </summary>
        public void OnCreate(ref SystemState state)
        {
            // [STANDARD] Idempotent — skip if another bootstrap path already created singletons.
            if (SystemAPI.HasSingleton<TeamStateSingleton>())
                return;

            // [ECS/DOTS] One entity holds multiple singleton components and shared buffers.
            var entity = state.EntityManager.CreateEntity(typeof(TeamStateSingleton), typeof(MatchStateSingleton),
                typeof(MapStateSingleton), typeof(ActiveBulletsTag));
            state.EntityManager.SetComponentData(entity, new TeamStateSingleton
            {
                ActiveTeamCount = 0,
                MaxPlayersPerTeam = 20,
            });
            state.EntityManager.SetComponentData(entity, new MatchStateSingleton());
            // [TITAN-ORBIT] Size stays 0 until map generation rolls a real period — never invent 1000×1000.
            state.EntityManager.SetComponentData(entity, new MapStateSingleton { MapWidth = 0f, MapHeight = 0f });
            state.EntityManager.AddBuffer<BulletElement>(entity);
            state.EntityManager.AddBuffer<BulletSpawnEventElement>(entity);
            state.EntityManager.AddBuffer<BulletHitEventElement>(entity);
            state.EntityManager.AddBuffer<MapLayoutEntryElement>(entity);
            // [NETCODE] Server roster only — not a ghost. Clients get names via PlayerNameAnnounceRpc.
            state.EntityManager.AddBuffer<PlayerNameElement>(entity);
        }

        public void OnUpdate(ref SystemState state) { }
    }

    /// <summary>
    /// Server: increments match elapsed time after first simulation tick. Sets MatchStarted flag.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct MatchTimerSystem : ISystem
    {
        public void OnCreate(ref SystemState state) => state.RequireForUpdate<MatchStateSingleton>();

        public void OnUpdate(ref SystemState state)
        {
            var match = SystemAPI.GetSingletonRW<MatchStateSingleton>();
            if (!match.ValueRO.MatchStarted)
            {
                match.ValueRW.MatchStarted = true;
                return;
            }
            match.ValueRW.MatchTimer += SystemAPI.Time.DeltaTime;
        }
    }

    /// <summary>
    /// Server procedural map spawn: rolls layout from <see cref="MapGenerationLogic"/>, instantiates
    /// planets and asteroids incrementally, then applies starting neutral captures one-at-a-time in
    /// round-robin order so sticky planet connections can rebuild between claims.
    /// <para>
    /// Loading contract: <see cref="MapStateSingleton.LoadingTotalSteps"/> is the planet+asteroid
    /// body count only (never claim ticks). Meta RPC waits for <c>LoadingComplete</c> so clients
    /// never latch a rolled overestimate after asteroid underfill.
    /// </para>
    /// Spawns multiple bodies per sim tick so large asteroid fields (400–800+) do not take minutes on
    /// dedicated servers or block remote clients waiting for ghost replication.
    /// Runs before <see cref="PlanetConnectionGraphSystem"/> so each claim dirties the graph in the
    /// same tick after ownership flips.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(PlanetConnectionGraphSystem))]
    public partial struct MapGenerationSystem : ISystem
    {
        /// <summary>How many planets/asteroids to instantiate per server sim tick during map build.</summary>
        const int SpawnsPerSimulationTick = 32;

        /// <summary>
        /// How many starting neutral claims to apply per sim tick.
        /// Keep at 1 so sticky non-crossing edges rebuild between captures.
        /// </summary>
        const int StartingClaimsPerSimulationTick = 1;

        enum Phase : byte
        {
            Idle,
            Spawning,
            ClaimingStartingNeutrals,
            Finalizing,
            Done,
        }

        enum SpawnKind : byte
        {
            HomePlanet,
            NeutralPlanet,
            Asteroid,
        }

        struct PendingSpawn
        {
            public SpawnKind Kind;
            public float3 Position;
            public float3 Scale;
            public TeamId Team;
            public int Level;
            public float GemValue;
            public float MaxHealth;
            public float Size;
            public byte ShipFamilyConfigIndex;

            /// <summary>
            /// Index into the neutral layout / planet-id lookup when <see cref="Kind"/> is NeutralPlanet.
            /// -1 for homes and asteroids.
            /// </summary>
            public int NeutralLayoutIndex;
        }

        Phase _phase;
        MapGenerationConfig _config;
        MapGenerationLogic.RolledParameters _rolled;
        Random _rng;
        int _spawnIndex;
        int _nextNeutralPlanetId;
        int _totalSpawnSteps;
        int _claimIndex;
        Entity _mapEntity;

        NativeList<PendingSpawn> _spawnQueue;
        NativeList<MapLayoutEntryElement> _layoutEntries;
        NativeList<MapGenerationLogic.StartingNeutralClaim> _claimQueue;
        NativeList<int> _neutralPlanetIdsByLayoutIndex;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<MapStateSingleton>();
            state.RequireForUpdate<GamePrefabs>();
        }

        public void OnDestroy(ref SystemState state)
        {
            DisposeNativeCollections();
        }

        MapGenerationConfig GetConfig(ref SystemState state)
        {
            if (MapGenerationSettingsCache.Settings != null)
                return MapGenerationConfigUtility.FromSettings(MapGenerationSettingsCache.Settings);

            if (SystemAPI.TryGetSingleton<MapGenerationConfig>(out var config))
                return config;

            return MapGenerationConfigUtility.Default();
        }

        static string DescribeConfigSource()
        {
            if (MapGenerationSettingsCache.Settings != null)
                return $"asset '{MapGenerationSettingsCache.Settings.name}' (runtime loader)";
            return "baked ECS defaults";
        }

        public void OnUpdate(ref SystemState state)
        {
            if (_phase == Phase.Done)
                return;

            if (_phase == Phase.Idle)
            {
                if (!BeginGeneration(ref state))
                    return;
                _spawnIndex = 0;
                _phase = Phase.Spawning;
            }

            switch (_phase)
            {
                case Phase.Spawning:
                    if (SpawnBatch(ref state))
                        _phase = Phase.ClaimingStartingNeutrals;
                    break;
                case Phase.ClaimingStartingNeutrals:
                    // --- One (or few) ownership flips per tick — mimics live capture timing ---
                    // [TITAN-ORBIT] PlanetConnectionGraphSystem runs after this system and rebuilds
                    // sticky edges when the ownership fingerprint changes.
                    if (ApplyStartingNeutralClaimBatch(ref state))
                        _phase = Phase.Finalizing;
                    break;
                case Phase.Finalizing:
                    FinalizeGeneration(ref state);
                    _phase = Phase.Done;
                    break;
            }
        }

        bool BeginGeneration(ref SystemState state)
        {
            // --- Reset world and roll procedural parameters ---
            var em = state.EntityManager;
            DestroyExistingPlayerShips(ref state);
            _mapEntity = SystemAPI.GetSingletonEntity<MapStateSingleton>();
            _config = GetConfig(ref state);
            UnityEngine.Debug.Log(
                $"[MapGeneration] Using settings from {DescribeConfigSource()}. " +
                $"Map {_config.MinMapSize:F0}-{_config.MaxMapSize:F0}, teams {_config.MinTeamsPerMatch}-{_config.MaxTeamsPerMatch}, " +
                $"neutrals {_config.MinNeutralPlanets}-{_config.MaxNeutralPlanets}, " +
                $"startingOwnedNeutralsPerTeam {_config.StartingOwnedNeutralPlanetsPerTeam}, " +
                $"startingRandomDefenseTurretsMax {_config.StartingRandomDefenseTurretsMax}, " +
                $"asteroids {_config.AsteroidsAtMinMapSize}-{_config.AsteroidsAtMaxMapSize}.");

            uint fallbackSeed = MapGenerationLogic.ComputeEphemeralSeed();
            _rolled = MapGenerationLogic.RollParameters(_config, fallbackSeed);
            _rng = Random.CreateFromIndex(_rolled.Seed);

            var mapState = em.GetComponentData<MapStateSingleton>(_mapEntity);
            mapState.MapWidth = _rolled.MapWidth;
            mapState.MapHeight = _rolled.MapHeight;
            mapState.BlueprintSeed = (int)_rolled.Seed;
            mapState.LoadingProgress = 0.05f;
            mapState.LoadingComplete = false;
            // --- Publish TeamCount + map size early; body counts wait for layout ---
            // [TITAN-ORBIT] Do NOT publish rolled AsteroidCount / LoadingTotalSteps as final meta yet.
            // BuildAsteroids can underfill on dense small maps; GoInGame must not latch an inflated N
            // (client Max-latch + MapSessionMetaSent blocked catch-up → Join Team hang).
            // MapSessionMetaRpc is gated on LoadingComplete — see TitanOrbitGoInGameServerSystem.
            mapState.TeamCount = _rolled.TeamCount;
            mapState.NeutralPlanetCount = 0;
            mapState.AsteroidCount = 0;
            mapState.LoadingTotalSteps = 0;
            mapState.LoadingCompletedSteps = 0;
            em.SetComponentData(_mapEntity, mapState);
            // --- ToroidalMapEcs.SetMapSize also mirrors into ToroidalMap (minimap twin) ---
            Generation.ToroidalMapEcs.SetMapSize(_rolled.MapWidth, _rolled.MapHeight);

            var teamState = SystemAPI.GetSingletonRW<TeamStateSingleton>();
            teamState.ValueRW.ActiveTeamCount = _rolled.TeamCount;
            teamState.ValueRW.TeamACount = 0;
            teamState.ValueRW.TeamBCount = 0;
            teamState.ValueRW.TeamCCount = 0;
            teamState.ValueRW.TeamDCount = 0;
            teamState.ValueRW.TeamECount = 0;
            teamState.ValueRW.EliminatedTeamsMask = 0;

            _nextNeutralPlanetId = 100;
            int estimatedEntries = _rolled.TeamCount + _rolled.NeutralPlanetCount + _rolled.AsteroidCount;
            // Capacity hint for native lists only — not the client loading denominator.
            _totalSpawnSteps = 0;
            _layoutEntries = new NativeList<MapLayoutEntryElement>(math.max(16, estimatedEntries), Allocator.Persistent);
            _spawnQueue = new NativeList<PendingSpawn>(math.max(16, estimatedEntries), Allocator.Persistent);

            var homeLayouts = new NativeList<MapGenerationLogic.HomePlanetLayout>(_rolled.TeamCount, Allocator.Temp);
            var neutralLayouts = new NativeList<MapGenerationLogic.NeutralPlanetLayout>(_rolled.NeutralPlanetCount, Allocator.Temp);
            var asteroidLayouts = new NativeList<MapGenerationLogic.AsteroidLayout>(_rolled.AsteroidCount, Allocator.Temp);
            var planetPlacements = new NativeList<MapGenerationLogic.PlanetPlacement>(estimatedEntries, Allocator.Temp);

            // --- Queue home planets, neutrals, then asteroids ---
            MapGenerationLogic.BuildHomePlanets(_config, _rolled, ref _rng, homeLayouts, planetPlacements);
            for (int i = 0; i < homeLayouts.Length; i++)
            {
                var home = homeLayouts[i];
                var team = (TeamId)(i + 1);
                _spawnQueue.Add(new PendingSpawn
                {
                    Kind = SpawnKind.HomePlanet,
                    Position = home.Position,
                    Scale = new float3(home.Scale),
                    Team = team,
                    Level = home.Level,
                    NeutralLayoutIndex = -1,
                });
            }

            MapGenerationLogic.BuildNeutralPlanets(_config, _rolled, ref _rng, planetPlacements, neutralLayouts);

            // --- Round-robin starting claims (applied after spawn, one per tick) ---
            // [TITAN-ORBIT] Neutrals spawn as TeamId.None; each team then “captures” the closest
            // available neutral to its home, one team at a time, so sticky connections form like
            // live play instead of wiring every pre-owned planet in a single graph rebuild.
            if (_claimQueue.IsCreated)
                _claimQueue.Dispose();
            _claimQueue = new NativeList<MapGenerationLogic.StartingNeutralClaim>(
                math.max(8, _config.StartingOwnedNeutralPlanetsPerTeam * _rolled.TeamCount),
                Allocator.Persistent);

            var homePositions = new NativeArray<float3>(_rolled.TeamCount, Allocator.Temp);
            for (int i = 0; i < homeLayouts.Length && i < homePositions.Length; i++)
                homePositions[i] = homeLayouts[i].Position;

            MapGenerationLogic.BuildStartingNeutralClaimOrder(
                _config.StartingOwnedNeutralPlanetsPerTeam,
                _rolled.TeamCount,
                homePositions,
                neutralLayouts,
                _rolled.MapWidth,
                _rolled.MapHeight,
                ref _rng,
                ref _claimQueue);
            homePositions.Dispose();
            _claimIndex = 0;

            if (_neutralPlanetIdsByLayoutIndex.IsCreated)
                _neutralPlanetIdsByLayoutIndex.Dispose();
            _neutralPlanetIdsByLayoutIndex = new NativeList<int>(neutralLayouts.Length, Allocator.Persistent);
            for (int i = 0; i < neutralLayouts.Length; i++)
                _neutralPlanetIdsByLayoutIndex.Add(0);

            for (int i = 0; i < neutralLayouts.Length; i++)
            {
                var neutral = neutralLayouts[i];
                _spawnQueue.Add(new PendingSpawn
                {
                    Kind = SpawnKind.NeutralPlanet,
                    Position = neutral.Position,
                    Scale = new float3(neutral.Scale),
                    Level = neutral.Level,
                    // Spawn unowned — claim phase assigns Team later.
                    Team = TeamId.None,
                    NeutralLayoutIndex = i,
                    ShipFamilyConfigIndex = (byte)(1 + _rng.NextInt(0, PlanetShipFamilyAssignment.NonHomeFamilySlotCount)),
                });
            }

            var asteroidBody = ResolveAsteroidBodyTuning();
            MapGenerationLogic.BuildAsteroids(
                _config, _rolled, asteroidBody, ref _rng, planetPlacements, asteroidLayouts);
            for (int i = 0; i < asteroidLayouts.Length; i++)
            {
                var asteroid = asteroidLayouts[i];
                _spawnQueue.Add(new PendingSpawn
                {
                    Kind = SpawnKind.Asteroid,
                    Position = asteroid.Position,
                    Scale = asteroid.Scale,
                    GemValue = asteroid.GemValue,
                    MaxHealth = asteroid.MaxHealth,
                    Size = asteroid.Size,
                    NeutralLayoutIndex = -1,
                });
            }

            // --- Authoritative body counts (actual placed — not rolled targets) ---
            // [TITAN-ORBIT] LoadingTotalSteps MUST equal planet+asteroid GameObject proxy count.
            // Starting-claim ticks must NOT inflate N — proxies never count claims, and
            // B/(B+C) < 0.92 permanently blocks Join Team after underfill.
            int neutralCount = neutralLayouts.Length;
            int asteroidCount = asteroidLayouts.Length;
            int bodyCount = _spawnQueue.Length;
            _totalSpawnSteps = math.max(1, bodyCount);

            mapState = em.GetComponentData<MapStateSingleton>(_mapEntity);
            mapState.NeutralPlanetCount = neutralCount;
            mapState.AsteroidCount = asteroidCount;
            em.SetComponentData(_mapEntity, mapState);
            SetLoadingProgress(ref state, 0, _totalSpawnSteps);

            if (asteroidCount < _rolled.AsteroidCount)
            {
                UnityEngine.Debug.LogWarning(
                    $"[MapGeneration] Asteroid underfill: placed {asteroidCount}/{_rolled.AsteroidCount} " +
                    $"on {_rolled.MapWidth:F0}x{_rolled.MapHeight:F0} with {homeLayouts.Length + neutralCount} planets " +
                    $"(seed {_rolled.Seed}). LoadingSteps={_totalSpawnSteps} (bodies only).");
            }

            homeLayouts.Dispose();
            neutralLayouts.Dispose();
            asteroidLayouts.Dispose();
            planetPlacements.Dispose();

            return _spawnQueue.IsCreated;
        }

        /// <summary>Spawns up to <see cref="SpawnsPerSimulationTick"/> queued bodies this tick.</summary>
        /// <returns>True when the spawn queue is empty.</returns>
        bool SpawnBatch(ref SystemState state)
        {
            if (!_spawnQueue.IsCreated || _spawnIndex >= _spawnQueue.Length)
                return true;

            int batchEnd = math.min(_spawnQueue.Length, _spawnIndex + SpawnsPerSimulationTick);
            while (_spawnIndex < batchEnd)
            {
                SpawnQueuedEntity(ref state, _spawnQueue[_spawnIndex]);
                _spawnIndex++;
            }

            SetLoadingProgress(ref state, _spawnIndex, _totalSpawnSteps);
            return _spawnIndex >= _spawnQueue.Length;
        }

        /// <summary>
        /// Applies up to <see cref="StartingClaimsPerSimulationTick"/> starting neutral captures.
        /// Each flip changes <see cref="PlanetState.Ownership"/> so the connection graph rebuilds
        /// sticky edges as if players captured planets over time.
        /// </summary>
        /// <returns>True when every queued starting claim has been applied.</returns>
        bool ApplyStartingNeutralClaimBatch(ref SystemState state)
        {
            if (!_claimQueue.IsCreated || _claimIndex >= _claimQueue.Length)
            {
                // Bodies already spawned — keep LoadingTotalSteps = body count (not claim ticks).
                SetLoadingProgress(ref state, _totalSpawnSteps, _totalSpawnSteps);
                return true;
            }

            int batchEnd = math.min(_claimQueue.Length, _claimIndex + StartingClaimsPerSimulationTick);
            var claimEcb = new EntityCommandBuffer(Allocator.Temp);
            while (_claimIndex < batchEnd)
            {
                var claim = _claimQueue[_claimIndex];
                _claimIndex++;

                if (claim.Team == TeamId.None ||
                    claim.NeutralLayoutIndex < 0 ||
                    !_neutralPlanetIdsByLayoutIndex.IsCreated ||
                    claim.NeutralLayoutIndex >= _neutralPlanetIdsByLayoutIndex.Length)
                    continue;

                int planetId = _neutralPlanetIdsByLayoutIndex[claim.NeutralLayoutIndex];
                if (planetId == 0)
                    continue;

                // --- Find the spawned planet ghost by PlanetId and flip ownership ---
                bool applied = false;
                int claimedLevel = 1;
                foreach (var (planet, planetEntity) in SystemAPI
                             .Query<RefRW<PlanetState>>()
                             .WithAll<PlanetTag>()
                             .WithEntityAccess())
                {
                    if (planet.ValueRO.PlanetId != planetId)
                        continue;
                    if (planet.ValueRO.IsHomePlanet)
                        break;

                    planet.ValueRW.Ownership = claim.Team;
                    claimedLevel = math.max(1, planet.ValueRO.PlanetLevel);
                    applied = true;

                    // [TITAN-ORBIT] Starting claims get fresh empty defense pads, then optional
                    // random starter turrets from Map Generation Settings (0 = leave empty).
                    PlanetaryDefenseSlotSyncSystem.WipeSlotsForOwnershipChange(
                        state.EntityManager, planetEntity, claim.Team, claimedLevel);

                    if (_config.StartingRandomDefenseTurretsMax > 0 &&
                        state.EntityManager.HasBuffer<PlanetaryDefenseSlotElement>(planetEntity))
                    {
                        var defenseBuffer = state.EntityManager
                            .GetBuffer<PlanetaryDefenseSlotElement>(planetEntity);
                        PlanetaryDefenseLogic.SeedRandomStartingTurrets(
                            defenseBuffer,
                            ref _rng,
                            _config.StartingRandomDefenseTurretsMax,
                            claimedLevel,
                            PlanetaryDefenseConfig.LoadDefault());
                    }

                    // Keep layout buffer metadata in sync for session / lobby consumers.
                    for (int i = 0; i < _layoutEntries.Length; i++)
                    {
                        var entry = _layoutEntries[i];
                        if (entry.PlanetId != planetId)
                            continue;
                        entry.Team = claim.Team;
                        _layoutEntries[i] = entry;
                        break;
                    }

                    break;
                }

                if (!applied)
                {
                    UnityEngine.Debug.LogWarning(
                        $"[MapGeneration] Starting claim skipped — planetId {planetId} for {claim.Team} not found.");
                }
                else
                {
                    // [TITAN-ORBIT] Same immediate client notify as live captures — starting neutrals
                    // must wire sticky lines without waiting on rate-limited planet ghosts.
                    PlanetOwnershipNetNotify.Send(
                        ref claimEcb,
                        planetId,
                        claim.Team,
                        0,
                        claimedLevel);
                }
            }

            claimEcb.Playback(state.EntityManager);
            claimEcb.Dispose();

            // --- Claims do not advance LoadingTotalSteps ---
            // [TITAN-ORBIT] Client denominator N = hybrid GO bodies only. Stay parked at 100% of
            // body steps while ownership flips finish, then Finalize sets LoadingComplete.
            SetLoadingProgress(ref state, _totalSpawnSteps, _totalSpawnSteps);
            return _claimIndex >= _claimQueue.Length;
        }

        /// <summary>Instantiates one prefab from the pending queue entry.</summary>
        void SpawnQueuedEntity(ref SystemState state, in PendingSpawn pending)
        {
            switch (pending.Kind)
            {
                case SpawnKind.HomePlanet:
                    _layoutEntries.Add(new MapLayoutEntryElement
                    {
                        EntityKind = 1,
                        Position = pending.Position,
                        Team = pending.Team,
                        PlanetId = (int)pending.Team,
                        Scale = pending.Scale.x,
                    });
                    SpawnPlanet(ref state, pending.Position, pending.Team, true, pending.Scale.x, pending.Level,
                        ref _nextNeutralPlanetId, 0);
                    break;
                case SpawnKind.NeutralPlanet:
                    // Always spawn as unowned; starting claims flip Ownership later (round-robin).
                    int planetId = _nextNeutralPlanetId;
                    _layoutEntries.Add(new MapLayoutEntryElement
                    {
                        EntityKind = 2,
                        Position = pending.Position,
                        Scale = pending.Scale.x,
                        Team = TeamId.None,
                        PlanetId = planetId,
                    });
                    if (pending.NeutralLayoutIndex >= 0 &&
                        pending.NeutralLayoutIndex < _neutralPlanetIdsByLayoutIndex.Length)
                    {
                        _neutralPlanetIdsByLayoutIndex[pending.NeutralLayoutIndex] = planetId;
                    }

                    SpawnPlanet(ref state, pending.Position, TeamId.None, false, pending.Scale.x, pending.Level,
                        ref _nextNeutralPlanetId, pending.ShipFamilyConfigIndex);
                    break;
                case SpawnKind.Asteroid:
                {
                    float uniformScale = math.cmax(pending.Scale);
                    _layoutEntries.Add(new MapLayoutEntryElement
                    {
                        EntityKind = 3,
                        Position = pending.Position,
                        Scale = uniformScale,
                    });
                    SpawnAsteroid(
                        ref state, pending.Position, pending.Scale, pending.GemValue, pending.MaxHealth,
                        pending.Size);
                    break;
                }
            }
        }

        void FinalizeGeneration(ref SystemState state)
        {
            // --- Publish layout buffer and mark loading complete for clients ---
            var em = state.EntityManager;
            var layout = em.GetBuffer<MapLayoutEntryElement>(_mapEntity);
            for (int i = 0; i < _layoutEntries.Length; i++)
                layout.Add(_layoutEntries[i]);

            int neutralCount = 0;
            int asteroidCount = 0;
            if (_spawnQueue.IsCreated)
            {
                for (int i = 0; i < _spawnQueue.Length; i++)
                {
                    switch (_spawnQueue[i].Kind)
                    {
                        case SpawnKind.NeutralPlanet: neutralCount++; break;
                        case SpawnKind.Asteroid: asteroidCount++; break;
                    }
                }
            }

            // --- Body-only denominator (homes + neutrals + asteroids) ---
            // [TITAN-ORBIT] Equals MapSessionMetaRpc.LoadingTotalSteps / client proxy N.
            int bodySteps = math.max(1, _rolled.TeamCount + neutralCount + asteroidCount);
            _totalSpawnSteps = bodySteps;
            SetLoadingProgress(ref state, bodySteps, bodySteps);

            var mapState = em.GetComponentData<MapStateSingleton>(_mapEntity);
            mapState.LoadingComplete = true;
            // --- Match metadata (stable for the life of this map) ---
            // [TITAN-ORBIT] Clients often never see MapStateSingleton as a ghost entity, so these
            // counts are also sent via MapSessionMetaRpc and written into the UGS lobby for Join Game.
            // GoInGame / catch-up send only after LoadingComplete so clients never latch rolled N.
            mapState.TeamCount = _rolled.TeamCount;
            mapState.NeutralPlanetCount = neutralCount;
            mapState.AsteroidCount = asteroidCount;
            em.SetComponentData(_mapEntity, mapState);

            int claimCount = _claimQueue.IsCreated ? _claimQueue.Length : 0;
            UnityEngine.Debug.Log(
                $"[MapGeneration] Map generated. Size: {_rolled.MapWidth:F0}x{_rolled.MapHeight:F0}, " +
                $"Teams: {_rolled.TeamCount}, Neutrals: {neutralCount}, Asteroids: {asteroidCount}" +
                (asteroidCount < _rolled.AsteroidCount
                    ? $" (rolled {_rolled.AsteroidCount})"
                    : string.Empty) +
                $", StartingClaims: {claimCount}, LoadingSteps: {bodySteps} (bodies only), Seed: {_rolled.Seed}");

            DisposeNativeCollections();
        }

        void SetLoadingProgress(ref SystemState state, int completedSteps, int totalSteps)
        {
            var em = state.EntityManager;
            var mapState = em.GetComponentData<MapStateSingleton>(_mapEntity);
            mapState.LoadingCompletedSteps = completedSteps;
            mapState.LoadingTotalSteps = totalSteps;
            mapState.LoadingProgress = totalSteps > 0 ? math.saturate((float)completedSteps / totalSteps) : 0f;
            em.SetComponentData(_mapEntity, mapState);
        }

        void DisposeNativeCollections()
        {
            if (_layoutEntries.IsCreated) _layoutEntries.Dispose();
            if (_spawnQueue.IsCreated) _spawnQueue.Dispose();
            if (_claimQueue.IsCreated) _claimQueue.Dispose();
            if (_neutralPlanetIdsByLayoutIndex.IsCreated) _neutralPlanetIdsByLayoutIndex.Dispose();
        }

        /// <summary>Fresh map generation must not inherit player ships from a prior match on the same server world.</summary>
        static void DestroyExistingPlayerShips(ref SystemState state)
        {
            var em = state.EntityManager;
            using var ships = em.CreateEntityQuery(typeof(ShipTag), typeof(GhostOwner));
            using var entities = ships.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
                em.DestroyEntity(entities[i]);
        }

        void SpawnPlanet(ref SystemState state, float3 pos, TeamId team, bool isHome, float scale, int level, ref int nextNeutralPlanetId, byte shipFamilyConfigIndex)
        {
            if (!SystemAPI.TryGetSingleton<GamePrefabs>(out var prefabs) || prefabs.Planet == Entity.Null)
                return;
            var em = state.EntityManager;
            var e = em.Instantiate(prefabs.Planet);
            em.SetComponentData(e, LocalTransform.FromPositionRotationScale(pos, quaternion.identity, scale));
            int planetId = isHome ? (int)team : nextNeutralPlanetId++;
            int maxPopulation = PlanetPopulationMath.GetMaxPopulation(scale, level);
            SetOrAddComponent(em, e, new PlanetState
            {
                Ownership = team,
                Population = maxPopulation,
                PlanetLevel = level,
                PlanetId = planetId,
                IsHomePlanet = isHome,
                ShipFamilyConfigIndex = isHome ? PlanetShipFamilyAssignment.HomeFamilyConfigIndex : shipFamilyConfigIndex,
            });
            if (!em.HasComponent<PlanetTag>(e))
                em.AddComponent<PlanetTag>(e);
            if (isHome && !em.HasComponent<HomePlanetTag>(e))
                em.AddComponent<HomePlanetTag>(e);
            if (isHome && !em.HasBuffer<ContributedGemsElement>(e))
                em.AddBuffer<ContributedGemsElement>(e);
            SetOrAddComponent(em, e, new PlanetGrowthState
            {
                FractionalPopulation = maxPopulation,
            });
            float maxShield = PlanetGemMoonMath.GetMaxShieldForLevel(level);
            var moonState = new PlanetGemMoonState
            {
                CurrentShield = maxShield,
                MaxShield = maxShield,
            };
            PlanetGemMoonCombatLogic.InitMoonGems(ref moonState);
            SetOrAddComponent(em, e, moonState);

            // --- Planetary defense pads for owned homes (neutrals wait for claim wipe) ---
            // [TITAN-ORBIT] Homes spawn already owned — seed empty slots immediately so pads
            // appear without waiting on SlotSync's first tick. Optional random starter turrets
            // use the same Map Generation Settings knob as starting owned neutrals.
            if (team != TeamId.None)
            {
                int planetLevel = math.max(1, level);
                PlanetaryDefenseSlotSyncSystem.WipeSlotsForOwnershipChange(
                    em, e, team, planetLevel);

                if (_config.StartingRandomDefenseTurretsMax > 0 &&
                    em.HasBuffer<PlanetaryDefenseSlotElement>(e))
                {
                    var defenseBuffer = em.GetBuffer<PlanetaryDefenseSlotElement>(e);
                    PlanetaryDefenseLogic.SeedRandomStartingTurrets(
                        defenseBuffer,
                        ref _rng,
                        _config.StartingRandomDefenseTurretsMax,
                        planetLevel,
                        PlanetaryDefenseConfig.LoadDefault());
                }
            }
        }

        /// <summary>
        /// Instantiates one asteroid from the map queue with gem capacity and max Health from
        /// <see cref="AsteroidSettings"/> ratios (via layout).
        /// </summary>
        void SpawnAsteroid(ref SystemState state, float3 pos, float3 scale, float gemValue, float maxHealth, float size)
        {
            if (!SystemAPI.TryGetSingleton<GamePrefabs>(out var prefabs) || prefabs.Asteroid == Entity.Null)
                return;

            // --- Shared spawn path (same as timed respawn) ---
            AsteroidSpawning.Spawn(
                state.EntityManager,
                prefabs.Asteroid,
                pos,
                math.cmax(scale),
                gemValue,
                maxHealth,
                size);
        }

        /// <summary>
        /// Copies <see cref="AsteroidSettingsCache"/> into a Burst-safe layout tuning struct.
        /// </summary>
        static MapGenerationLogic.AsteroidBodyTuning ResolveAsteroidBodyTuning()
        {
            var settings = AsteroidSettingsCache.ResolveOrDefault();
            settings.ClampValues();
            return new MapGenerationLogic.AsteroidBodyTuning
            {
                MinSize = settings.MinSize,
                MaxSize = settings.MaxSize,
                HealthPerSize = settings.HealthPerSize,
                GemsPerSize = settings.GemsPerSize,
                VisualScaleAtMinSize = settings.VisualScaleAtMinSize,
                VisualScaleAtMaxSize = settings.VisualScaleAtMaxSize,
            };
        }

        static void SetOrAddComponent<T>(EntityManager em, Entity e, T value) where T : unmanaged, IComponentData
        {
            if (em.HasComponent<T>(e))
                em.SetComponentData(e, value);
            else
                em.AddComponentData(e, value);
        }
    }
}
