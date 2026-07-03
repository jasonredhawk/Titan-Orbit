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
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial struct GameBootstrapSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            if (SystemAPI.HasSingleton<TeamStateSingleton>())
                return;

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

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct MapGenerationSystem : ISystem
    {
        bool _generated;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<MapStateSingleton>();
            state.RequireForUpdate<GamePrefabs>();
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
            if (_generated) return;
            _generated = true;

            var em = state.EntityManager;
            var mapEntity = SystemAPI.GetSingletonEntity<MapStateSingleton>();
            var config = GetConfig(ref state);
            UnityEngine.Debug.Log(
                $"[MapGeneration] Using settings from {DescribeConfigSource()}. " +
                $"Map {config.MinMapSize:F0}-{config.MaxMapSize:F0}, teams {config.MinTeamsPerMatch}-{config.MaxTeamsPerMatch}, " +
                $"neutrals {config.MinNeutralPlanets}-{config.MaxNeutralPlanets}.");
            uint fallbackSeed = MapGenerationLogic.ComputeEphemeralSeed();
            var rolled = MapGenerationLogic.RollParameters(config, fallbackSeed);
            var rng = Random.CreateFromIndex(rolled.Seed);

            var mapState = em.GetComponentData<MapStateSingleton>(mapEntity);
            mapState.MapWidth = rolled.MapWidth;
            mapState.MapHeight = rolled.MapHeight;
            mapState.BlueprintSeed = (int)rolled.Seed;
            em.SetComponentData(mapEntity, mapState);
            Generation.ToroidalMapEcs.SetMapSize(rolled.MapWidth, rolled.MapHeight);

            var teamState = SystemAPI.GetSingletonRW<TeamStateSingleton>();
            teamState.ValueRW.ActiveTeamCount = rolled.TeamCount;

            int nextNeutralPlanetId = 100;
            int estimatedEntries = rolled.TeamCount + rolled.NeutralPlanetCount + rolled.AsteroidCount;
            var layoutEntries = new NativeList<MapLayoutEntryElement>(math.max(16, estimatedEntries), Allocator.Temp);
            var planetPlacements = new NativeList<MapGenerationLogic.PlanetPlacement>(estimatedEntries, Allocator.Temp);
            var homeLayouts = new NativeList<MapGenerationLogic.HomePlanetLayout>(rolled.TeamCount, Allocator.Temp);
            var neutralLayouts = new NativeList<MapGenerationLogic.NeutralPlanetLayout>(rolled.NeutralPlanetCount, Allocator.Temp);
            var asteroidLayouts = new NativeList<MapGenerationLogic.AsteroidLayout>(rolled.AsteroidCount, Allocator.Temp);

            MapGenerationLogic.BuildHomePlanets(config, rolled, ref rng, homeLayouts, planetPlacements);
            for (int i = 0; i < homeLayouts.Length; i++)
            {
                var home = homeLayouts[i];
                var team = (TeamId)(i + 1);
                layoutEntries.Add(new MapLayoutEntryElement
                {
                    EntityKind = 1,
                    Position = home.Position,
                    Team = team,
                    PlanetId = (int)team,
                    Scale = home.Scale,
                });
                SpawnPlanet(ref state, home.Position, team, true, home.Scale, home.Level, ref nextNeutralPlanetId);
            }

            MapGenerationLogic.BuildNeutralPlanets(config, rolled, ref rng, planetPlacements, neutralLayouts);
            for (int i = 0; i < neutralLayouts.Length; i++)
            {
                var neutral = neutralLayouts[i];
                layoutEntries.Add(new MapLayoutEntryElement
                {
                    EntityKind = 2,
                    Position = neutral.Position,
                    Scale = neutral.Scale,
                });
                SpawnPlanet(ref state, neutral.Position, TeamId.None, false, neutral.Scale, neutral.Level, ref nextNeutralPlanetId);
            }

            MapGenerationLogic.BuildAsteroids(config, rolled, ref rng, planetPlacements, asteroidLayouts);
            for (int i = 0; i < asteroidLayouts.Length; i++)
            {
                var asteroid = asteroidLayouts[i];
                float uniformScale = math.cmax(asteroid.Scale);
                layoutEntries.Add(new MapLayoutEntryElement
                {
                    EntityKind = 3,
                    Position = asteroid.Position,
                    Scale = uniformScale,
                });
                SpawnAsteroid(ref state, asteroid.Position, asteroid.Scale, asteroid.GemValue);
            }

            homeLayouts.Dispose();
            int neutralCount = neutralLayouts.Length;
            int asteroidCount = asteroidLayouts.Length;
            neutralLayouts.Dispose();
            asteroidLayouts.Dispose();
            planetPlacements.Dispose();

            var layout = em.GetBuffer<MapLayoutEntryElement>(mapEntity);
            for (int i = 0; i < layoutEntries.Length; i++)
                layout.Add(layoutEntries[i]);
            layoutEntries.Dispose();

            mapState = em.GetComponentData<MapStateSingleton>(mapEntity);
            mapState.LoadingProgress = 1f;
            mapState.LoadingComplete = true;
            em.SetComponentData(mapEntity, mapState);

            UnityEngine.Debug.Log(
                $"[MapGeneration] Map generated. Size: {rolled.MapWidth:F0}x{rolled.MapHeight:F0}, " +
                $"Teams: {rolled.TeamCount}, Neutrals: {neutralCount}, Asteroids: {asteroidCount}, Seed: {rolled.Seed}");
        }

        void SpawnPlanet(ref SystemState state, float3 pos, TeamId team, bool isHome, float scale, int level, ref int nextNeutralPlanetId)
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
