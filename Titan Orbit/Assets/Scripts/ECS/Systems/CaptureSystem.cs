using TitanOrbit.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [ECS/DOTS] Server win-condition check: if only one team color still owns planets, declare
    /// the match won. Uncaptured (neutral) worlds do not block that. Runs after people transport
    /// sim so a capture from population transfer is visible the same tick. Writes
    /// <see cref="MatchStateSingleton.WinningTeam"/>. That singleton lives on the server world
    /// and is not a ghost, so <see cref="MatchWinBroadcastSystem"/> mirrors the win onto the
    /// host client and sends <see cref="MatchWonRpc"/> for everyone else. The congrats card
    /// reads that. World: ServerSimulation. Group: SimulationSystemGroup, after
    /// PeopleTransportSimulationSystem.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(PeopleTransportSimulationSystem))]
    public partial struct CaptureSystem : ISystem
    {
        /// <summary>
        /// [ECS/DOTS] Each server tick: if every owned planet belongs to the same team, set
        /// WinningTeam and GameState = 2 (won). Neutral worlds are skipped. A second team color
        /// aborts the check. No owned planets at all is not a win.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // --- Singleton guards ---
            if (!SystemAPI.TryGetSingletonRW<MatchStateSingleton>(out var match))
                return;
            // [STANDARD] Early exit — winner already decided this match.
            if (match.ValueRO.WinningTeam != TeamId.None)
                return;

            // Map not ready: homes have not been assigned to teams yet.
            int activeTeams = SystemAPI.GetSingleton<TeamStateSingleton>().ActiveTeamCount;
            if (activeTeams <= 0)
                return;

            // --- One color left on the map ---
            // Neutral (TeamId.None) is uncaptured, not an opposing faction. Those worlds
            // stay in play and do not stop the win. Any second team color does.
            TeamId owner = TeamId.None;
            foreach (var planet in SystemAPI.Query<RefRO<PlanetState>>().WithAll<PlanetTag>())
            {
                TeamId team = planet.ValueRO.Ownership;
                if (team == TeamId.None)
                    continue;

                if (owner == TeamId.None)
                {
                    owner = team;
                    continue;
                }

                if (team != owner)
                    return;
            }

            if (owner == TeamId.None)
                return;

            match.ValueRW.WinningTeam = owner;
            match.ValueRW.GameState = 2;
            LogMatchWon(owner);
        }

        [Unity.Burst.BurstDiscard]
        static void LogMatchWon(TeamId team)
        {
            UnityEngine.Debug.Log($"[CaptureSystem] Match won by {team} — no other team still holds a planet.");
        }
    }

    /// <summary>
    /// Sends the win to clients the moment <see cref="CaptureSystem"/> sets it.
    /// One shot per server world. Local host also writes the client world immediately
    /// so the congrats card does not wait on an RPC round-trip.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(CaptureSystem))]
    public partial struct MatchWinBroadcastSystem : ISystem
    {
        /// <summary>1 after this server world has announced the current win.</summary>
        byte _announced;

        /// <summary>Watches the match singleton and broadcasts the first time a winner appears.</summary>
        public void OnUpdate(ref SystemState state)
        {
            if (_announced != 0)
                return;
            if (!SystemAPI.TryGetSingleton<MatchStateSingleton>(out var match))
                return;
            if (match.WinningTeam == TeamId.None)
                return;

            MatchWinNetNotify.Broadcast(match.WinningTeam, match.MatchTimer);
            _announced = 1;
        }
    }

    /// <summary>
    /// Client: copies <see cref="MatchWonRpc"/> onto a local <see cref="MatchStateSingleton"/>
    /// so the congrats card can read <c>WinningTeam</c> without a ghost.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct MatchWonRpcClientSystem : ISystem
    {
        /// <summary>Requires the incoming RPC queue.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<ReceiveRpcCommandRequest>();
        }

        /// <summary>Applies each win RPC, then destroys the request so it cannot replay.</summary>
        public void OnUpdate(ref SystemState state)
        {
            var ecb = new EntityCommandBuffer(Unity.Collections.Allocator.Temp);
            foreach (var (rpc, rpcEntity) in SystemAPI
                         .Query<RefRO<MatchWonRpc>>()
                         .WithAll<ReceiveRpcCommandRequest>()
                         .WithEntityAccess())
            {
                var cmd = rpc.ValueRO;
                if (cmd.WinningTeam != 0)
                    MatchWinNetNotify.Apply(state.EntityManager, (TeamId)cmd.WinningTeam, cmd.MatchTimer);

                ecb.DestroyEntity(rpcEntity);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }

    /// <summary>
    /// Host mirror + RPC send for a match win. Same shape as
    /// <see cref="PlanetOwnershipNetNotify"/>: the in-process client sees it this tick,
    /// remote clients see the RPC.
    /// </summary>
    public static class MatchWinNetNotify
    {
        /// <summary>
        /// Writes the win onto the host client world and queues <see cref="MatchWonRpc"/>
        /// for every connection.
        /// </summary>
        /// <param name="team">Team that is the only color left.</param>
        /// <param name="matchTimer">Server clock in seconds, painted on the congrats card.</param>
        public static void Broadcast(TeamId team, float matchTimer)
        {
            if (team == TeamId.None)
                return;

            // --- Host in-process (Editor / listen-server) ---
            // The congrats card reads ClientWorld first. The win component was only
            // created on the server, so without this mirror the card never opens.
            if (ClientServerBootstrap.ClientWorld != null && ClientServerBootstrap.ClientWorld.IsCreated)
                Apply(ClientServerBootstrap.ClientWorld.EntityManager, team, matchTimer);

            var server = ClientServerBootstrap.ServerWorld;
            if (server == null || !server.IsCreated)
                return;

            var em = server.EntityManager;
            Entity rpcEntity = em.CreateEntity();
            em.AddComponentData(rpcEntity, new MatchWonRpc
            {
                WinningTeam = (byte)team,
                MatchTimer = matchTimer,
            });
            em.AddComponentData(rpcEntity, new SendRpcCommandRequest { TargetConnection = Entity.Null });
        }

        /// <summary>
        /// Stores the winner on <paramref name="em"/>. Creates the singleton if this
        /// world does not have one yet (normal for a client).
        /// </summary>
        /// <param name="em">Client world, or the host's client world.</param>
        /// <param name="team">Winning team.</param>
        /// <param name="matchTimer">Clock to show. Does not rewind a timer that is already ahead.</param>
        public static void Apply(EntityManager em, TeamId team, float matchTimer)
        {
            if (team == TeamId.None)
                return;

            using var query = em.CreateEntityQuery(typeof(MatchStateSingleton));
            if (query.CalculateEntityCount() == 1)
            {
                Entity existing = query.GetSingletonEntity();
                var match = em.GetComponentData<MatchStateSingleton>(existing);
                match.WinningTeam = team;
                match.GameState = 2;
                match.MatchStarted = true;
                if (matchTimer > match.MatchTimer)
                    match.MatchTimer = matchTimer;
                em.SetComponentData(existing, match);
                return;
            }

            // More than one would make TryGetSingleton fail. Leave that alone.
            if (query.CalculateEntityCount() > 1)
                return;

            Entity created = em.CreateEntity(typeof(MatchStateSingleton));
            em.SetComponentData(created, new MatchStateSingleton
            {
                MatchStarted = true,
                MatchTimer = matchTimer,
                WinningTeam = team,
                GameState = 2,
            });
        }
    }
}
