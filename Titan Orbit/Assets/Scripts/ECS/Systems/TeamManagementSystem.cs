using TitanOrbit;
using TitanOrbit.Core;
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
                bool ok = TryAssignTeam(ref teamState.ValueRW, requested, out var message);
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
        /// cross-world CreateEntity was ignored by the client RPC drain). Dedicated uses SendRpc.
        /// </summary>
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
            // ClientPredictedShipSpawnSystem finds home ring when spawn pose is not on the RPC.
            var resultEntity = ecb.CreateEntity();
            ecb.AddComponent(resultEntity, new TeamChoiceResultRpc
            {
                NetworkId = networkId,
                AssignedTeam = (byte)(success ? team : TeamId.None),
                Success = (byte)(success ? 1 : 0),
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
                ClientJoinSettleCache.ArmPostTeamChoiceHold();
                ClientTeamFlowState.RequestDeferredConfirmTeamChoice();

                // --- Queue ClientWorld predicted Instantiates (GhostReceive often never sees hull) ---
                // [TITAN-ORBIT] Editor.log: server ghostId assigned, InstantiatesSession stuck at
                // map meta-N, placeholders=0. Client predicted spawn unblocks presentation; NetCode
                // classification adopts the server ghost when its snapshot arrives.
                ClientPredictedShipSpawnRequest.Request(networkId, team, spawnPos, hasSpawnPos);

                Debug.Log(
                    $"[TeamManagementSystem] Local Host TeamChoiceResult applied to ClientTeamFlowState " +
                    $"(networkId={networkId} team={team}). Confirm deferred until Instantiates hold expires.");
            }
            else
            {
                ClientTeamFlowState.ClearTeamPickRequest();
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
        /// [TITAN-ORBIT] Validates team choice against ActiveTeamCount and MaxPlayersPerTeam cap.
        /// </summary>
        static bool TryAssignTeam(ref TeamStateSingleton team, TeamId requested, out FixedString128Bytes message)
        {
            message = default;
            if (requested == TeamId.None || (int)requested > team.ActiveTeamCount)
            {
                message = "Invalid team.";
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
            if (!SystemAPI.TryGetSingleton<GamePrefabs>(out var prefabs) || prefabs.Ship == Entity.Null)
                return false;

            // --- Resolve GhostCollection ship prefab (preferred over baked GamePrefabs entity) ---
            Entity shipPrefab = ResolveGhostCollectionShipPrefab(em, prefabs.Ship, out bool usedCollection);
            if (shipPrefab == Entity.Null || !em.Exists(shipPrefab))
                return false;

            // --- Resolve spawn on home orbit ring (outside moon dock zone) ---
            // [TITAN-ORBIT] Same helper as death respawn / rejoin — random ring angle, not fixed +X.
            int hz = 0;
            if (SystemAPI.TryGetSingleton<ClientServerTickRate>(out var tickRate))
                hz = tickRate.SimulationTickRate;
            double orbitElapsed = SystemAPI.TryGetSingleton<NetworkTime>(out var networkTime)
                ? PlanetGemMoonOrbitClock.GetElapsedSeconds(networkTime, hz, includeTickFraction: false)
                : SystemAPI.Time.ElapsedTime;
            spawnPos = ShipHomeSpawnLogic.FindHomeSpawnPosition(em, team, orbitElapsed);

            // --- Immediate Instantiates (same tick as CommandTarget + GhostSend grace) ---
            // [NETCODE] ECB Instantiates would leave the hull invisible to GhostSend until playback.
            Entity ship = em.Instantiate(shipPrefab);
            em.SetComponentData(ship, new ShipState
            {
                Health = 100f,
                MaxHealth = 100f,
                Team = team,
                ShipLevel = 1,
                GemCapacity = 50f,
                CurrentEnergy = 50f,
                MaxEnergy = 50f,
                PeopleCapacity = 10,
                AwaitingTeamSelection = false,
            });
            em.SetComponentData(ship, LocalTransform.FromPosition(spawnPos));

            if (em.HasComponent<GhostOwner>(ship))
                em.SetComponentData(ship, new GhostOwner { NetworkId = networkId });
            else
                em.AddComponentData(ship, new GhostOwner { NetworkId = networkId });

            // Prefab often already bakes ShipAttributeUpgradeState — only add when missing.
            if (!em.HasComponent<ShipAttributeUpgradeState>(ship))
                em.AddComponentData(ship, new ShipAttributeUpgradeState());

            var commandTarget = new CommandTarget { targetEntity = ship };
            if (em.HasComponent<CommandTarget>(connection))
                em.SetComponentData(connection, commandTarget);
            else
                em.AddComponentData(connection, commandTarget);

            // --- Point distance-importance at the new hull immediately ---
            // [NETCODE] GhostConnectionPosition often stays at origin until the next
            // TitanOrbitGhostConnectionPositionSystem tick — first ship snapshot can lose to
            // far map resends even with FirstSend bias.
            if (em.HasComponent<GhostConnectionPosition>(connection))
            {
                em.SetComponentData(connection, new GhostConnectionPosition
                {
                    Position = spawnPos,
                    Rotation = quaternion.identity,
                });
            }
            else
            {
                em.AddComponentData(connection, new GhostConnectionPosition
                {
                    Position = spawnPos,
                    Rotation = quaternion.identity,
                });
            }

            // --- Keep GhostSend elevated until the first ship snapshots leave ---
            TitanOrbitGhostSendGrace.ArmShipSpawnGrace();
            TitanOrbitServerShipGhostVerifySystem.Enqueue(ship, networkId);

            int ghostId = 0;
            if (em.HasComponent<GhostInstance>(ship))
                ghostId = em.GetComponentData<GhostInstance>(ship).ghostId;

            Debug.Log(
                $"[TeamManagementSystem] Spawned ship for networkId={networkId} team={team} at {spawnPos} " +
                $"(collectionPrefab={usedCollection}, ghostId={ghostId}).");
            return true;
        }

        /// <summary>
        /// Finds the GhostCollection entry for the ship prefab so SpawnGhostJob can assign a ghost id.
        /// Prefers entity match, then <see cref="GhostType"/> match, else first <see cref="ShipTag"/>.
        /// </summary>
        static Entity ResolveGhostCollectionShipPrefab(
            EntityManager em,
            Entity gamePrefabsShip,
            out bool usedCollection)
        {
            usedCollection = false;
            Entity shipTagFallback = Entity.Null;

            using var collectionQuery = em.CreateEntityQuery(ComponentType.ReadOnly<GhostCollection>());
            if (collectionQuery.IsEmptyIgnoreFilter)
                return gamePrefabsShip;

            Entity collectionEntity = collectionQuery.GetSingletonEntity();
            if (!em.HasBuffer<GhostCollectionPrefab>(collectionEntity))
                return gamePrefabsShip;

            GhostType targetType = default;
            bool hasTargetType = gamePrefabsShip != Entity.Null && em.HasComponent<GhostType>(gamePrefabsShip);
            if (hasTargetType)
                targetType = em.GetComponentData<GhostType>(gamePrefabsShip);

            var buffer = em.GetBuffer<GhostCollectionPrefab>(collectionEntity, isReadOnly: true);
            for (int i = 0; i < buffer.Length; i++)
            {
                Entity candidate = buffer[i].GhostPrefab;
                if (candidate == Entity.Null || !em.Exists(candidate))
                    continue;

                if (candidate == gamePrefabsShip)
                {
                    usedCollection = true;
                    return candidate;
                }

                // GamePrefabs.Ship entity handle often differs from the collection entry — match GhostType.
                if (hasTargetType &&
                    em.HasComponent<GhostType>(candidate) &&
                    em.GetComponentData<GhostType>(candidate) == targetType)
                {
                    usedCollection = true;
                    return candidate;
                }

                if (shipTagFallback == Entity.Null && em.HasComponent<ShipTag>(candidate))
                    shipTagFallback = candidate;
            }

            if (shipTagFallback != Entity.Null)
            {
                usedCollection = true;
                return shipTagFallback;
            }

            return gamePrefabsShip;
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
