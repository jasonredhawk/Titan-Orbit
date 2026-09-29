using System.Text;
using AOT;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Generation;
using Unity.Burst;
using Unity.Burst.Intrinsics;
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
    /// [TITAN-ORBIT] Solid join architecture: clients build asteroids from this recipe
    /// (<see cref="ClientMapHydrateSystem"/>). Planets arrive as ghosts. Occupancy RPC
    /// then culls rocks the server already destroyed.
    /// Loading bar tracks local hydrate progress, not hybrid Instantiates=1.
    /// </para>
    /// <para>
    /// Manual serializer. A dedicated build that stops after <see cref="AsteroidVisualScaleAtMaxSize"/>
    /// does not include <see cref="LivePlanetCount"/>, <see cref="LiveShipCount"/>, or
    /// <see cref="AsteroidSizeSmallBias"/>. Reading those anyway logs "Trying to read 4 bytes"
    /// and drops the rest of that RPC packet.
    /// </para>
    /// </summary>
    // No [BurstCompile] on this struct. The static function-pointer field is a static
    // constructor; Burst AOT rejects external calls there (BC1091) on the Linux player.
    [NetCodeDisableCommandCodeGen]
    public struct MapSessionMetaRpc : IRpcCommand, IRpcCommandSerializer<MapSessionMetaRpc>
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

        /// <summary>Live planet ghosts on the server at send time (homes + neutrals still in play).</summary>
        public int LivePlanetCount;

        /// <summary>Live ship ghosts on the server at send time (0 in an empty match is valid).</summary>
        public int LiveShipCount;

        /// <summary>
        /// Power-law Size bias used during BuildAsteroids (1 = uniform, 2+ = more small).
        /// Last on the recipe. Payloads that end before this field leave it 0 and the client uses 2.
        /// </summary>
        public float AsteroidSizeSmallBias;

        /// <summary>Writes the same field order the generated serializer used, including the live-count tail.</summary>
        [BurstCompile]
        public void Serialize(ref DataStreamWriter writer, in RpcSerializerState state, in MapSessionMetaRpc data)
        {
            writer.WriteInt(data.LoadingTotalSteps);
            writer.WriteInt(data.TeamCount);
            writer.WriteInt(data.NeutralPlanetCount);
            writer.WriteInt(data.AsteroidCount);
            writer.WriteFloat(data.MapWidth);
            writer.WriteFloat(data.MapHeight);
            writer.WriteUInt(data.MatchSeed);
            writer.WriteByte(data.HasFullRecipe);
            writer.WriteInt(data.RecipeConfig.Seed);
            writer.WriteFloat(data.RecipeConfig.MinMapSize);
            writer.WriteFloat(data.RecipeConfig.MaxMapSize);
            writer.WriteInt(data.RecipeConfig.MinTeamsPerMatch);
            writer.WriteInt(data.RecipeConfig.MaxTeamsPerMatch);
            writer.WriteInt(data.RecipeConfig.MaxPlayersPerTeam);
            writer.WriteFloat(data.RecipeConfig.HomePlanetSize);
            writer.WriteInt(data.RecipeConfig.HomePlanetLevel);
            writer.WriteFloat(data.RecipeConfig.HomePlanetDistance);
            writer.WriteFloat(data.RecipeConfig.MinHomePlanetPairSeparation);
            writer.WriteFloat(data.RecipeConfig.ClearanceRadiusAroundHomePlanet);
            writer.WriteInt(data.RecipeConfig.MinNeutralPlanets);
            writer.WriteInt(data.RecipeConfig.MaxNeutralPlanets);
            writer.WriteInt(data.RecipeConfig.StartingOwnedNeutralPlanetsPerTeam);
            writer.WriteInt(data.RecipeConfig.StartingRandomDefenseTurretsMax);
            writer.WriteFloat(data.RecipeConfig.MinPlanetSize);
            writer.WriteFloat(data.RecipeConfig.MaxPlanetSize);
            writer.WriteByte(data.RecipeConfig.RandomizeNeutralStartingLevel);
            writer.WriteInt(data.RecipeConfig.MinNeutralStartingLevel);
            writer.WriteInt(data.RecipeConfig.MaxNeutralStartingLevel);
            writer.WriteFloat(data.RecipeConfig.PlanetRingPlacementMargin);
            writer.WriteInt(data.RecipeConfig.AsteroidsAtMinMapSize);
            writer.WriteInt(data.RecipeConfig.AsteroidsAtMaxMapSize);
            writer.WriteInt(data.RecipeConfig.MinAsteroidClusters);
            writer.WriteInt(data.RecipeConfig.MaxAsteroidClusters);
            writer.WriteFloat(data.RecipeConfig.MinAsteroidGemValue);
            writer.WriteFloat(data.RecipeConfig.MaxAsteroidGemValue);
            writer.WriteFloat(data.RecipeConfig.MinAsteroidSpacing);
            writer.WriteFloat(data.AsteroidMinSize);
            writer.WriteFloat(data.AsteroidMaxSize);
            writer.WriteFloat(data.AsteroidHealthPerSize);
            writer.WriteFloat(data.AsteroidGemsPerSize);
            writer.WriteFloat(data.AsteroidVisualScaleAtMinSize);
            writer.WriteFloat(data.AsteroidVisualScaleAtMaxSize);
            writer.WriteInt(data.LivePlanetCount);
            writer.WriteInt(data.LiveShipCount);
            writer.WriteFloat(data.AsteroidSizeSmallBias);
        }

        /// <summary>
        /// Reads the recipe, stopping at <see cref="RpcDeserializerState.PayloadEndByte"/> so a
        /// shorter dedicated-server payload does not read past the buffer.
        /// </summary>
        [BurstCompile]
        public void Deserialize(ref DataStreamReader reader, in RpcDeserializerState state, ref MapSessionMetaRpc data)
        {
            int end = state.PayloadEndByte;
            if (end <= 0 || end > reader.Length)
                end = reader.Length;

            data.LoadingTotalSteps = ReadIntBounded(ref reader, end);
            data.TeamCount = ReadIntBounded(ref reader, end);
            data.NeutralPlanetCount = ReadIntBounded(ref reader, end);
            data.AsteroidCount = ReadIntBounded(ref reader, end);
            data.MapWidth = ReadFloatBounded(ref reader, end);
            data.MapHeight = ReadFloatBounded(ref reader, end);
            data.MatchSeed = ReadUIntBounded(ref reader, end);
            data.HasFullRecipe = ReadByteBounded(ref reader, end);
            data.RecipeConfig.Seed = ReadIntBounded(ref reader, end);
            data.RecipeConfig.MinMapSize = ReadFloatBounded(ref reader, end);
            data.RecipeConfig.MaxMapSize = ReadFloatBounded(ref reader, end);
            data.RecipeConfig.MinTeamsPerMatch = ReadIntBounded(ref reader, end);
            data.RecipeConfig.MaxTeamsPerMatch = ReadIntBounded(ref reader, end);
            data.RecipeConfig.MaxPlayersPerTeam = ReadIntBounded(ref reader, end);
            data.RecipeConfig.HomePlanetSize = ReadFloatBounded(ref reader, end);
            data.RecipeConfig.HomePlanetLevel = ReadIntBounded(ref reader, end);
            data.RecipeConfig.HomePlanetDistance = ReadFloatBounded(ref reader, end);
            data.RecipeConfig.MinHomePlanetPairSeparation = ReadFloatBounded(ref reader, end);
            data.RecipeConfig.ClearanceRadiusAroundHomePlanet = ReadFloatBounded(ref reader, end);
            data.RecipeConfig.MinNeutralPlanets = ReadIntBounded(ref reader, end);
            data.RecipeConfig.MaxNeutralPlanets = ReadIntBounded(ref reader, end);
            data.RecipeConfig.StartingOwnedNeutralPlanetsPerTeam = ReadIntBounded(ref reader, end);
            data.RecipeConfig.StartingRandomDefenseTurretsMax = ReadIntBounded(ref reader, end);
            data.RecipeConfig.MinPlanetSize = ReadFloatBounded(ref reader, end);
            data.RecipeConfig.MaxPlanetSize = ReadFloatBounded(ref reader, end);
            data.RecipeConfig.RandomizeNeutralStartingLevel = ReadByteBounded(ref reader, end);
            data.RecipeConfig.MinNeutralStartingLevel = ReadIntBounded(ref reader, end);
            data.RecipeConfig.MaxNeutralStartingLevel = ReadIntBounded(ref reader, end);
            data.RecipeConfig.PlanetRingPlacementMargin = ReadFloatBounded(ref reader, end);
            data.RecipeConfig.AsteroidsAtMinMapSize = ReadIntBounded(ref reader, end);
            data.RecipeConfig.AsteroidsAtMaxMapSize = ReadIntBounded(ref reader, end);
            data.RecipeConfig.MinAsteroidClusters = ReadIntBounded(ref reader, end);
            data.RecipeConfig.MaxAsteroidClusters = ReadIntBounded(ref reader, end);
            data.RecipeConfig.MinAsteroidGemValue = ReadFloatBounded(ref reader, end);
            data.RecipeConfig.MaxAsteroidGemValue = ReadFloatBounded(ref reader, end);
            data.RecipeConfig.MinAsteroidSpacing = ReadFloatBounded(ref reader, end);
            data.AsteroidMinSize = ReadFloatBounded(ref reader, end);
            data.AsteroidMaxSize = ReadFloatBounded(ref reader, end);
            data.AsteroidHealthPerSize = ReadFloatBounded(ref reader, end);
            data.AsteroidGemsPerSize = ReadFloatBounded(ref reader, end);
            data.AsteroidVisualScaleAtMinSize = ReadFloatBounded(ref reader, end);
            data.AsteroidVisualScaleAtMaxSize = ReadFloatBounded(ref reader, end);
            data.LivePlanetCount = ReadIntBounded(ref reader, end);
            data.LiveShipCount = ReadIntBounded(ref reader, end);
            data.AsteroidSizeSmallBias = ReadFloatBounded(ref reader, end);
        }

        /// <inheritdoc/>
        public PortableFunctionPointer<RpcExecutor.ExecuteDelegate> CompileExecute()
        {
            return InvokeExecuteFunctionPointer;
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(RpcExecutor.ExecuteDelegate))]
        private static void InvokeExecute(ref RpcExecutor.Parameters parameters)
        {
            RpcExecutor.ExecuteCreateRequestComponent<MapSessionMetaRpc, MapSessionMetaRpc>(ref parameters);
        }

        static readonly PortableFunctionPointer<RpcExecutor.ExecuteDelegate> InvokeExecuteFunctionPointer =
            new PortableFunctionPointer<RpcExecutor.ExecuteDelegate>(InvokeExecute);

        static int ReadIntBounded(ref DataStreamReader reader, int end)
        {
            if (reader.GetBytesRead() + 4 > end)
                return 0;
            return reader.ReadInt();
        }

        static uint ReadUIntBounded(ref DataStreamReader reader, int end)
        {
            if (reader.GetBytesRead() + 4 > end)
                return 0;
            return reader.ReadUInt();
        }

        static float ReadFloatBounded(ref DataStreamReader reader, int end)
        {
            if (reader.GetBytesRead() + 4 > end)
                return 0f;
            return reader.ReadFloat();
        }

        static byte ReadByteBounded(ref DataStreamReader reader, int end)
        {
            if (reader.GetBytesRead() + 1 > end)
                return 0;
            return reader.ReadByte();
        }
    }

    /// <summary>
    /// Sends <see cref="MapSessionMetaRpc"/>. Replaces the generated request system, which
    /// source-gen skips once the RPC implements <see cref="IRpcCommandSerializer{T}"/>.
    /// </summary>
    [BurstCompile]
    [UpdateInGroup(typeof(RpcCommandRequestSystemGroup))]
    [CreateAfter(typeof(RpcSystem))]
    internal partial struct MapSessionMetaRpcSendSystem : ISystem
    {
        RpcCommandRequest<MapSessionMetaRpc, MapSessionMetaRpc> m_Request;

        [BurstCompile]
        struct SendRpc : IJobChunk
        {
            public RpcCommandRequest<MapSessionMetaRpc, MapSessionMetaRpc>.SendRpcData data;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                data.Execute(chunk, unfilteredChunkIndex);
            }
        }

        // Managed on purpose. OnCreate builds entity queries (Allocator.Temp / NativeText).
        // Burst-compiling it fails the Linux player with BC1091 inside NativeText dispose.
        public void OnCreate(ref SystemState state)
        {
            m_Request.OnCreate(ref state);
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var sendJob = new SendRpc { data = m_Request.InitJobData(ref state) };
            state.Dependency = sendJob.Schedule(m_Request.Query, state.Dependency);
        }
    }

    /// <summary>
    /// [NETCODE] Empty client→server RPC: "I am connected but still have no map recipe."
    /// The first recipe send is easy to drop (handshake / not-yet-Connected), and tagging
    /// <see cref="MapSessionMetaSent"/> used to prevent any retry — loading then soft-crawls
    /// to 8% with no 0/N counts and never finishes.
    /// </summary>
    public struct MapSessionMetaRequestRpc : IRpcCommand { }

    /// <summary>
    /// [NETCODE] On a server connection after we queued at least one <see cref="MapSessionMetaRpc"/>.
    /// Not proof the client applied it — we still resend until that connection is InGame, and
    /// we always answer <see cref="MapSessionMetaRequestRpc"/>.
    /// </summary>
    public struct MapSessionMetaSent : IComponentData
    {
        /// <summary>
        /// [NETCODE] <see cref="NetworkTime.ServerTick"/> index when we last queued a recipe RPC.
        /// Used so resends are once per second, not every sim tick.
        /// </summary>
        public uint LastSentSimulationTick;
    }

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

        /// <summary>Server live planet count at last recipe send.</summary>
        public static int LivePlanetCount { get; private set; }

        /// <summary>Server live ship count at last recipe send.</summary>
        public static int LiveShipCount { get; private set; }

        /// <summary>Rolled map width from the server (0 until meta arrives).</summary>
        public static float MapWidth { get; private set; }

        /// <summary>Rolled map height from the server (0 until meta arrives).</summary>
        public static float MapHeight { get; private set; }

        /// <summary>True when MapWidth/Height look like a real rolled map (not missing).</summary>
        public static bool HasMapSize => MapWidth >= 100f && MapHeight >= 100f;

        /// <summary>
        /// [TITAN-ORBIT] realtimeSinceStartup of the last client recipe request (or a large
        /// negative when none this session). Reset in <see cref="Clear"/>.
        /// </summary>
        public static float LastClientRecipeRequestRealtime { get; set; } = -999f;

        /// <summary>Ticks between recipe resends (~1 s at 60 Hz sim).</summary>
        public const uint RecipeResendIntervalTicks = 60;

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

            int livePlanets = 0;
            int liveShips = 0;
            using (var planetQ = em.CreateEntityQuery(ComponentType.ReadOnly<PlanetTag>()))
                livePlanets = planetQ.CalculateEntityCount();
            using (var shipQ = em.CreateEntityQuery(ComponentType.ReadOnly<ShipTag>()))
                liveShips = shipQ.CalculateEntityCount();

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
                LivePlanetCount = livePlanets,
                LiveShipCount = liveShips,
                AsteroidSizeSmallBias = asteroid.SizeSmallBias,
            };
            return true;
        }

        /// <summary>Copies AsteroidSettings into Burst-safe tuning (same as map generation).</summary>
        public static MapGenerationLogic.AsteroidBodyTuning ResolveAsteroidBodyTuning()
        {
            return MapGenerationLogic.FromAsteroidSettings(AsteroidSettingsCache.ResolveOrDefault());
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
                SizeSmallBias = rpc.AsteroidSizeSmallBias > 0f ? rpc.AsteroidSizeSmallBias : 2f,
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

            LivePlanetCount = Mathf.Max(0, rpc.LivePlanetCount);
            LiveShipCount = Mathf.Max(0, rpc.LiveShipCount);
            if (LivePlanetCount <= 0)
                LivePlanetCount = Mathf.Max(0, TeamCount) + Mathf.Max(0, NeutralPlanetCount);
            JoinWorldReadyCache.ExpectedPlanets = LivePlanetCount;
            JoinWorldReadyCache.ExpectedShips = LiveShipCount;
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
            LivePlanetCount = 0;
            LiveShipCount = 0;
            MapWidth = 0f;
            MapHeight = 0f;
            LastClientRecipeRequestRealtime = -999f;
            ToroidalMapEcs.ClearMapSize();
            ClientMapHydrateCache.Clear();
            JoinWorldReadyCache.Clear();
        }

        /// <summary>
        /// True when NetCode will actually queue a normal (non-approval) RPC to this connection.
        /// Sending earlier destroys the send entity without delivering — the loading bar then
        /// stalls at the 8% soft-crawl with no 0/N hydrate counts.
        /// </summary>
        /// <param name="conn">Server or client <see cref="NetworkStreamConnection"/>.</param>
        /// <returns>True when CurrentState is Connected and handshake/approval is finished.</returns>
        public static bool ConnectionCanReceiveGameplayRpc(in NetworkStreamConnection conn)
        {
            // [NETCODE] IsHandshakeOrApproval is internal to the NetCode package — mirror it here.
            var connectionState = conn.CurrentState;
            if (connectionState == ConnectionState.State.Handshake ||
                connectionState == ConnectionState.State.Approval)
                return false;
            return connectionState == ConnectionState.State.Connected;
        }

        /// <summary>
        /// Queues one targeted <see cref="MapSessionMetaRpc"/> (does not tag the connection).
        /// </summary>
        /// <param name="ecb">Temp command buffer played back this system update.</param>
        /// <param name="connection">Server connection entity to send to.</param>
        /// <param name="meta">Recipe payload built by <see cref="TryBuildRecipeRpc"/>.</param>
        public static void QueueRecipeRpc(EntityCommandBuffer ecb, Entity connection, in MapSessionMetaRpc meta)
        {
            Entity metaEntity = ecb.CreateEntity();
            ecb.AddComponent(metaEntity, meta);
            ecb.AddComponent(metaEntity, new SendRpcCommandRequest { TargetConnection = connection });
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
    /// [NETCODE] Client applies inbound <see cref="MapSessionMetaRpc"/> into
    /// <see cref="MapSessionMetaCache"/> so seed-hydrate can start.
    /// No RequireForUpdate — consume immediately (MaxRpcAgeFrames is only 4).
    /// World: ClientSimulation. Group: SimulationSystemGroup.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct MapSessionMetaClientSystem : ISystem
    {
        /// <summary>No singleton gate — inbound recipe RPCs must be destroyed the same tick.</summary>
        public void OnCreate(ref SystemState state)
        {
        }

        /// <summary>
        /// Latches every inbound recipe RPC, then destroys the receive entity.
        /// </summary>
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
#if UNITY_WEBGL && !UNITY_EDITOR
                // #region agent log
                TitanOrbit.Diagnostics.WebGlBootDebugProbe.Emit("F", "MapSessionMetaClientSystem.OnUpdate",
                    "recipe-latched",
                    "{\"seed\":" + rpc.ValueRO.MatchSeed +
                    ",\"full\":" + rpc.ValueRO.HasFullRecipe + "}");
                // #endregion
#endif
                commandBuffer.DestroyEntity(reqEntity);
            }

            commandBuffer.Playback(state.EntityManager);
        }
    }

    /// <summary>
    /// [TITAN-ORBIT] Local Host / Editor: copy the recipe from ServerWorld without waiting for
    /// an RPC. Same-process join used to stall at the 8% loading crawl when the one-shot
    /// <see cref="MapSessionMetaRpc"/> was queued during handshake and never delivered.
    /// Dedicated clients have no ServerWorld here — they use the RPC + request path.
    /// World: ClientSimulation. Group: InitializationSystemGroup (before hydrate in Simulation).
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial struct MapSessionMetaLocalHostCopySystem : ISystem
    {
        /// <summary>No RequireForUpdate — must tick while the recipe is still missing.</summary>
        public void OnCreate(ref SystemState state)
        {
        }

        /// <summary>
        /// When this process also has a ServerWorld and map gen is complete, latch the recipe
        /// locally so ClientMapHydrateSystem can start the same frame Simulation runs.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // --- Already latched this session ---
            if (ClientMapHydrateCache.HasFullRecipe)
                return;

            // --- Dedicated / remote client: no in-process server ---
            World serverWorld = ClientServerBootstrap.ServerWorld;
            if (serverWorld == null || !serverWorld.IsCreated)
                return;

            if (!MapSessionMetaCache.TryBuildRecipeRpc(serverWorld, out var meta))
                return;

            MapSessionMetaCache.Apply(meta);
            Debug.Log(
                "[MapSessionMeta] Local host copied recipe from ServerWorld seed=" + meta.MatchSeed +
                " asteroids=" + meta.AsteroidCount +
                " (skipped RPC wait)");
        }
    }

    /// <summary>
    /// [NETCODE] Client asks the server for the map recipe when connected but still missing
    /// <see cref="ClientMapHydrateCache.HasFullRecipe"/>. Cooldown avoids flooding the reliable
    /// RPC queue. World: ClientSimulation. Group: SimulationSystemGroup.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct MapSessionMetaRequestClientSystem : ISystem
    {
        /// <summary>Seconds between recipe requests (handshake drops are retried, not spammed).</summary>
        const float RequestCooldownSeconds = 1f;

        /// <summary>Log once so Player.log shows a hang instead of silent 8% crawl.</summary>
        bool _loggedWaiting;

        /// <summary>Needs a live NetCode driver; ticks even when no request RPC is in flight.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<NetworkStreamDriver>();
        }

        /// <summary>
        /// Sends <see cref="MapSessionMetaRequestRpc"/> to the server when this client has a
        /// Connected <see cref="NetworkId"/> but no full recipe yet.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // --- Stop once seed-hydrate can run ---
            if (ClientMapHydrateCache.HasFullRecipe)
            {
                _loggedWaiting = false;
                return;
            }

            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now - MapSessionMetaCache.LastClientRecipeRequestRealtime < RequestCooldownSeconds)
                return;

            var commandBuffer = new EntityCommandBuffer(Allocator.Temp);
            bool queued = false;

            foreach (var (conn, id) in SystemAPI.Query<RefRO<NetworkStreamConnection>, RefRO<NetworkId>>())
            {
                // [NETCODE] Non-approval RPCs queued during Handshake are destroyed without sending.
                if (!MapSessionMetaCache.ConnectionCanReceiveGameplayRpc(conn.ValueRO))
                    continue;

                Entity req = commandBuffer.CreateEntity();
                commandBuffer.AddComponent<MapSessionMetaRequestRpc>(req);
                commandBuffer.AddComponent(req, new SendRpcCommandRequest());
                queued = true;

                if (!_loggedWaiting)
                {
                    _loggedWaiting = true;
                    Debug.LogWarning(
                        "[MapSessionMeta] Client has NetworkId=" + id.ValueRO.Value +
                        " but no map recipe yet — requesting from server. " +
                        "Loading bar stays on the 8% crawl until this arrives.");
                }

                break;
            }

            if (queued)
                MapSessionMetaCache.LastClientRecipeRequestRealtime = now;

            commandBuffer.Playback(state.EntityManager);
        }
    }

    /// <summary>
    /// [NETCODE] Server sends the seed recipe to connections that can receive gameplay RPCs.
    /// First send waits until Connected (not Handshake). Resends every
    /// <see cref="MapSessionMetaCache.RecipeResendIntervalTicks"/> until that connection is
    /// InGame. Always answers <see cref="MapSessionMetaRequestRpc"/>.
    /// World: ServerSimulation. Group: SimulationSystemGroup, before GoInGame accept.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(TitanOrbitGoInGameServerSystem))]
    public partial struct MapSessionMetaServerCatchUpSystem : ISystem
    {
        /// <summary>Map must be finalized before we can build a recipe payload.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<MapStateSingleton>();
            state.RequireForUpdate<NetworkStreamDriver>();
        }

        /// <summary>
        /// Drains client recipe requests, then catch-up / resend to Connected connections.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            if (!MapSessionMetaCache.TryBuildRecipeRpc(state.World, out var meta))
                return;

            var commandBuffer = new EntityCommandBuffer(Allocator.Temp);
            uint tick = 0;
            if (SystemAPI.HasSingleton<NetworkTime>())
                tick = SystemAPI.GetSingleton<NetworkTime>().ServerTick.TickIndexForValidTick;

            // --- Client asked because the first send never latched ---
            foreach (var (req, rpcEntity) in SystemAPI.Query<RefRO<ReceiveRpcCommandRequest>>()
                         .WithAll<MapSessionMetaRequestRpc>()
                         .WithEntityAccess())
            {
                Entity connection = req.ValueRO.SourceConnection;
                commandBuffer.DestroyEntity(rpcEntity);

                if (!state.EntityManager.Exists(connection) ||
                    !state.EntityManager.HasComponent<NetworkStreamConnection>(connection) ||
                    !state.EntityManager.HasComponent<NetworkId>(connection))
                    continue;

                var conn = state.EntityManager.GetComponentData<NetworkStreamConnection>(connection);
                if (!MapSessionMetaCache.ConnectionCanReceiveGameplayRpc(conn))
                    continue;

                MapSessionMetaCache.QueueRecipeRpc(commandBuffer, connection, meta);
                StampSent(commandBuffer, connection, tick, state.EntityManager);
                Debug.Log(
                    "[MapSessionMeta] Server answered recipe request seed=" + meta.MatchSeed +
                    " asteroids=" + meta.AsteroidCount);
            }

            // --- First send: Connected + NetworkId, not yet tagged ---
            foreach (var (conn, connection) in SystemAPI.Query<RefRO<NetworkStreamConnection>>()
                         .WithAll<NetworkId>()
                         .WithNone<MapSessionMetaSent>()
                         .WithEntityAccess())
            {
                if (!MapSessionMetaCache.ConnectionCanReceiveGameplayRpc(conn.ValueRO))
                    continue;

                MapSessionMetaCache.QueueRecipeRpc(commandBuffer, connection, meta);
                commandBuffer.AddComponent(connection, new MapSessionMetaSent { LastSentSimulationTick = tick });
                Debug.Log(
                    "[MapSessionMeta] Server sent recipe seed=" + meta.MatchSeed +
                    " steps=" + meta.LoadingTotalSteps +
                    " teams=" + meta.TeamCount +
                    " asteroids=" + meta.AsteroidCount +
                    " (pre-InGame ok)");
            }

            // --- Resend until InGame (first queue can still be dropped the same tick) ---
            foreach (var (sent, conn, connection) in SystemAPI
                         .Query<RefRW<MapSessionMetaSent>, RefRO<NetworkStreamConnection>>()
                         .WithAll<NetworkId>()
                         .WithNone<NetworkStreamInGame>()
                         .WithEntityAccess())
            {
                if (!MapSessionMetaCache.ConnectionCanReceiveGameplayRpc(conn.ValueRO))
                    continue;

                uint last = sent.ValueRO.LastSentSimulationTick;
                uint elapsed = tick >= last ? tick - last : MapSessionMetaCache.RecipeResendIntervalTicks;
                if (elapsed < MapSessionMetaCache.RecipeResendIntervalTicks)
                    continue;

                MapSessionMetaCache.QueueRecipeRpc(commandBuffer, connection, meta);
                sent.ValueRW.LastSentSimulationTick = tick;
            }

            commandBuffer.Playback(state.EntityManager);
        }

        /// <summary>
        /// Adds or refreshes <see cref="MapSessionMetaSent"/> after queueing a recipe RPC.
        /// </summary>
        static void StampSent(
            EntityCommandBuffer commandBuffer,
            Entity connection,
            uint tick,
            EntityManager entityManager)
        {
            var sent = new MapSessionMetaSent { LastSentSimulationTick = tick };
            if (entityManager.HasComponent<MapSessionMetaSent>(connection))
                commandBuffer.SetComponent(connection, sent);
            else
                commandBuffer.AddComponent(connection, sent);
        }
    }
}
