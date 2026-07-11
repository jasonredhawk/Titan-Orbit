using TitanOrbit.Core;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [NETCODE] Server-only team assignment and player ship spawn. Handles
    /// <see cref="RequestTeamCommand"/> RPCs from clients: validates roster caps, spawns ship ghost,
    /// replies with <see cref="TeamChoiceResultRpc"/>. Sets CommandTarget on the connection so NetCode
    /// routes input to the new ship. Paired with <see cref="TeamChoiceResultClientSystem"/>.
    /// World: ServerSimulation. Group: SimulationSystemGroup.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct TeamManagementSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<TeamStateSingleton>();
        }

        /// <summary>
        /// [NETCODE] Processes pending RequestTeamCommand RPCs: assign team, spawn ship, send result RPC.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            var em = state.EntityManager;
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            // --- Drain team-pick RPC queue ---
            // [NETCODE] ReceiveRpcCommandRequest pairs each RPC entity with its source connection.
            foreach (var (cmd, req, entity) in SystemAPI.Query<RefRO<RequestTeamCommand>, RefRO<ReceiveRpcCommandRequest>>().WithEntityAccess())
            {
                int networkId = cmd.ValueRO.NetworkId;
                if (networkId == 0 && em.HasComponent<NetworkId>(req.ValueRO.SourceConnection))
                    networkId = em.GetComponentData<NetworkId>(req.ValueRO.SourceConnection).Value;

                var connection = req.ValueRO.SourceConnection;
                var requested = (TeamId)cmd.ValueRO.RequestedTeam;

                ecb.DestroyEntity(entity);

                // [NETCODE] Duplicate team RPC (double-click / retry) — acknowledge so client UI advances.
                if (TryGetShipTeamForNetworkId(ref state, networkId, out var existingTeam))
                {
                    SendTeamChoiceResult(ecb, connection, networkId, existingTeam, success: true, default);
                    continue;
                }

                var teamState = SystemAPI.GetSingletonRW<TeamStateSingleton>();
                bool ok = TryAssignTeam(ref teamState.ValueRW, requested, out var message);

                if (ok)
                    ok = TrySpawnPlayerShip(ref state, em, ecb, connection, networkId, requested);

                if (!ok)
                {
                    if (!message.IsEmpty)
                        LogTeamAssignFailed(networkId, message);
                    else
                        LogSpawnFailed(networkId);
                }

                var resultEntity = ecb.CreateEntity();
                ecb.AddComponent(resultEntity, new TeamChoiceResultRpc
                {
                    NetworkId = networkId,
                    AssignedTeam = (byte)(ok ? requested : TeamId.None),
                    Success = (byte)(ok ? 1 : 0),
                    Message = message,
                });
                ecb.AddComponent(resultEntity, new SendRpcCommandRequest { TargetConnection = connection });
            }

            ecb.Playback(em);
            ecb.Dispose();
        }

        /// <summary>[NETCODE] Queues a team-pick result RPC back to the requesting connection.</summary>
        static void SendTeamChoiceResult(
            EntityCommandBuffer ecb,
            Entity connection,
            int networkId,
            TeamId team,
            bool success,
            FixedString128Bytes message)
        {
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

        /// <summary>[NETCODE] True if this network id already owns a ship ghost; returns assigned team.</summary>
        bool TryGetShipTeamForNetworkId(ref SystemState state, int networkId, out TeamId team)
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
        /// [NETCODE] Instantiates ship prefab, sets team/state, assigns GhostOwner and CommandTarget.
        /// </summary>
        bool TrySpawnPlayerShip(ref SystemState state, EntityManager em, EntityCommandBuffer ecb, Entity connection, int networkId, TeamId team)
        {
            if (!SystemAPI.TryGetSingleton<GamePrefabs>(out var prefabs) || prefabs.Ship == Entity.Null)
                return false;

            // --- Resolve spawn position near home planet from map layout ---
            float3 spawnPos = float3.zero;
            if (SystemAPI.TryGetSingletonBuffer<MapLayoutEntryElement>(out var layout))
            {
                for (int i = 0; i < layout.Length; i++)
                {
                    var entry = layout[i];
                    if (entry.EntityKind == 1 && entry.Team == team)
                    {
                        spawnPos = entry.Position + new float3(20f, 0f, 0f);
                        break;
                    }
                }
            }

            var ship = ecb.Instantiate(prefabs.Ship);
            ecb.SetComponent(ship, new ShipState
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
            ecb.SetComponent(ship, LocalTransform.FromPosition(spawnPos));
            if (em.HasComponent<GhostOwner>(prefabs.Ship))
                ecb.SetComponent(ship, new GhostOwner { NetworkId = networkId });
            else
                ecb.AddComponent(ship, new GhostOwner { NetworkId = networkId });

            ecb.AddComponent(ship, new ShipAttributeUpgradeState());

            var commandTarget = new CommandTarget { targetEntity = ship };
            if (em.HasComponent<CommandTarget>(connection))
                ecb.SetComponent(connection, commandTarget);
            else
                ecb.AddComponent(connection, commandTarget);

            LogSpawned(networkId, team, spawnPos);
            return true;
        }

        [BurstDiscard]
        static void LogSpawned(int networkId, TeamId team, float3 spawnPos)
        {
            UnityEngine.Debug.Log($"[TeamManagementSystem] Spawned ship for networkId={networkId} team={team} at {spawnPos}.");
        }

        [BurstDiscard]
        static void LogTeamAssignFailed(int networkId, FixedString128Bytes message)
        {
            UnityEngine.Debug.LogWarning($"[TeamManagementSystem] Team assign failed for networkId={networkId}: {message}");
        }

        [BurstDiscard]
        static void LogSpawnFailed(int networkId)
        {
            UnityEngine.Debug.LogError($"[TeamManagementSystem] Cannot spawn ship for networkId={networkId}: GamePrefabs.Ship is missing.");
        }
    }
}
