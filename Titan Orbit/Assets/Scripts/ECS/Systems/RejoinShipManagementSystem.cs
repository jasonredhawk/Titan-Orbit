using TitanOrbit.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(TeamManagementSystem))]
    public partial struct RejoinShipManagementSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<TeamStateSingleton>();
        }

        public void OnUpdate(ref SystemState state)
        {
            var em = state.EntityManager;
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (req, entity) in SystemAPI.Query<RefRO<ReceiveRpcCommandRequest>>()
                         .WithAll<ResumeExistingShipCommand>().WithEntityAccess())
            {
                ecb.DestroyEntity(entity);
                HandleResume(ref state, em, ecb, req.ValueRO.SourceConnection);
            }

            foreach (var (req, entity) in SystemAPI.Query<RefRO<ReceiveRpcCommandRequest>>()
                         .WithAll<AbandonShipForRejoinCommand>().WithEntityAccess())
            {
                ecb.DestroyEntity(entity);
                HandleAbandon(ref state, em, ecb, req.ValueRO.SourceConnection);
            }

            ecb.Playback(em);
            ecb.Dispose();
        }

        void HandleResume(ref SystemState state, EntityManager em, EntityCommandBuffer ecb, Entity connection)
        {
            if (!TryGetNetworkId(em, connection, out int networkId))
            {
                SendResult(ecb, connection, success: false, choice: 1, team: TeamId.None, "Missing network id.");
                return;
            }

            if (!TryFindShipForNetworkId(ref state, networkId, out Entity ship, out ShipState shipState))
            {
                SendResult(ecb, connection, success: false, choice: 1, team: TeamId.None, "No saved ship found.");
                return;
            }

            if (shipState.Team == TeamId.None || shipState.AwaitingTeamSelection)
            {
                SendResult(ecb, connection, success: false, choice: 1, team: TeamId.None, "Saved ship is not active.");
                return;
            }

            var commandTarget = new CommandTarget { targetEntity = ship };
            if (em.HasComponent<CommandTarget>(connection))
                ecb.SetComponent(connection, commandTarget);
            else
                ecb.AddComponent(connection, commandTarget);

            SendResult(ecb, connection, success: true, choice: 1, team: shipState.Team, default);
            LogResume(networkId, shipState.Team);
        }

        void HandleAbandon(ref SystemState state, EntityManager em, EntityCommandBuffer ecb, Entity connection)
        {
            if (!TryGetNetworkId(em, connection, out int networkId))
            {
                SendResult(ecb, connection, success: false, choice: 2, team: TeamId.None, "Missing network id.");
                return;
            }

            if (!TryFindShipForNetworkId(ref state, networkId, out Entity ship, out ShipState shipState))
            {
                ClearCommandTarget(em, ecb, connection);
                SendResult(ecb, connection, success: true, choice: 2, team: TeamId.None, default);
                return;
            }

            if (shipState.Team != TeamId.None)
            {
                var teamState = SystemAPI.GetSingletonRW<TeamStateSingleton>();
                DecrementTeamCount(ref teamState.ValueRW, shipState.Team);
            }

            ecb.DestroyEntity(ship);
            ClearCommandTarget(em, ecb, connection);
            SendResult(ecb, connection, success: true, choice: 2, team: TeamId.None, default);
            LogAbandon(networkId, shipState.Team);
        }

        static bool TryGetNetworkId(EntityManager em, Entity connection, out int networkId)
        {
            networkId = 0;
            if (connection == Entity.Null || !em.Exists(connection) || !em.HasComponent<NetworkId>(connection))
                return false;
            networkId = em.GetComponentData<NetworkId>(connection).Value;
            return networkId > 0;
        }

        bool TryFindShipForNetworkId(ref SystemState state, int networkId, out Entity ship, out ShipState shipState)
        {
            ship = Entity.Null;
            shipState = default;
            foreach (var (owner, stateComp, entity) in SystemAPI.Query<RefRO<GhostOwner>, RefRO<ShipState>>()
                         .WithAll<ShipTag>().WithEntityAccess())
            {
                if (owner.ValueRO.NetworkId != networkId)
                    continue;
                ship = entity;
                shipState = stateComp.ValueRO;
                return true;
            }

            return false;
        }

        static void ClearCommandTarget(EntityManager em, EntityCommandBuffer ecb, Entity connection)
        {
            if (connection == Entity.Null || !em.Exists(connection) || !em.HasComponent<CommandTarget>(connection))
                return;
            ecb.SetComponent(connection, new CommandTarget { targetEntity = Entity.Null });
        }

        static void DecrementTeamCount(ref TeamStateSingleton team, TeamId teamId)
        {
            switch (teamId)
            {
                case TeamId.TeamA: team.TeamACount = Unity.Mathematics.math.max(0, team.TeamACount - 1); break;
                case TeamId.TeamB: team.TeamBCount = Unity.Mathematics.math.max(0, team.TeamBCount - 1); break;
                case TeamId.TeamC: team.TeamCCount = Unity.Mathematics.math.max(0, team.TeamCCount - 1); break;
                case TeamId.TeamD: team.TeamDCount = Unity.Mathematics.math.max(0, team.TeamDCount - 1); break;
                case TeamId.TeamE: team.TeamECount = Unity.Mathematics.math.max(0, team.TeamECount - 1); break;
            }
        }

        static void SendResult(
            EntityCommandBuffer ecb,
            Entity connection,
            bool success,
            byte choice,
            TeamId team,
            FixedString128Bytes message)
        {
            var resultEntity = ecb.CreateEntity();
            ecb.AddComponent(resultEntity, new RejoinShipResultRpc
            {
                Success = (byte)(success ? 1 : 0),
                Choice = choice,
                AssignedTeam = (byte)team,
                Message = message,
            });
            ecb.AddComponent(resultEntity, new SendRpcCommandRequest { TargetConnection = connection });
        }

        [Unity.Burst.BurstDiscard]
        static void LogResume(int networkId, TeamId team)
        {
            UnityEngine.Debug.Log($"[RejoinShipManagement] Resumed ship for networkId={networkId} team={team}.");
        }

        [Unity.Burst.BurstDiscard]
        static void LogAbandon(int networkId, TeamId team)
        {
            UnityEngine.Debug.Log($"[RejoinShipManagement] Abandoned ship for networkId={networkId} team={team}.");
        }
    }
}
