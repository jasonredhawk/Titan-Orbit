using System.Text;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Generation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// [NETCODE] One-shot RPC: server sends the match map recipe to a joining client —
    /// seed + generation config so the client can hydrate the map locally, plus size/team counts
    /// for UI and lobby.
    /// <para>
    /// [TITAN-ORBIT] Solid join architecture: clients build planets/asteroids from this recipe
    /// (<see cref="ClientMapHydrateSystem"/>). GhostSpawn no longer Instantiates the full field.
    /// Loading bar tracks local hydrate progress, not hybrid Instantiates=1.
    /// </para>
    /// </summary>
    public struct MapSessionMetaRpc : IRpcCommand
    {
        /// <summary>
        /// [TITAN-ORBIT] How many planet+asteroid bodies the client should build (loading "/ N").
        /// Body count only (homes + neutrals + asteroids) — never starting-claim ticks.
        /// </summary>
        public int LoadingTotalSteps;

        /// <summary>[TITAN-ORBIT] Team slots / home planets for this match.</summary>
        public int TeamCount;

        /// <summary>[TITAN-ORBIT] Neutral non-home planets.</summary>
        public int NeutralPlanetCount;

        /// <summary>[TITAN-ORBIT] Asteroids.</summary>
        public int AsteroidCount;

        /// <summary>[TITAN-ORBIT] Rolled toroidal map width (world units).</summary>
        public float MapWidth;

        /// <summary>[TITAN-ORBIT] Rolled toroidal map height (world units).</summary>
        public float MapHeight;

        /// <summary>
        /// [TITAN-ORBIT] Match seed used for <see cref="MapLayoutBlueprint"/> (BlueprintSeed).
        /// Non-zero when <see cref="HasFullRecipe"/> is 1.
        /// </summary>
        public uint MatchSeed;

        /// <summary>1 when <see cref="RecipeConfig"/> + seed are valid for client hydrate.</summary>
        public byte HasFullRecipe;

        /// <summary>Full generation config the server used (Seed field overwritten to MatchSeed).</summary>
        public MapGenerationConfig RecipeConfig;

        /// <summary>Asteroid Size / HP / gem / visual ratios used during BuildAsteroids.</summary>
        public float AsteroidMinSize;

        /// <summary>See <see cref="AsteroidMinSize"/>.</summary>
        public float AsteroidMaxSize;

        /// <summary>See <see cref="AsteroidMinSize"/>.</summary>
        public float AsteroidHealthPerSize;

        /// <summary>See <see cref="AsteroidMinSize"/>.</summary>
        public float AsteroidGemsPerSize;

        /// <summary>See <see cref="AsteroidMinSize"/>.</summary>
        public float AsteroidVisualScaleAtMinSize;

        /// <summary>See <see cref="AsteroidMinSize"/>.</summary>
        public float AsteroidVisualScaleAtMaxSize;
    }

    /// <summary>
    /// [NETCODE] Tag on a server connection entity after MapSessionMetaRpc was sent once.
    /// </summary>
    public struct MapSessionMetaSent : IComponentData { }

    /// <summary>
    /// [TITAN-ORBIT] Managed cache of the last MapSessionMetaRpc + lobby helpers.
    /// </summary>
    public static class MapSessionMetaCache
    {
        /// <summary>True after at least one MapSessionMetaRpc was applied this session.</summary>
        public static bool HasMeta { get; private set; }

        /// <summary>Authoritative loading denominator from the server.</summary>
        public static int LoadingTotalSteps { get; private set; }

        /// <summary>Teams / homes for this match.</summary>
        public static int TeamCount { get; private set; }

        /// <summary>Neutral planets for this match.</summary>
        public static int NeutralPlanetCount { get; private set; }

        /// <summary>Asteroids for this match.</summary>
        public static int AsteroidCount { get; private set; }

        /// <summary>Rolled map width from the server (0 until meta arrives).</summary>
        public static float MapWidth { get; private set; }

        /// <summary>Rolled map height from the server (0 until meta arrives).</summary>
        public static float MapHeight { get; private set; }

        /// <summary>True when MapWidth/Height look like a real rolled map (not missing).</summary>
        public static bool HasMapSize => MapWidth >= 100f && MapHeight >= 100f;

        /// <summary>
        /// Builds a recipe RPC from finalized <see cref="MapStateSingleton"/> + generation config.
        /// </summary>
        public static bool TryBuildRecipeRpc(
            World serverWorld,
            out MapSessionMetaRpc meta)
        {
            meta = default;
            if (serverWorld == null || !serverWorld.IsCreated)
                return false;

            var em = serverWorld.EntityManager;
            using var mapQuery = em.CreateEntityQuery(ComponentType.ReadOnly<MapStateSingleton>());
            if (mapQuery.IsEmptyIgnoreFilter)
                return false;

            var mapState = mapQuery.GetSingleton<MapStateSingleton>();
            if (!mapState.LoadingComplete ||
                mapState.LoadingTotalSteps <= 0 ||
                mapState.TeamCount <= 0)
                return false;

            MapGenerationConfig config = MapGenerationConfigUtility.Default();
            using (var cfgQuery = em.CreateEntityQuery(ComponentType.ReadOnly<MapGenerationConfig>()))
            {
                if (!cfgQuery.IsEmptyIgnoreFilter)
                    config = cfgQuery.GetSingleton<MapGenerationConfig>();
                else if (MapGenerationSettingsCache.Settings != null)
                    config = MapGenerationConfigUtility.FromSettings(MapGenerationSettingsCache.Settings);
            }

            uint seed = mapState.BlueprintSeed != 0
                ? (uint)mapState.BlueprintSeed
                : (uint)math.max(1, config.Seed);
            config.Seed = (int)seed;

            var asteroid = ResolveAsteroidBodyTuning();

            meta = new MapSessionMetaRpc
            {
                LoadingTotalSteps = mapState.LoadingTotalSteps,
                TeamCount = mapState.TeamCount,
                NeutralPlanetCount = mapState.NeutralPlanetCount,
                AsteroidCount = mapState.AsteroidCount,
                MapWidth = mapState.MapWidth,
                MapHeight = mapState.MapHeight,
                MatchSeed = seed,
                HasFullRecipe = 1,
                RecipeConfig = config,
                AsteroidMinSize = asteroid.MinSize,
                AsteroidMaxSize = asteroid.MaxSize,
                AsteroidHealthPerSize = asteroid.HealthPerSize,
                AsteroidGemsPerSize = asteroid.GemsPerSize,
                AsteroidVisualScaleAtMinSize = asteroid.VisualScaleAtMinSize,
                AsteroidVisualScaleAtMaxSize = asteroid.VisualScaleAtMaxSize,
            };
            return true;
        }

        /// <summary>Copies AsteroidSettings into Burst-safe tuning (same as map generation).</summary>
        public static MapGenerationLogic.AsteroidBodyTuning ResolveAsteroidBodyTuning()
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

        /// <summary>
        /// Applies RPC payload to the cache and kicks client seed hydrate.
        /// </summary>
        public static void Apply(in MapSessionMetaRpc rpc)
        {
            int nextSteps = Mathf.Max(0, rpc.LoadingTotalSteps);
            int nextTeams = Mathf.Max(0, rpc.TeamCount);

            if (nextSteps > 0)
            {
                LoadingTotalSteps = nextSteps;
                NeutralPlanetCount = Mathf.Max(0, rpc.NeutralPlanetCount);
                AsteroidCount = Mathf.Max(0, rpc.AsteroidCount);
            }

            if (nextTeams > 0)
                TeamCount = nextTeams;

            if (rpc.MapWidth > 0f)
                MapWidth = rpc.MapWidth;
            if (rpc.MapHeight > 0f)
                MapHeight = rpc.MapHeight;

            HasMeta = LoadingTotalSteps > 0 || TeamCount > 0 || NeutralPlanetCount > 0 ||
                      AsteroidCount > 0 || HasMapSize;

            if (HasMapSize)
                ApplyMapSizeToToroidalHelpers(MapWidth, MapHeight);

            // --- Seed hydrate recipe ---
            bool full = rpc.HasFullRecipe != 0 && rpc.MatchSeed != 0;
            var asteroidBody = new MapGenerationLogic.AsteroidBodyTuning
            {
                MinSize = rpc.AsteroidMinSize > 0f ? rpc.AsteroidMinSize : 1f,
                MaxSize = rpc.AsteroidMaxSize > 0f ? rpc.AsteroidMaxSize : 70f,
                HealthPerSize = rpc.AsteroidHealthPerSize > 0f ? rpc.AsteroidHealthPerSize : 1f,
                GemsPerSize = rpc.AsteroidGemsPerSize > 0f ? rpc.AsteroidGemsPerSize : 1f,
                VisualScaleAtMinSize = rpc.AsteroidVisualScaleAtMinSize > 0f
                    ? rpc.AsteroidVisualScaleAtMinSize
                    : 0.35f,
                VisualScaleAtMaxSize = rpc.AsteroidVisualScaleAtMaxSize > 0f
                    ? rpc.AsteroidVisualScaleAtMaxSize
                    : 3.5f,
            };

            // --- Hydrate denominator = asteroids (planets still stream as ghosts) ---
            int hydrateBodies = Mathf.Max(0, rpc.AsteroidCount);
            if (hydrateBodies <= 0 && nextSteps > 0)
                hydrateBodies = nextSteps;

            ClientMapHydrateCache.ApplyRecipe(
                rpc.MatchSeed,
                hydrateBodies,
                rpc.RecipeConfig,
                asteroidBody,
                full);
        }

        /// <summary>
        /// Writes width/height into both ECS and Vector3 toroidal static caches.
        /// </summary>
        public static void ApplyMapSizeToToroidalHelpers(float width, float height)
        {
            if (!ToroidalMapEcs.IsValidMapSize(width, height))
                return;

            ToroidalMapEcs.SetMapSize(width, height);
            ToroidalMap.SetMapSize(width, height);
        }

        /// <summary>Clears latched meta when disconnecting / returning to menu.</summary>
        public static void Clear()
        {
            HasMeta = false;
            LoadingTotalSteps = 0;
            TeamCount = 0;
            NeutralPlanetCount = 0;
            AsteroidCount = 0;
            MapWidth = 0f;
            MapHeight = 0f;
            ToroidalMapEcs.ClearMapSize();
            ClientMapHydrateCache.Clear();
        }

        /// <summary>
        /// Tries to read map totals from a ServerWorld MapStateSingleton (for lobby heartbeat).
        /// </summary>
        public static bool TryReadFromServerWorld(World serverWorld, out MapSessionMetaRpc meta)
        {
            return TryBuildRecipeRpc(serverWorld, out meta);
        }

        /// <summary>
        /// Counts live planets owned by each active team (TeamA.. for <paramref name="teamCount"/>).
        /// </summary>
        public static bool TryBuildTeamPlanetCountsCsv(World serverWorld, int teamCount, out string csv)
        {
            csv = string.Empty;
            if (serverWorld == null || !serverWorld.IsCreated)
                return false;

            int slots = Mathf.Clamp(teamCount, 0, 5);
            if (slots <= 0)
                return false;

            var counts = new int[slots];
            var em = serverWorld.EntityManager;
            using var query = em.CreateEntityQuery(ComponentType.ReadOnly<PlanetState>());
            if (query.IsEmptyIgnoreFilter)
            {
                csv = BuildCsv(counts);
                return true;
            }

            using var planets = query.ToComponentDataArray<PlanetState>(Allocator.Temp);
            for (int i = 0; i < planets.Length; i++)
            {
                TeamId ownership = planets[i].Ownership;
                if (ownership == TeamId.None)
                    continue;

                int index = (int)ownership - 1;
                if (index >= 0 && index < slots)
                    counts[index]++;
            }

            csv = BuildCsv(counts);
            return true;
        }

        /// <summary>
        /// Builds a CSV of current roster sizes per active team from <see cref="TeamStateSingleton"/>.
        /// </summary>
        public static bool TryBuildTeamPlayerCountsCsv(World serverWorld, int teamCount, out string csv)
        {
            csv = string.Empty;
            if (serverWorld == null || !serverWorld.IsCreated)
                return false;

            int slots = Mathf.Clamp(teamCount, 0, 5);
            if (slots <= 0)
                return false;

            var em = serverWorld.EntityManager;
            using var query = em.CreateEntityQuery(ComponentType.ReadOnly<TeamStateSingleton>());
            if (query.IsEmptyIgnoreFilter)
                return false;

            var team = query.GetSingleton<TeamStateSingleton>();
            var counts = new int[slots];
            for (int i = 0; i < slots; i++)
                counts[i] = GetTeamPlayerCount(team, (TeamId)(i + 1));

            csv = BuildCsv(counts);
            return true;
        }

        /// <summary>Reads <see cref="TeamStateSingleton.MaxPlayersPerTeam"/> for lobby Data.</summary>
        public static bool TryReadMaxPlayersPerTeam(World serverWorld, out int maxPlayersPerTeam)
        {
            maxPlayersPerTeam = 0;
            if (serverWorld == null || !serverWorld.IsCreated)
                return false;

            var em = serverWorld.EntityManager;
            using var query = em.CreateEntityQuery(ComponentType.ReadOnly<TeamStateSingleton>());
            if (query.IsEmptyIgnoreFilter)
                return false;

            maxPlayersPerTeam = query.GetSingleton<TeamStateSingleton>().MaxPlayersPerTeam;
            return maxPlayersPerTeam > 0;
        }

        static int GetTeamPlayerCount(in TeamStateSingleton team, TeamId id)
        {
            switch (id)
            {
                case TeamId.TeamA: return Mathf.Max(0, team.TeamACount);
                case TeamId.TeamB: return Mathf.Max(0, team.TeamBCount);
                case TeamId.TeamC: return Mathf.Max(0, team.TeamCCount);
                case TeamId.TeamD: return Mathf.Max(0, team.TeamDCount);
                case TeamId.TeamE: return Mathf.Max(0, team.TeamECount);
                default: return 0;
            }
        }

        static string BuildCsv(int[] counts)
        {
            var sb = new StringBuilder(counts.Length * 3);
            for (int i = 0; i < counts.Length; i++)
            {
                if (i > 0)
                    sb.Append(',');
                sb.Append(counts[i]);
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// [NETCODE] Client applies MapSessionMetaRpc into <see cref="MapSessionMetaCache"/>.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct MapSessionMetaClientSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            var builder = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<MapSessionMetaRpc>()
                .WithAll<ReceiveRpcCommandRequest>();
            state.RequireForUpdate(state.GetEntityQuery(builder));
        }

        public void OnUpdate(ref SystemState state)
        {
            var commandBuffer = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (rpc, reqEntity) in SystemAPI.Query<RefRO<MapSessionMetaRpc>>()
                         .WithAll<ReceiveRpcCommandRequest>().WithEntityAccess())
            {
                MapSessionMetaCache.Apply(rpc.ValueRO);
                Debug.Log(
                    "[MapSessionMeta] Client latched recipe seed=" + rpc.ValueRO.MatchSeed +
                    " full=" + rpc.ValueRO.HasFullRecipe +
                    " steps=" + MapSessionMetaCache.LoadingTotalSteps +
                    " teams=" + MapSessionMetaCache.TeamCount +
                    " neutrals=" + MapSessionMetaCache.NeutralPlanetCount +
                    " asteroids=" + MapSessionMetaCache.AsteroidCount +
                    " map=" + MapSessionMetaCache.MapWidth.ToString("F0") + "x" +
                    MapSessionMetaCache.MapHeight.ToString("F0"));
                commandBuffer.DestroyEntity(reqEntity);
            }

            commandBuffer.Playback(state.EntityManager);
        }
    }

    /// <summary>
    /// [NETCODE] Server sends MapSessionMetaRpc (with seed recipe) to connections that have
    /// <see cref="NetworkId"/> but have not received meta yet — <b>before</b> they go InGame
    /// so clients can hydrate the map first (Unity EnterInGame handshake pattern).
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(TitanOrbitGoInGameServerSystem))]
    public partial struct MapSessionMetaServerCatchUpSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<MapStateSingleton>();
            state.RequireForUpdate<NetworkStreamDriver>();
        }

        public void OnUpdate(ref SystemState state)
        {
            if (!MapSessionMetaCache.TryBuildRecipeRpc(state.World, out var meta))
                return;

            var commandBuffer = new EntityCommandBuffer(Allocator.Temp);

            // --- Send to any connected client missing the recipe (InGame not required) ---
            foreach (var (_, connection) in SystemAPI.Query<RefRO<NetworkId>>()
                         .WithNone<MapSessionMetaSent>()
                         .WithEntityAccess())
            {
                Entity metaEntity = commandBuffer.CreateEntity();
                commandBuffer.AddComponent(metaEntity, meta);
                commandBuffer.AddComponent(metaEntity, new SendRpcCommandRequest { TargetConnection = connection });
                commandBuffer.AddComponent<MapSessionMetaSent>(connection);
                Debug.Log(
                    "[MapSessionMeta] Server sent recipe seed=" + meta.MatchSeed +
                    " steps=" + meta.LoadingTotalSteps +
                    " teams=" + meta.TeamCount +
                    " asteroids=" + meta.AsteroidCount +
                    " (pre-InGame ok)");
            }

            commandBuffer.Playback(state.EntityManager);
        }
    }
}
