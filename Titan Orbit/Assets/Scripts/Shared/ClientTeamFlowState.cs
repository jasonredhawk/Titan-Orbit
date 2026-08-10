using UnityEngine;

namespace TitanOrbit.Core
{
    /// <summary>
    /// Client-only state machine for team pick and dedicated-server rejoin ship-resume flow.
    /// Written by team/rejoin RPC result systems and team UI. Gates local ship control,
    /// tagging, camera, and owned-hull presentation until <see cref="TeamChoiceConfirmed"/>
    /// (Join Team or resume). Not replicated — server uses ShipState flags.
    /// </summary>
    public static class ClientTeamFlowState
    {
        /// <summary>
        /// [UNITY] Domain Reload disabled leaves TeamChoiceConfirmed sticky across Play Mode —
        /// second Join Team then skips Local Host Result apply / predicted spawn. Reset every enter.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void ResetBeforeSceneLoad() => Reset();

        /// <summary>Also clear after assemblies load (Domain Reload on).</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetSubsystemRegistration() => Reset();

        /// <summary>Player decision when reconnecting to a match with a saved ship.</summary>
        public enum RejoinShipChoice
        {
            NotApplicable,
            Pending,
            UseExisting,
            StartFresh,
        }

        public static bool TeamChoiceConfirmed { get; private set; }
        public static RejoinShipChoice RejoinChoice { get; private set; } = RejoinShipChoice.NotApplicable;

        /// <summary>True after the player clicks Join Team — blocks late ship ghosts from triggering rejoin UI.</summary>
        static bool _teamPickRequested;
        static bool _rejoinEligibilityLocked;

        /// <summary>
        /// Last team the player clicked. Used by spawn-wait timeout retry when
        /// <see cref="TeamChoiceResultRpc"/> never arrives (lost RequestTeam).
        /// </summary>
        public static TeamId LastRequestedTeam { get; private set; } = TeamId.None;

        /// <summary>
        /// [TITAN-ORBIT] TeamChoiceResult armed the Instantiates hold but Confirm is waiting until
        /// that hold expires (not merely the next frame). While set,
        /// <see cref="ShouldSuppressLocalPlayerControl"/> stays true so systems that check suppress
        /// before <c>ShouldSkipShipEntityQueries</c> cannot open ship gathers mid-Instantiates.
        /// Flushed by <c>ClientDeferredTeamChoiceConfirmSystem</c> after the hold countdown.
        /// Player.log 2026-07-30: 1-frame-only defer → Crash!!! on Confirm flush.
        /// </summary>
        static bool _deferredConfirmPending;

        public static bool HasRequestedTeamPick => _teamPickRequested;
        public static bool IsRejoinEligibilityLocked => _rejoinEligibilityLocked;

        /// <summary>
        /// True while Join Team / resume ack is latched but Confirm is still deferred through the
        /// post–TeamChoice Instantiates hold (Windows TeamChoice Crash!!! gather guard).
        /// </summary>
        public static bool HasDeferredTeamChoiceConfirmPending => _deferredConfirmPending;

        public static void ConfirmTeamChoice()
        {
            // --- Server acknowledged team — unlock normal play ---
            _deferredConfirmPending = false;
            TeamChoiceConfirmed = true;
            LockRejoinEligibility();
        }

        /// <summary>
        /// Queue Confirm until <c>ClientDeferredTeamChoiceConfirmSystem</c> sees the post–TeamChoice
        /// Instantiates hold expire. Keeps suppress on for that whole Instantiates window.
        /// </summary>
        public static void RequestDeferredConfirmTeamChoice()
        {
            // --- Do not unlock suppress until the Instantiates hold finishes ---
            _deferredConfirmPending = true;
            LockRejoinEligibility();
        }

        /// <summary>
        /// Applies a pending deferred Confirm. No-op when nothing is queued or already confirmed.
        /// </summary>
        public static void FlushDeferredConfirmTeamChoice()
        {
            if (!_deferredConfirmPending)
                return;
            ConfirmTeamChoice();
        }

        public static void Reset()
        {
            // --- Full session reset (disconnect, return to menu) ---
            TeamChoiceConfirmed = false;
            RejoinChoice = RejoinShipChoice.NotApplicable;
            _teamPickRequested = false;
            _rejoinEligibilityLocked = false;
            _deferredConfirmPending = false;
            LastRequestedTeam = TeamId.None;
        }

