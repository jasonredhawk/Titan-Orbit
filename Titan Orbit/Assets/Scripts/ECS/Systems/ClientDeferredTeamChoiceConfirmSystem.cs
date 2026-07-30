using TitanOrbit.Core;
using Unity.Entities;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Flushes a deferred <see cref="ClientTeamFlowState.ConfirmTeamChoice"/> after TeamChoice /
    /// rejoin success. Runs first in InitializationSystemGroup.
    /// <para>
    /// [TITAN-ORBIT] Player.log 2026-07-28: Arm + same-frame Confirm still Crash!!!'d — need at
    /// least one frame of defer so suppress stays on for the TeamChoiceResult Instantiates window.
    /// Player.log 2026-07-30: flushing Confirm on the very next frame still Crash!!!'d —
    /// suppress-only ship gathers opened while GhostSpawn Instantiates the hull. Keep Confirm
    /// (and therefore <see cref="ClientTeamFlowState.ShouldSuppressLocalPlayerControl"/>) deferred
    /// for the full <see cref="ClientJoinSettleCache.ArmPostTeamChoiceHold"/> countdown, not just
    /// one frame. <see cref="ClientJoinSettleCache.ShouldSkipShipEntityQueries"/> stays true the
    /// whole time via hold + deferred-pending.
    /// </para>
    /// World: ClientSimulation.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(InitializationSystemGroup), OrderFirst = true)]
    public partial struct ClientDeferredTeamChoiceConfirmSystem : ISystem
    {
        /// <summary>
        /// Applies a queued deferred Confirm once the post–TeamChoice Instantiates hold has expired.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // --- Nothing queued ---
            if (!ClientTeamFlowState.HasDeferredTeamChoiceConfirmPending)
                return;

            // --- Hold still covering ship Instantiates ---
            // [TITAN-ORBIT] ArmPostTeamChoiceHold runs on the TeamChoiceResult frame (Simulation).
            // This system runs in Initialization the next frames. Do not unlock suppress until the
            // hold countdown finishes — 1-frame-only defer left suppress-only HUD / cache paths
            // open while Instantiates were still unsafe (Player.log 2026-07-30 Crash!!!).
            if (ClientJoinSettleCache.IsPostTeamChoiceHoldActive)
                return;

            // --- Unlock local ship control ---
            // Hold expired (or never armed). GhostSpawnBacklog may still be true from queue /
            // post-ship hold — ShouldSkipShipEntityQueries continues to gate gathers.
            ClientTeamFlowState.FlushDeferredConfirmTeamChoice();
            UnityEngine.Debug.Log(
                "[TeamChoiceResult] Deferred Confirm flushed — local ship control unlocked " +
                $"(skipShip={ClientJoinSettleCache.ShouldSkipShipEntityQueries} " +
                $"backlog={ClientJoinSettleCache.GhostSpawnBacklog}).");
        }
    }
}
