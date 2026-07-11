using TitanOrbit.Core;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [NETCODE] Client-side handler for <see cref="TeamChoiceResultRpc"/> replies from
    /// <see cref="TeamManagementSystem"/>. Updates <see cref="ClientTeamFlowState"/> so team-pick UI
    /// and input suppression know whether spawn succeeded. World: ClientSimulation.
    /// Paired with TeamManagementSystem on the server.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct TeamChoiceResultClientSystem : ISystem
    {
        /// <summary>
        /// [NETCODE] Consumes one-shot TeamChoiceResultRpc entities each frame and destroys them.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // --- Drain RPC queue ---
            // [NETCODE] RPC entities are one-shot — consume and destroy each frame.
            var ecb = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (result, entity) in SystemAPI.Query<RefRO<TeamChoiceResultRpc>>().WithEntityAccess())
            {
                LogResult(result.ValueRO);
                ecb.DestroyEntity(entity);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }

        /// <summary>
        /// [HYBRID] Maps RPC success/failure to ClientTeamFlowState transitions and console logs.
        /// [BurstDiscard] — touches managed Debug and ClientTeamFlowState.
        /// </summary>
        [BurstDiscard]
        static void LogResult(TeamChoiceResultRpc rpc)
        {
            if (rpc.Success != 0)
            {
                // [TITAN-ORBIT] Team assigned — unblock local player control and close team picker.
                ClientTeamFlowState.ConfirmTeamChoice();
                UnityEngine.Debug.Log($"[TeamChoiceResult] Assigned to {(TeamId)rpc.AssignedTeam} (networkId={rpc.NetworkId}).");
            }
            else
            {
                // [TITAN-ORBIT] Server rejected pick (full team, invalid team) — allow retry.
                ClientTeamFlowState.ClearTeamPickRequest();
                UnityEngine.Debug.LogWarning($"[TeamChoiceResult] Failed: {rpc.Message}");
            }
        }
    }
}
