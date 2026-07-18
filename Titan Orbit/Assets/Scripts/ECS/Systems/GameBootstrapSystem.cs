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
            state.EntityManager.SetComponentData(entity, new MapStateSingleton { MapWidth = 1000f, MapHeight = 1000f });
            state.EntityManager.AddBuffer<BulletElement>(entity);
            state.EntityManager.AddBuffer<BulletSpawnEventElement>(entity);
            state.EntityManager.AddBuffer<BulletHitEventElement>(entity);
            state.EntityManager.AddBuffer<MapLayoutEntryElement>(entity);
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
    /// planets and asteroids incrementally, updates loading progress on <see cref="MapStateSingleton"/>.
    /// Spawns multiple bodies per sim tick so large asteroid fields (400–800+) do not take minutes on
    /// dedicated servers or block remote clients waiting for ghost replication.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct MapGenerationSystem : ISystem
    {
        /// <summary>How many planets/asteroids to instantiate per server sim tick during map build.</summary>
        const int SpawnsPerSimulationTick = 32;
        enum Phase : byte
        {
            Idle,
            Spawning,
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
            public byte ShipFamilyConfigIndex;
        }

        Phase _phase;
        MapGenerationConfig _config;
        MapGenerationLogic.RolledParameters _rolled;
        Random _rng;
        int _spawnIndex;
        int _nextNeutralPlanetId;
        int _totalSpawnSteps;
        Entity _mapEntity;

        NativeList<PendingSpawn> _spawnQueue;
        NativeList<MapLayoutEntryElement> _layoutEntries;

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
            // Publish total spawn steps immediately so loading UI never treats "completed so far" as 100%.
            _totalSpawnSteps = math.max(1, estimatedEntries);
            SetLoadingProgress(ref state, 0, _totalSpawnSteps);
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
                });
            }

            MapGenerationLogic.BuildNeutralPlanets(_config, _rolled, ref _rng, planetPlacements, neutralLayouts);
            for (int i = 0; i < neutralLayouts.Length; i++)
            {
                var neutral = neutralLayouts[i];
                _spawnQueue.Add(new PendingSpawn
                {
                    Kind = SpawnKind.NeutralPlanet,
                    Position = neutral.Position,
                    Scale = new float3(neutral.Scale),
                    Level = neutral.Level,
                    ShipFamilyConfigIndex = (byte)(1 + _rng.NextInt(0, PlanetShipFamilyAssignment.NonHomeFamilySlotCount)),
                });
            }

            MapGenerationLogic.BuildAsteroids(_config, _rolled, ref _rng, planetPlacements, asteroidLayouts);
            for (int i = 0; i < asteroidLayouts.Length; i++)
            {
                var asteroid = asteroidLayouts[i];
                _spawnQueue.Add(new PendingSpawn
                {
                    Kind = SpawnKind.Asteroid,
                    Position = asteroid.Position,
                    Scale = asteroid.Scale,
                    GemValue = asteroid.GemValue,
                });
            }

            homeLayouts.Dispose();
            neutralLayouts.Dispose();
            asteroidLayouts.Dispose();
            planetPlacements.Dispose();

            _totalSpawnSteps = math.max(1, _spawnQueue.Length);
            SetLoadingProgress(ref state, 0, _totalSpawnSteps);
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
                    _layoutEntries.Add(new MapLayoutEntryElement
                    {
                        EntityKind = 2,
                        Position = pending.Position,
                        Scale = pending.Scale.x,
                    });
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
                    SpawnAsteroid(ref state, pending.Position, pending.Scale, pending.GemValue);
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

            SetLoadingProgress(ref state, _totalSpawnSteps, _totalSpawnSteps);
            var mapState = em.GetComponentData<MapStateSingleton>(_mapEntity);
            mapState.LoadingComplete = true;
            // --- Match metadata (stable for the life of this map) ---
            // [TITAN-ORBIT] Clients often never see MapStateSingleton as a ghost entity, so these
            // counts are also sent via MapSessionMetaRpc and written into the UGS lobby for Join Game.
            mapState.TeamCount = _rolled.TeamCount;
            mapState.NeutralPlanetCount = neutralCount;
            mapState.AsteroidCount = asteroidCount;
            em.SetComponentData(_mapEntity, mapState);

            UnityEngine.Debug.Log(
                $"[MapGeneration] Map generated. Size: {_rolled.MapWidth:F0}x{_rolled.MapHeight:F0}, " +
                $"Teams: {_rolled.TeamCount}, Neutrals: {neutralCount}, Asteroids: {asteroidCount}, " +
                $"LoadingSteps: {_totalSpawnSteps}, Seed: {_rolled.Seed}");

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
        }

        void SpawnAsteroid(ref SystemState state, float3 pos, float3 scale, float gemValue)
        {
            if (!SystemAPI.TryGetSingleton<GamePrefabs>(out var prefabs) || prefabs.Asteroid == Entity.Null)
                return;
            var em = state.EntityManager;
            var e = em.Instantiate(prefabs.Asteroid);
            float uniformScale = math.cmax(scale);
            em.SetComponentData(e, LocalTransform.FromPositionRotationScale(pos, quaternion.identity, uniformScale));
            SetOrAddComponent(em, e, new AsteroidState
            {
                RemainingGems = gemValue,
                Health = gemValue,
            });
            if (!em.HasComponent<AsteroidTag>(e))
                em.AddComponent<AsteroidTag>(e);
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
