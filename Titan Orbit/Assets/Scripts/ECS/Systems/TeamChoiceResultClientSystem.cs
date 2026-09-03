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
    /// and input suppression know whether spawn succeeded. Does <b>not</b> Instantiates a client
    /// predicted hull — Confirm waits for GhostReceive of the server ship.
    /// World: ClientSimulation. Paired with TeamManagementSystem on the server.
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
            // --- Duplicate / Local Host already applied ---
            // [TITAN-ORBIT] Local Host applies ClientTeamFlowState from TeamManagementSystem
            // (no RPC). A late SendRpc must not re-Arm or ClearTeamPickRequest.
            if (ClientTeamFlowState.TeamChoiceConfirmed
                || ClientTeamFlowState.HasDeferredTeamChoiceConfirmPending)
            {
                UnityEngine.Debug.Log(
                    $"[TeamChoiceResult] Ignored duplicate (already confirmed/pending) " +
                    $"networkId={rpc.NetworkId} success={rpc.Success}.");
                return;
            }

            if (rpc.Success != 0)
            {
                // [TITAN-ORBIT] Re-Arm ship-query hold, then DEFER Confirm until that hold expires.
                // RequestTeam already pre-Arms on Join Team click (Player.log 2026-07-31 race:
                // ship Instantiates before this system runs). Re-Arm here so Deferred Confirm
                // stays suppressed for a full PostTeamChoiceHoldFrames window after the ack.
                // Player.log 2026-07-28: same-frame Confirm Crash!!!'d.
                // Player.log 2026-07-30: next-frame-only Confirm flush still Crash!!!'d.
                // Arm publishes GhostSpawnBacklog immediately; deferred Confirm keeps suppress on.
                // Flush: ClientDeferredTeamChoiceConfirmSystem (waits for hold clear).
                var team = (TeamId)rpc.AssignedTeam;
                bool hasSpawn = rpc.HasSpawnPos != 0;
                LocalShipEntitySeed.PrepareForTeamChoiceShip();
                ClientTeamFlowState.LatchTeamChoiceSuccess(team, rpc.SpawnPosition, hasSpawn);
                ClientJoinSettleCache.ArmPostTeamChoiceHold();
                ClientTeamFlowState.RequestDeferredConfirmTeamChoice();

                // --- Wait for GhostReceive of the server ship ---
                // [TITAN-ORBIT] Do not Instantiates a ClientWorld predicted hull. Overlay stays
                // until LocalShipEntitySeed sees the owner ghost (real RTT), then Confirm flushes.
                // Spawn pose is latched so an ownerless Instantiates can snap off prefab origin.

                UnityEngine.Debug.Log(
                    $"[TeamChoiceResult] Assigned to {team} (networkId={rpc.NetworkId}" +
                    (hasSpawn ? $", spawn={rpc.SpawnPosition}" : "") +
                    "). Confirm deferred until GhostReceive owner ship + Instantiates hold (join-crash guard).");
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