        /// <summary>Call when the player clicks a team button (before server ack).</summary>
        /// <param name="team">Team the player requested — stored for timeout retry.</param>
        public static void NotifyTeamPickRequested(TeamId team = TeamId.None)
        {
            // --- Optimistic team pick — block late rejoin prompts ---
            _teamPickRequested = true;
            if (team != TeamId.None)
                LastRequestedTeam = team;
            LockRejoinEligibility();
        }

        /// <summary>Allow retry after a failed team RPC without treating the spawned ship as a rejoin.</summary>
        public static void ClearTeamPickRequest()
        {
            // --- RPC failed before confirm — allow another team click ---
            if (TeamChoiceConfirmed)
                return;
            _teamPickRequested = false;
            // Keep LastRequestedTeam so a watchdog can resend the same pick.
        }

        /// <summary>
        /// [TITAN-ORBIT] Confirm succeeded but no local hull arrived (debug 1af271 Instantiates stuck
        /// at map meta-N). Clears confirm + pick latches so UI can leave endless
        /// "Spawning your ship..." and retry Join Team. Keeps <see cref="LastRequestedTeam"/>.
        /// </summary>
        public static void ResetTeamChoiceForShipSpawnFailure()
        {
            TeamChoiceConfirmed = false;
            _deferredConfirmPending = false;
            _teamPickRequested = false;
        }

        static void LockRejoinEligibility() => _rejoinEligibilityLocked = true;

        /// <summary>
        /// Promote to Pending only while the player has not started normal team selection.
        /// Ships spawned after Join Team must not be mistaken for a prior-session rejoin.
        /// </summary>
        public static void TryNotifyRejoinableShip(bool hasRejoinableShip)
        {
            // --- Guard: only promote to Pending during valid rejoin window ---
            if (!hasRejoinableShip || _rejoinEligibilityLocked || TeamChoiceConfirmed || IsRejoinChoiceResolved)
                return;
            if (RejoinChoice == RejoinShipChoice.NotApplicable)
                RejoinChoice = RejoinShipChoice.Pending;
        }

        public static bool IsRejoinChoicePending => RejoinChoice == RejoinShipChoice.Pending;
        public static bool ChoseUseExistingShip => RejoinChoice == RejoinShipChoice.UseExisting;
        public static bool ChoseStartFreshShip => RejoinChoice == RejoinShipChoice.StartFresh;
        public static bool IsRejoinChoiceResolved =>
            RejoinChoice == RejoinShipChoice.UseExisting || RejoinChoice == RejoinShipChoice.StartFresh;

        public static void ChooseUseExistingShip() => RejoinChoice = RejoinShipChoice.UseExisting;

        public static void ChooseStartFreshShip()
        {
            // --- Fresh spawn path — player must pick team again ---
            RejoinChoice = RejoinShipChoice.StartFresh;
            TeamChoiceConfirmed = false;
            _deferredConfirmPending = false;
        }

        public static void ResetRejoinChoiceToPending()
        {
            if (RejoinChoice != RejoinShipChoice.NotApplicable)
                RejoinChoice = RejoinShipChoice.Pending;
        }

        /// <summary>
        /// Block command target, local ship tagging, camera, and owned-ship presentation until the
        /// server confirms team pick or resume. Covers map loading (before rejoin UI latches Pending)
        /// and the normal Join Team screen — a GhostOwner-matched orphan must not act as "my ship".
        /// </summary>
        public static bool ShouldSuppressLocalPlayerControl()
        {
            // --- Block until TeamChoiceResultRpc / RejoinShipResultRpc confirms ---
            // [TITAN-ORBIT] TeamChoiceConfirmed is false from connect through "Building galaxy..."
            // and team/rejoin UI. Resume and Join Team both call ConfirmTeamChoice on success.
            // Previous gate only suppressed Rejoin Pending / StartFresh, so a persisted ship for
            // this NetworkId drove camera + hull visuals during map load before team pick.
            return !TeamChoiceConfirmed;
        }

        public static bool ShouldBindCommandTarget() => !ShouldSuppressLocalPlayerControl();
    }
}
