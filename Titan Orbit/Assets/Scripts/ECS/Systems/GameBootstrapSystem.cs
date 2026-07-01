using TitanOrbit.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
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
                ActiveTeamCount = 3,
                MaxPlayersPerTeam = 20,
            });
            state.EntityManager.SetComponentData(entity, new MatchStateSingleton());
            state.EntityManager.SetComponentData(entity, new MapStateSingleton { MapWidth = 1000f, MapHeight = 1000f });
            state.EntityManager.AddBuffer<BulletElement>(entity);
            state.EntityManager.AddBuffer<BulletSpawnEventElement>(entity);
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

        public void OnUpdate(ref SystemState state)
        {
            if (_generated) return;
            _generated = true;

            var em = state.EntityManager;
            var mapEntity = SystemAPI.GetSingletonEntity<MapStateSingleton>();
            var layoutEntries = new NativeList<MapLayoutEntryElement>(35, Allocator.Temp);
            uint seed = (uint)SystemAPI.Time.ElapsedTime + 1;
            var rng = Random.CreateFromIndex(seed);

            float mapW = 1000f;
            float mapH = 1000f;
            var mapState = em.GetComponentData<MapStateSingleton>(mapEntity);
            mapState.MapWidth = mapW;
            mapState.MapHeight = mapH;
            mapState.BlueprintSeed = (int)seed;
            em.SetComponentData(mapEntity, mapState);
            Generation.ToroidalMapEcs.SetMapSize(mapW, mapH);

            int teamCount = 3;
            float radius = math.min(mapW, mapH) * 0.35f;
            for (int i = 0; i < teamCount; i++)
            {
                float angle = i * (math.PI * 2f / teamCount);
                float3 pos = new float3(math.cos(angle) * radius, 0f, math.sin(angle) * radius);
                layoutEntries.Add(new MapLayoutEntryElement
                {
                    EntityKind = 1,
                    Position = pos,
                    Team = (TeamId)(i + 1),
                    PlanetId = i,
                    Scale = 15f,
                });
                SpawnPlanet(ref state, pos, (TeamId)(i + 1), true);
            }

            for (int i = 0; i < 12; i++)
            {
                float3 pos = new float3(rng.NextFloat(-mapW * 0.4f, mapW * 0.4f), 0f, rng.NextFloat(-mapH * 0.4f, mapH * 0.4f));
                layoutEntries.Add(new MapLayoutEntryElement { EntityKind = 2, Position = pos, Scale = 8f });
                SpawnPlanet(ref state, pos, TeamId.None, false);
            }

            for (int i = 0; i < 20; i++)
            {
                float3 pos = new float3(rng.NextFloat(-mapW * 0.45f, mapW * 0.45f), 0f, rng.NextFloat(-mapH * 0.45f, mapH * 0.45f));
                layoutEntries.Add(new MapLayoutEntryElement { EntityKind = 3, Position = pos, Scale = 3f });
                SpawnAsteroid(ref state, pos);
            }

            var layout = em.GetBuffer<MapLayoutEntryElement>(mapEntity);
            for (int i = 0; i < layoutEntries.Length; i++)
                layout.Add(layoutEntries[i]);
            layoutEntries.Dispose();

            mapState = em.GetComponentData<MapStateSingleton>(mapEntity);
            mapState.LoadingProgress = 1f;
            mapState.LoadingComplete = true;
            em.SetComponentData(mapEntity, mapState);
        }

        void SpawnPlanet(ref SystemState state, float3 pos, TeamId team, bool isHome)
        {
            if (!SystemAPI.TryGetSingleton<GamePrefabs>(out var prefabs) || prefabs.Planet == Entity.Null)
                return;
            var em = state.EntityManager;
            var e = em.Instantiate(prefabs.Planet);
            em.SetComponentData(e, LocalTransform.FromPositionRotationScale(pos, quaternion.identity, isHome ? 15f : 8f));
            SetOrAddComponent(em, e, new PlanetState
            {
                Ownership = team,
                Population = isHome ? 50 : 0,
                PlanetLevel = isHome ? 3 : 1,
                PlanetId = (int)team,
                IsHomePlanet = isHome,
            });
            if (!em.HasComponent<PlanetTag>(e))
                em.AddComponent<PlanetTag>(e);
            if (isHome && !em.HasComponent<HomePlanetTag>(e))
                em.AddComponent<HomePlanetTag>(e);
        }

        void SpawnAsteroid(ref SystemState state, float3 pos)
        {
            if (!SystemAPI.TryGetSingleton<GamePrefabs>(out var prefabs) || prefabs.Asteroid == Entity.Null)
                return;
            var em = state.EntityManager;
            var e = em.Instantiate(prefabs.Asteroid);
            em.SetComponentData(e, LocalTransform.FromPositionRotationScale(pos, quaternion.identity, 3f));
            SetOrAddComponent(em, e, new AsteroidState
            {
                RemainingGems = 100f,
                Health = 100f,
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
