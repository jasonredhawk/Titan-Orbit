using TitanOrbit.Core;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct RejoinShipResultClientSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            var ecb = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (result, entity) in SystemAPI.Query<RefRO<RejoinShipResultRpc>>().WithEntityAccess())
            {
                ApplyResult(result.ValueRO);
                ecb.DestroyEntity(entity);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }

        [BurstDiscard]
        static void ApplyResult(RejoinShipResultRpc rpc)
        {
            if (rpc.Success == 0)
            {
                UnityEngine.Debug.LogWarning("[RejoinShipResult] Failed: " + rpc.Message);
                if (rpc.Choice == 1)
                    ClientTeamFlowState.ResetRejoinChoiceToPending();
                return;
            }

            if (rpc.Choice == 1)
            {
                ClientTeamFlowState.ChooseUseExistingShip();
                ClientTeamFlowState.ConfirmTeamChoice();
                UnityEngine.Debug.Log("[RejoinShipResult] Resumed existing ship on team " + (TeamId)rpc.AssignedTeam + ".");
                return;
            }

            if (rpc.Choice == 2)
            {
                ClientTeamFlowState.ChooseStartFreshShip();
                UnityEngine.Debug.Log("[RejoinShipResult] Abandoned saved ship — pick a new team.");
            }
        }
    }
}
