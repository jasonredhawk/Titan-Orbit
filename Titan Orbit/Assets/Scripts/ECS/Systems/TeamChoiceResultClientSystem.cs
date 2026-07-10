using TitanOrbit.Core;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Client-side handler for <see cref="TeamChoiceResultRpc"/> replies from
    /// <see cref="TeamManagementSystem"/>. Updates local team-pick UI state and logs outcome.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct TeamChoiceResultClientSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
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

        [BurstDiscard]
        static void LogResult(TeamChoiceResultRpc rpc)
        {
            if (rpc.Success != 0)
            {
                ClientTeamFlowState.ConfirmTeamChoice();
                UnityEngine.Debug.Log($"[TeamChoiceResult] Assigned to {(TeamId)rpc.AssignedTeam} (networkId={rpc.NetworkId}).");
            }
            else
            {
                ClientTeamFlowState.ClearTeamPickRequest();
                UnityEngine.Debug.LogWarning($"[TeamChoiceResult] Failed: {rpc.Message}");
            }
        }
    }
}
