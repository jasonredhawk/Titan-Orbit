using TitanOrbit.Core;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct TeamChoiceResultClientSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
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
                UnityEngine.Debug.LogWarning($"[TeamChoiceResult] Failed: {rpc.Message}");
        }
    }
}
