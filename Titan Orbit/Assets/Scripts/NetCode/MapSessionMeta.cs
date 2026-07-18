using System.Text;
using TitanOrbit.Core;
using TitanOrbit.ECS;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// [NETCODE] One-shot RPC: server sends authoritative map/session totals to a joining client.
    /// Used for the loading-screen denominator (stable "/ N") and can mirror UGS lobby browse data.
    /// MapStateSingleton GhostFields often never arrive on dedicated clients because the singleton
    /// entity is created with CreateEntity and is not a ghost prefab — this RPC fills that gap.
    /// </summary>
    public struct MapSessionMetaRpc : IRpcCommand
    {
        /// <summary>[TITAN-ORBIT] Total map body spawn steps (planets + asteroids + homes, etc.).</summary>
        public int LoadingTotalSteps;

        /// <summary>[TITAN-ORBIT] Team slots / home planets for this match.</summary>
        public int TeamCount;

        /// <summary>[TITAN-ORBIT] Neutral non-home planets.</summary>
        public int NeutralPlanetCount;

        /// <summary>[TITAN-ORBIT] Asteroids.</summary>
        public int AsteroidCount;
    }

    /// <summary>
    /// [NETCODE] Tag on a server connection entity after MapSessionMetaRpc was sent once.
    /// Prevents duplicate meta RPCs from GoInGame + catch-up systems.
    /// </summary>
    public struct MapSessionMetaSent : IComponentData { }

    /// <summary>
    /// [TITAN-ORBIT] Managed cache of the last MapSessionMetaRpc received on this client.
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

        /// <summary>
        /// Applies RPC payload to the cache. Called from the client receive system.
        /// </summary>
        /// <param name="rpc">Server-authored match totals.</param>
        public static void Apply(in MapSessionMetaRpc rpc)
        {
            LoadingTotalSteps = Mathf.Max(0, rpc.LoadingTotalSteps);
            TeamCount = Mathf.Max(0, rpc.TeamCount);
            NeutralPlanetCount = Mathf.Max(0, rpc.NeutralPlanetCount);
            AsteroidCount = Mathf.Max(0, rpc.AsteroidCount);
            HasMeta = LoadingTotalSteps > 0 || TeamCount > 0 || NeutralPlanetCount > 0 || AsteroidCount > 0;
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
                AsteroidCount = mapState.AsteroidCount
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
                    " asteroids=" + MapSessionMetaCache.AsteroidCount);
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
        /// When map totals exist, send meta once per NetworkStreamInGame connection.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            if (!SystemAPI.TryGetSingleton<MapStateSingleton>(out var mapState))
                return;
            if (!mapState.LoadingComplete && mapState.LoadingTotalSteps <= 0)
                return;

            var meta = new MapSessionMetaRpc
            {
                LoadingTotalSteps = mapState.LoadingTotalSteps,
                TeamCount = mapState.TeamCount,
                NeutralPlanetCount = mapState.NeutralPlanetCount,
                AsteroidCount = mapState.AsteroidCount
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
