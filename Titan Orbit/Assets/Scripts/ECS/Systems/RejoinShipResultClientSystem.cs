using TitanOrbit.Core;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [NETCODE] Client-side handler for <see cref="RejoinShipResultRpc"/> responses from the server.
    /// Updates <see cref="ClientTeamFlowState"/> so UI and input systems know whether the player
    /// resumed an existing ship or chose to start fresh. World: ClientSimulation.
    /// Paired with RejoinShipManagementSystem on the server.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct RejoinShipResultClientSystem : ISystem
    {
        /// <summary>[NETCODE] Consumes RejoinShipResultRpc entities each frame.</summary>
        public void OnUpdate(ref SystemState state)
        {
            // --- Drain RPC queue ---
            var ecb = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (result, entity) in SystemAPI.Query<RefRO<RejoinShipResultRpc>>().WithEntityAccess())
            {
                ApplyResult(result.ValueRO);
                ecb.DestroyEntity(entity);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }

        /// <summary>
        /// [HYBRID] Maps RPC result to client team-flow state machine transitions.
        /// [BurstDiscard] — touches managed Debug and ClientTeamFlowState.
        /// </summary>
        [BurstDiscard]
        static void ApplyResult(RejoinShipResultRpc rpc)
        {
            // --- Failure path ---
            if (rpc.Success == 0)
            {
                UnityEngine.Debug.LogWarning("[RejoinShipResult] Failed: " + rpc.Message);
                if (rpc.Choice == 1)
                    ClientTeamFlowState.ResetRejoinChoiceToPending();
                return;
            }

            // --- Choice 1 = resume existing ship on assigned team ---
            if (rpc.Choice == 1)
            {
                ClientTeamFlowState.ChooseUseExistingShip();
                // Arm + defer Confirm — same TeamChoice Crash!!! guard as TeamChoiceResultClientSystem.
                ClientJoinSettleCache.ArmPostTeamChoiceHold();
                ClientTeamFlowState.RequestDeferredConfirmTeamChoice();
                UnityEngine.Debug.Log(
                    "[RejoinShipResult] Resumed existing ship on team " + (TeamId)rpc.AssignedTeam +
                    ". Confirm deferred until post-TeamChoice Instantiates hold expires (join-crash guard).");
                return;
            }

            // --- Choice 2 = abandon saved ship — show team picker for a fresh start ---
            if (rpc.Choice == 2)
            {
                ClientTeamFlowState.ChooseStartFreshShip();
                UnityEngine.Debug.Log("[RejoinShipResult] Abandoned saved ship — pick a new team.");
            }
        }
    }
}
