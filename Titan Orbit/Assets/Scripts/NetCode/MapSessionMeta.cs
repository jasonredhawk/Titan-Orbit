using System.Text;
using TitanOrbit.Core;
using TitanOrbit.ECS;
using TitanOrbit.Generation;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// [NETCODE] One-shot RPC: server sends the map "recipe summary" to a joining client —
    /// how many planets/asteroids to expect, team count, and rolled map size.
    /// <para>
    /// [TITAN-ORBIT] Loading-bar contract: this is the denominator <c>N</c> only. The bar advances
    /// when the client Instantiates GameObject proxies, not when this RPC (or ghost packets) arrive.
    /// Sim bodies still stream as NetCode ghosts; this is not a full per-body layout payload yet.
    /// </para>
    /// Also used for toroidal size / UGS lobby browse. Fills the gap where MapStateSingleton
    /// GhostFields never arrive on dedicated clients (singleton is CreateEntity, not a ghost).
    /// </summary>
    public struct MapSessionMetaRpc : IRpcCommand
    {
        /// <summary>
        /// [TITAN-ORBIT] How many planet+asteroid GameObjects the client should build (loading "/ N").
        /// </summary>
        public int LoadingTotalSteps;

        /// <summary>[TITAN-ORBIT] Team slots / home planets for this match.</summary>
        public int TeamCount;

        /// <summary>[TITAN-ORBIT] Neutral non-home planets.</summary>
        public int NeutralPlanetCount;

        /// <summary>[TITAN-ORBIT] Asteroids.</summary>
        public int AsteroidCount;

        /// <summary>[TITAN-ORBIT] Rolled toroidal map width (world units). Required for wrap/minimap.</summary>
        public float MapWidth;

        /// <summary>[TITAN-ORBIT] Rolled toroidal map height (world units).</summary>
        public float MapHeight;
    }

    /// <summary>
    /// [NETCODE] Tag on a server connection entity after MapSessionMetaRpc was sent once.
    /// Prevents duplicate meta RPCs from GoInGame + catch-up systems.
    /// </summary>
    public struct MapSessionMetaSent : IComponentData { }

    /// <summary>
    /// [TITAN-ORBIT] Managed cache of the last MapSessionMetaRpc received on this client,
    /// plus server-side helpers that publish the same totals (and per-team roster/planet CSVs)
    /// into UGS lobby Data for the Join Game browser.
    /// Readable from MonoBehaviours (EcsGameBridge, Join Game UI) without querying ECS.
    /// Cleared when leaving a session so a new join does not reuse stale totals.
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
        /// Applies RPC payload to the cache. Called from the client receive system.
        /// Also pushes size into <see cref="ToroidalMapEcs"/> / <see cref="ToroidalMap"/> so display
        /// and minimap do not keep the 1000×1000 default (which leaves huge empty wrap gaps).
        /// </summary>
        /// <param name="rpc">Server-authored match totals.</param>
        public static void Apply(in MapSessionMetaRpc rpc)
        {
            // --- Merge payload (never downgrade a good team count to 0) ---
            // [TITAN-ORBIT] A mid-spawn RPC with steps>0 teams=0 used to wipe Join Team UI.
            // Prefer the richer of previous latch vs this RPC.
            int nextSteps = Mathf.Max(0, rpc.LoadingTotalSteps);
            int nextTeams = Mathf.Max(0, rpc.TeamCount);
            int nextNeutrals = Mathf.Max(0, rpc.NeutralPlanetCount);
            int nextAsteroids = Mathf.Max(0, rpc.AsteroidCount);

            if (nextSteps > 0)
                LoadingTotalSteps = Mathf.Max(LoadingTotalSteps, nextSteps);
            if (nextTeams > 0)
                TeamCount = nextTeams;
            if (nextNeutrals > 0)
                NeutralPlanetCount = nextNeutrals;
            if (nextAsteroids > 0)
                AsteroidCount = nextAsteroids;

            if (rpc.MapWidth > 0f)
                MapWidth = rpc.MapWidth;
            if (rpc.MapHeight > 0f)
                MapHeight = rpc.MapHeight;

            HasMeta = LoadingTotalSteps > 0 || TeamCount > 0 || NeutralPlanetCount > 0 || AsteroidCount > 0 || HasMapSize;

            // --- Keep toroidal helpers aligned with the rolled match ---
            if (HasMapSize)
                ApplyMapSizeToToroidalHelpers(MapWidth, MapHeight);
        }

        /// <summary>
        /// Writes width/height into both ECS and Vector3 toroidal static caches.
        /// </summary>
        public static void ApplyMapSizeToToroidalHelpers(float width, float height)
        {
            float w = Mathf.Max(100f, width);
            float h = Mathf.Max(100f, height);
            ToroidalMapEcs.SetMapSize(w, h);
            ToroidalMap.SetMapSize(w, h);
        }

        /// <summary>
        /// Clears latched meta when disconnecting / returning to menu.
        /// </summary>
        public static void Clear()
        {
            HasMeta = false;
            LoadingTotalSteps = 0;
            TeamCount = 0;
            NeutralPlanetCount = 0;
            AsteroidCount = 0;
            MapWidth = 0f;
            MapHeight = 0f;
        }

        /// <summary>
        /// Tries to read map totals from a ServerWorld MapStateSingleton (for lobby heartbeat).
        /// Returns false when the map has not finished generating yet.
        /// </summary>
        public static bool TryReadFromServerWorld(World serverWorld, out MapSessionMetaRpc meta)
        {
            meta = default;
            if (serverWorld == null || !serverWorld.IsCreated)
                return false;

            var em = serverWorld.EntityManager;
            using var query = em.CreateEntityQuery(ComponentType.ReadOnly<MapStateSingleton>());
            if (query.IsEmptyIgnoreFilter)
                return false;

            var mapState = query.GetSingleton<MapStateSingleton>();
            if (!mapState.LoadingComplete && mapState.LoadingTotalSteps <= 0)
                return false;

            meta = new MapSessionMetaRpc
            {
                LoadingTotalSteps = mapState.LoadingTotalSteps,
                TeamCount = mapState.TeamCount,
                NeutralPlanetCount = mapState.NeutralPlanetCount,
                AsteroidCount = mapState.AsteroidCount,
                MapWidth = mapState.MapWidth,
                MapHeight = mapState.MapHeight,
            };
            return mapState.LoadingComplete || meta.LoadingTotalSteps > 0;
        }

        /// <summary>
        /// Counts live planets owned by each active team (TeamA.. for <paramref name="teamCount"/>).
        /// Used by UGS lobby heartbeat so Join Game can show "planets 2/1/3" style ownership.
        /// </summary>
        /// <param name="serverWorld">Server ECS world.</param>
        /// <param name="teamCount">Active team slots for this match (2–5).</param>
        /// <param name="csv">Comma-separated ownership counts in TeamA.. order, or empty on failure.</param>
        /// <returns>True when at least one team slot was counted.</returns>
        public static bool TryBuildTeamPlanetCountsCsv(World serverWorld, int teamCount, out string csv)
        {
            csv = string.Empty;
            if (serverWorld == null || !serverWorld.IsCreated)
                return false;

            // --- Clamp to playable team slots ---
            // [TITAN-ORBIT] TeamId.TeamA=1 … TeamE=5; we publish one integer per active slot.
            int slots = Mathf.Clamp(teamCount, 0, 5);
            if (slots <= 0)
                return false;

            var counts = new int[slots];
            var em = serverWorld.EntityManager;
            using var query = em.CreateEntityQuery(ComponentType.ReadOnly<PlanetState>());
            if (query.IsEmptyIgnoreFilter)
            {
                // Map may still be spawning — publish zeros so Join Game still shows the team slots.
                csv = BuildCsv(counts);
                return true;
            }

            using var planets = query.ToComponentDataArray<PlanetState>(Allocator.Temp);
            for (int i = 0; i < planets.Length; i++)
            {
                TeamId ownership = planets[i].Ownership;
                if (ownership == TeamId.None)
                    continue;

                int index = (int)ownership - 1; // TeamA → 0
                if (index >= 0 && index < slots)
                    counts[index]++;
            }

            csv = BuildCsv(counts);
            return true;
        }

        /// <summary>
        /// Builds a CSV of current roster sizes per active team (TeamA.. order) from
        /// <see cref="TeamStateSingleton"/>. Used by UGS lobby heartbeat so Join Game can show
        /// "1/20" style player counts before the client connects.
        /// </summary>
        /// <param name="serverWorld">Server ECS world.</param>
        /// <param name="teamCount">Active team slots for this match (2–5).</param>
        /// <param name="csv">Comma-separated player counts, or empty on failure.</param>
        /// <returns>True when at least one team slot was written.</returns>
        public static bool TryBuildTeamPlayerCountsCsv(World serverWorld, int teamCount, out string csv)
        {
            csv = string.Empty;
            if (serverWorld == null || !serverWorld.IsCreated)
                return false;

            // --- Clamp to playable team slots ---
            // [TITAN-ORBIT] Same slot order as MapTeamPlanets so Join Game can zip the two CSVs.
            int slots = Mathf.Clamp(teamCount, 0, 5);
            if (slots <= 0)
                return false;

            var em = serverWorld.EntityManager;
            using var query = em.CreateEntityQuery(ComponentType.ReadOnly<TeamStateSingleton>());
            if (query.IsEmptyIgnoreFilter)
                return false;

            // --- Read roster counts ---
            // [ECS/DOTS] TeamStateSingleton is the server-authoritative roster (also ghosted in-match).
            var team = query.GetSingleton<TeamStateSingleton>();
            var counts = new int[slots];
            for (int i = 0; i < slots; i++)
                counts[i] = GetTeamPlayerCount(team, (TeamId)(i + 1));

            csv = BuildCsv(counts);
            return true;
        }

        /// <summary>
        /// Reads <see cref="TeamStateSingleton.MaxPlayersPerTeam"/> for lobby Data / Join Game capacity.
        /// </summary>
        /// <param name="serverWorld">Server ECS world.</param>
        /// <param name="maxPlayersPerTeam">Cap written at bootstrap (typically 20).</param>
        /// <returns>True when the singleton exists and the cap is positive.</returns>
        public static bool TryReadMaxPlayersPerTeam(World serverWorld, out int maxPlayersPerTeam)
        {
            maxPlayersPerTeam = 0;
            if (serverWorld == null || !serverWorld.IsCreated)
                return false;

            var em = serverWorld.EntityManager;
            using var query = em.CreateEntityQuery(ComponentType.ReadOnly<TeamStateSingleton>());
            if (query.IsEmptyIgnoreFilter)
                return false;

            // --- Cap from bootstrap ---
            // [TITAN-ORBIT] Match capacity for Join Game = ActiveTeamCount × MaxPlayersPerTeam
            // (not the UGS lobby MaxPlayers default of 60, which is a hard server ceiling).
            maxPlayersPerTeam = query.GetSingleton<TeamStateSingleton>().MaxPlayersPerTeam;
            return maxPlayersPerTeam > 0;
        }

        /// <summary>Returns the roster count field for one team id.</summary>
        static int GetTeamPlayerCount(in TeamStateSingleton team, TeamId id)
        {
            // --- Per-team roster switch ---
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

        /// <summary>Joins ownership counts as "1,1,2" for lobby Data.</summary>
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
    /// World: ClientSimulation. Runs after receive; destroys the RPC entity when done.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct MapSessionMetaClientSystem : ISystem
    {
        /// <summary>Require pending MapSessionMetaRpc receive entities.</summary>
        public void OnCreate(ref SystemState state)
        {
            var builder = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<MapSessionMetaRpc>()
                .WithAll<ReceiveRpcCommandRequest>();
            state.RequireForUpdate(state.GetEntityQuery(builder));
        }

        /// <summary>
        /// Latches each received meta payload, then destroys the RPC entity.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            var commandBuffer = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (rpc, reqEntity) in SystemAPI.Query<RefRO<MapSessionMetaRpc>>()
                         .WithAll<ReceiveRpcCommandRequest>().WithEntityAccess())
            {
                MapSessionMetaCache.Apply(rpc.ValueRO);
                Debug.Log(
                    "[MapSessionMeta] Client latched totals steps=" + MapSessionMetaCache.LoadingTotalSteps +
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
    /// [NETCODE] Server catch-up: send MapSessionMetaRpc to in-game connections that have not
    /// received it yet (e.g. GoInGame arrived before map FinalizeGeneration).
    /// World: ServerSimulation.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(TitanOrbitGoInGameServerSystem))]
    public partial struct MapSessionMetaServerCatchUpSystem : ISystem
    {
        /// <summary>Need map state and at least one in-game connection missing the sent tag.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<MapStateSingleton>();
            state.RequireForUpdate<NetworkStreamDriver>();
        }

        /// <summary>
        /// When map totals exist (including TeamCount), send meta once per in-game connection.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            if (!SystemAPI.TryGetSingleton<MapStateSingleton>(out var mapState))
                return;

            // --- Wait until counts are real ---
            // [TITAN-ORBIT] LoadingTotalSteps alone is not enough — BeginGeneration publishes steps
            // before TeamCount historically, and a teams=0 RPC + MapSessionMetaSent stuck Join Team.
            if (mapState.LoadingTotalSteps <= 0 || mapState.TeamCount <= 0)
                return;

            var meta = new MapSessionMetaRpc
            {
                LoadingTotalSteps = mapState.LoadingTotalSteps,
                TeamCount = mapState.TeamCount,
                NeutralPlanetCount = mapState.NeutralPlanetCount,
                AsteroidCount = mapState.AsteroidCount,
                MapWidth = mapState.MapWidth,
                MapHeight = mapState.MapHeight,
            };

            var commandBuffer = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (_, connection) in SystemAPI.Query<RefRO<NetworkId>>()
                         .WithAll<NetworkStreamInGame>()
                         .WithNone<MapSessionMetaSent>()
                         .WithEntityAccess())
            {
                Entity metaEntity = commandBuffer.CreateEntity();
                commandBuffer.AddComponent(metaEntity, meta);
                commandBuffer.AddComponent(metaEntity, new SendRpcCommandRequest { TargetConnection = connection });
                commandBuffer.AddComponent<MapSessionMetaSent>(connection);
                Debug.Log(
                    "[MapSessionMeta] Server catch-up sent steps=" + meta.LoadingTotalSteps +
                    " teams=" + meta.TeamCount +
                    " neutrals=" + meta.NeutralPlanetCount +
                    " asteroids=" + meta.AsteroidCount);
            }

            commandBuffer.Playback(state.EntityManager);
        }
    }
}
