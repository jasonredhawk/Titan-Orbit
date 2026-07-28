using TitanOrbit.Core;
using Unity.Entities;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Flushes a one-frame-deferred <see cref="ClientTeamFlowState.ConfirmTeamChoice"/> after
    /// TeamChoice / rejoin success. Runs first in InitializationSystemGroup so the confirm frame
    /// starts with suppress cleared only after the TeamChoiceResult Instantiates window has ended.
    /// <para>
    /// [TITAN-ORBIT] Player.log 2026-07-28: Arm + same-frame Confirm still Crash!!!'d. Deferring
    /// Confirm keeps <see cref="ClientTeamFlowState.ShouldSuppressLocalPlayerControl"/> true for
    /// the rest of the TeamChoiceResult frame while <see cref="ClientJoinSettleCache.ArmPostTeamChoiceHold"/>
    /// already published <see cref="ClientJoinSettleCache.GhostSpawnBacklog"/>.
    /// </para>
    /// World: ClientSimulation.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(InitializationSystemGroup), OrderFirst = true)]
    public partial struct ClientDeferredTeamChoiceConfirmSystem : ISystem
    {
        /// <summary>
        /// Applies a queued deferred Confirm at the start of the next client frame.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // --- Flush deferred Join Team / resume unlock ---
            // [TITAN-ORBIT] No ECS gathers here — managed state only.
            if (!ClientTeamFlowState.HasDeferredTeamChoiceConfirmPending)
                return;

            ClientTeamFlowState.FlushDeferredConfirmTeamChoice();
            UnityEngine.Debug.Log(
                "[TeamChoiceResult] Deferred Confirm flushed — local ship control unlocked.");
        }
    }
}
