using TitanOrbit;
using TitanOrbit.Core;
using TitanOrbit.Data;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [NETCODE] Server-only team assignment and player ship spawn. Handles
    /// <see cref="RequestTeamCommand"/> RPCs from clients: validates roster caps, spawns ship ghost,
    /// replies with <see cref="TeamChoiceResultRpc"/>. Sets CommandTarget on the connection so NetCode
    /// routes input to the new ship. Paired with <see cref="TeamChoiceResultClientSystem"/>.
    /// <para>
    /// Managed <see cref="SystemBase"/> (not Burst ISystem) so Local Host can apply
    /// <see cref="ClientTeamFlowState"/> directly — server→client RPC IPC drops under Instantiates
    /// load (Editor: ship exists, Join Team times out). Request side already injects onto ServerWorld
    /// via <c>TitanOrbitSessionManager.TryEnqueueLocalHostTeamRequest</c>.
    /// </para>
    /// World: ServerSimulation. Group: SimulationSystemGroup.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial class TeamManagementSystem : SystemBase
    {
        /// <summary>[ECS/DOTS] Require TeamStateSingleton before processing team picks.</summary>
        protected override void OnCreate()
        {
            RequireForUpdate<TeamStateSingleton>();
        }

        /// <summary>
        /// [NETCODE] Processes pending RequestTeamCommand RPCs: assign team, spawn ship, send result.
        /// </summary>
        protected override void OnUpdate()
        {
            var em = EntityManager;
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            // --- Drain team-pick RPC queue ---
            // [NETCODE] ReceiveRpcCommandRequest pairs each RPC entity with its source connection.
            // Local Host may inject these directly (no IPC) — see TitanOrbitSessionManager.RequestTeam.
            foreach (var (cmd, req, entity) in SystemAPI
                         .Query<RefRO<RequestTeamCommand>, RefRO<ReceiveRpcCommandRequest>>()
                         .WithEntityAccess())
            {
                int networkId = cmd.ValueRO.NetworkId;
                if (networkId == 0 && em.HasComponent<NetworkId>(req.ValueRO.SourceConnection))
                    networkId = em.GetComponentData<NetworkId>(req.ValueRO.SourceConnection).Value;

                var connection = req.ValueRO.SourceConnection;
                var requested = (TeamId)cmd.ValueRO.RequestedTeam;

                // [TITAN-ORBIT] Log before spawn so lost-RPC hangs are distinguishable from spawn failures.
                Debug.Log($"[TeamManagementSystem] Received RequestTeam networkId={networkId} team={requested}.");

                ecb.DestroyEntity(entity);

                // [NETCODE] Duplicate team RPC (double-click / retry / auto-pick) — acknowledge so
                // client UI advances. Do not spawn a second ship for the same NetworkId.
                if (TryGetShipTeamForNetworkId(networkId, out var existingTeam))
                {
                    Debug.Log(
                        $"[TeamManagementSystem] TeamChoice ack existing ship networkId={networkId} " +
                        $"team={existingTeam} (no new spawn).");
                    bool hasExistingPos = TryGetOwnedShipSpawnPos(networkId, out float3 existingPos);
                    SendTeamChoiceResult(
                        ecb, connection, networkId, existingTeam, success: true, default,
                        existingPos, hasExistingPos);
                    continue;
                }

                var teamState = SystemAPI.GetSingletonRW<TeamStateSingleton>();
                bool ok = true;
                FixedString128Bytes message = default;
                if (MatchPlayerShipStore.HasSavedShip(networkId))
                {
                    ok = false;
                    message = "Resume or abandon your saved ship first.";
                }
                else
                    ok = TryAssignTeam(em, ref teamState.ValueRW, requested, out message);
                float3 spawnedPos = float3.zero;
                bool hasSpawnedPos = false;

                if (ok)
                {
                    ok = TrySpawnPlayerShip(em, ecb, connection, networkId, requested, out spawnedPos);
                    hasSpawnedPos = ok;
                }

                if (!ok)
                {
                    if (!message.IsEmpty)
                        Debug.LogWarning(
                            $"[TeamManagementSystem] Team assign failed for networkId={networkId}: {message}");
                    else
                        Debug.LogError(
                            $"[TeamManagementSystem] Cannot spawn ship for networkId={networkId}: " +
                            "GamePrefabs.Ship is missing.");
                }

                SendTeamChoiceResult(
                    ecb,
                    connection,
                    networkId,
                    ok ? requested : TeamId.None,
                    success: ok,
                    message,
                    spawnedPos,
                    hasSpawnedPos);
            }

            ecb.Playback(em);
            ecb.Dispose();
        }

        /// <summary>
        /// [NETCODE] Delivers team-pick result to the client.
        /// Local Host applies <see cref="ClientTeamFlowState"/> directly (no ClientWorld entity inject —
        /// cross-world CreateEntity was ignored by the client RPC drain). Dedicated uses SendRpc
        /// and includes the home-ring spawn pose for diagnostics / UI (client does not Instantiates a hull).
        /// </summary>
        /// <param name="ecb">Playback buffer for the RPC entity (dedicated path).</param>
        /// <param name="connection">Client connection that sent RequestTeam.</param>
        /// <param name="networkId">Owning NetCode id.</param>
        /// <param name="team">Assigned team, or None on failure.</param>
        /// <param name="success">True when team assign + ship Instantiates succeeded.</param>
        /// <param name="message">Rejection text for lobby UI (empty on success).</param>
        /// <param name="spawnPos">Interior home-planet spawn written to the ship LocalTransform.</param>
        /// <param name="hasSpawnPos">True when <paramref name="spawnPos"/> is the server spawn.</param>
        static void SendTeamChoiceResult(
            EntityCommandBuffer ecb,
            Entity connection,
            int networkId,
            TeamId team,
            bool success,
            FixedString128Bytes message,
            float3 spawnPos,
            bool hasSpawnPos)
        {
            // --- Local Host: apply client flow state immediately (no IPC) ---
            if (TryApplyLocalHostTeamChoiceResult(
                    networkId, team, success, message, spawnPos, hasSpawnPos))
                return;

            // --- Dedicated / remote: normal server → client RPC ---
            // [TITAN-ORBIT] Spawn pose stays on this RPC for logs / late diagnostics. The client
            // does not Instantiates a predicted hull — GhostReceive delivers this server ship.
            var resultEntity = ecb.CreateEntity();
            ecb.AddComponent(resultEntity, new TeamChoiceResultRpc
            {
                NetworkId = networkId,
                AssignedTeam = (byte)(success ? team : TeamId.None),
                Success = (byte)(success ? 1 : 0),
                HasSpawnPos = (byte)(success && hasSpawnPos ? 1 : 0),
                SpawnPosition = success && hasSpawnPos ? spawnPos : float3.zero,
                Message = message,
            });
            ecb.AddComponent(resultEntity, new SendRpcCommandRequest { TargetConnection = connection });
        }

        /// <summary>
        /// [TITAN-ORBIT] When ClientWorld + ServerWorld share a process, mirror
        /// <see cref="TeamChoiceResultClientSystem"/> success/failure onto
        /// <see cref="ClientTeamFlowState"/> without relying on RPC transport.
        /// </summary>
        /// <returns>True when Local Host path applied (caller should skip SendRpc).</returns>
        static bool TryApplyLocalHostTeamChoiceResult(
            int networkId,
            TeamId team,
            bool success,
            FixedString128Bytes message,
            float3 spawnPos,
            bool hasSpawnPos)
        {
            var client = ClientServerBootstrap.ClientWorld;
            var server = ClientServerBootstrap.ServerWorld;
            if (client == null || !client.IsCreated || server == null || !server.IsCreated)
                return false;

            // Only the in-process host client may skip SendRpc. MPPM / LAN Player 2 is a
            // different NetworkId — swallowing their result left Join Team stuck on the clone
            // (host already confirmed, so this used to return true and never deliver the RPC).
            if (!TryReadClientWorldNetworkId(client, out int localId) || localId != networkId)
            {
                Debug.Log(
                    $"[TeamManagementSystem] TeamChoiceResult will SendRpc (remote client) " +
                    $"networkId={networkId} localHostId={localId}.");
                return false;
            }

            // Already confirmed / deferred — ignore duplicate retries.
            if (ClientTeamFlowState.TeamChoiceConfirmed
                || ClientTeamFlowState.HasDeferredTeamChoiceConfirmPending)
            {
                Debug.Log(
                    $"[TeamManagementSystem] Local Host TeamChoiceResult ignored (already confirmed/pending) " +
                    $"networkId={networkId} team={team}.");
                return true;
            }

            if (success)
            {
                // Same sequence as TeamChoiceResultClientSystem.LogResult (join-crash Instantiates hold).
                LocalShipEntitySeed.PrepareForTeamChoiceShip();
                ClientTeamFlowState.LatchTeamChoiceSuccess(team, spawnPos, hasSpawnPos);
                ClientJoinSettleCache.ArmPostTeamChoiceHold();
                ClientTeamFlowState.RequestDeferredConfirmTeamChoice();

                // --- No client predicted Instantiates ---
                // [TITAN-ORBIT] GhostReceive delivers this server hull. Instantiating a fake
                // OwnerPredicted ship on ClientWorld produced a visible hull that could not move.
                // GhostConnectionPosition + CommandTarget are set in TrySpawnPlayerShip so the
                // first snapshot is not starved at the origin.

                Debug.Log(
                    $"[TeamManagementSystem] Local Host TeamChoiceResult applied to ClientTeamFlowState " +
                    $"(networkId={networkId} team={team}). Confirm waits for GhostReceive owner ship.");
            }
            else
            {
                ClientTeamFlowState.NotifyTeamPickRejected(message.ToString());
                Debug.LogWarning(
                    $"[TeamManagementSystem] Local Host TeamChoiceResult failed networkId={networkId}: {message}");
            }

            return true;
        }

        /// <summary>[NETCODE] True if this network id already owns a ship ghost; returns assigned team.</summary>
        bool TryGetShipTeamForNetworkId(int networkId, out TeamId team)
        {
            team = TeamId.None;
            if (networkId == 0)
                return false;

            foreach (var (owner, shipState) in SystemAPI.Query<RefRO<GhostOwner>, RefRO<ShipState>>().WithAll<ShipTag>())
            {
                if (owner.ValueRO.NetworkId != networkId)
                    continue;

                team = shipState.ValueRO.Team;
                return true;
            }

            return false;
        }

        /// <summary>
        /// [TITAN-ORBIT] Validates team choice against ActiveTeamCount, live planet ownership,
        /// and MaxPlayersPerTeam. A team with zero owned planets stays listed on Join Team
        /// but cannot be joined — there is nowhere to spawn.
        /// </summary>
        static bool TryAssignTeam(
            EntityManager em,
            ref TeamStateSingleton team,
            TeamId requested,
            out FixedString128Bytes message)
        {
            message = default;
            if (requested == TeamId.None || (int)requested > team.ActiveTeamCount)
            {
                message = "Invalid team.";
                return false;
            }

            // --- No friendly world: reject before the roster count increments ---
            // [TITAN-ORBIT] Join Team still shows the card. Ownership is live PlanetState,
            // not the baked home slot (a captured home with other worlds still allows join).
            if (!ShipHomeSpawnLogic.TeamOwnsAnyPlanet(em, requested))
            {
                message = "This team has no planets.";
                return false;
            }

            int count = GetTeamCount(team, requested);
            if (count >= team.MaxPlayersPerTeam)
            {
                message = "Team full.";
                return false;
            }

            SetTeamCount(ref team, requested, count + 1);
            return true;
        }

        /// <summary>[TITAN-ORBIT] Reads per-team player count from TeamStateSingleton.</summary>
        static int GetTeamCount(TeamStateSingleton team, TeamId t)
        {
            switch (t)
            {
                case TeamId.TeamA: return team.TeamACount;
                case TeamId.TeamB: return team.TeamBCount;
                case TeamId.TeamC: return team.TeamCCount;
                case TeamId.TeamD: return team.TeamDCount;
                case TeamId.TeamE: return team.TeamECount;
                default: return 0;
            }
        }

        /// <summary>[TITAN-ORBIT] Writes per-team player count after successful assignment.</summary>
        static void SetTeamCount(ref TeamStateSingleton team, TeamId t, int value)
        {
            switch (t)
            {
                case TeamId.TeamA: team.TeamACount = value; break;
                case TeamId.TeamB: team.TeamBCount = value; break;
                case TeamId.TeamC: team.TeamCCount = value; break;
                case TeamId.TeamD: team.TeamDCount = value; break;
                case TeamId.TeamE: team.TeamECount = value; break;
            }
        }

        /// <summary>
        /// [NETCODE] Instantiates ship prefab from GhostCollection (immediate EntityManager),
        /// sets team/state, assigns GhostOwner and CommandTarget, arms elevated GhostSend grace.
        /// <para>
        /// [TITAN-ORBIT] Debug 1af271: deferred <c>ecb.Instantiate(GamePrefabs.Ship)</c> left
        /// <c>usedCollectionPrefab=false</c> and clients stuck at Instantiates=map-meta with no hull.
        /// GhostCollection Instantiates + same-tick EM Instantiates matches map planet spawn.
        /// </para>
        /// </summary>
        bool TrySpawnPlayerShip(
            EntityManager em,
            EntityCommandBuffer ecb,
            Entity connection,
            int networkId,
            TeamId team,
            out float3 spawnPos)
        {
            spawnPos = float3.zero;
            // ecb stays in the signature for callers. Spawn is immediate so GhostSend sees the hull
            // the same tick — deferred Instantiates left clients stuck at map-meta with no ship.
            _ = ecb;

            int hz = 0;
            if (SystemAPI.TryGetSingleton<ClientServerTickRate>(out var tickRate))
                hz = tickRate.SimulationTickRate;
            double orbitElapsed = SystemAPI.TryGetSingleton<NetworkTime>(out var networkTime)
                ? PlanetGemMoonOrbitClock.GetElapsedSeconds(networkTime, hz, includeTickFraction: false)
                : SystemAPI.Time.ElapsedTime;

            return PlayerShipSpawn.TrySpawn(em, connection, networkId, team, orbitElapsed, out _, out spawnPos);
        }

        /// <summary>In-process ClientWorld NetworkId, or false when this process is server-only / not in-game.</summary>
        static bool TryReadClientWorldNetworkId(World client, out int networkId)
        {
            networkId = 0;
            if (client == null || !client.IsCreated)
                return false;

            var em = client.EntityManager;
            using var ids = em.CreateEntityQuery(
                    ComponentType.ReadOnly<NetworkStreamConnection>(),
                    ComponentType.ReadOnly<NetworkStreamInGame>(),
                    ComponentType.ReadOnly<NetworkId>())
                .ToComponentDataArray<NetworkId>(Allocator.Temp);
            if (ids.Length == 0 || ids[0].Value <= 0)
                return false;

            networkId = ids[0].Value;
            return true;
        }

        /// <summary>Reads LocalTransform of the ship owned by <paramref name="networkId"/> when present.</summary>
        static bool TryGetOwnedShipSpawnPos(int networkId, out float3 spawnPos)
        {
            spawnPos = float3.zero;
            if (networkId <= 0)
                return false;

            var server = ClientServerBootstrap.ServerWorld;
            if (server == null || !server.IsCreated)
                return false;

            var em = server.EntityManager;
            using var query = em.CreateEntityQuery(
                ComponentType.ReadOnly<GhostOwner>(),
                ComponentType.ReadOnly<ShipTag>(),
                ComponentType.ReadOnly<LocalTransform>());
            using var entities = query.ToEntityArray(Allocator.Temp);
            using var owners = query.ToComponentDataArray<GhostOwner>(Allocator.Temp);
            for (int i = 0; i < owners.Length; i++)
            {
                if (owners[i].NetworkId != networkId)
                    continue;
                spawnPos = em.GetComponentData<LocalTransform>(entities[i]).Position;
                return true;
            }

            return false;
        }
    }
}
