namespace TitanOrbit.Core
{
    /// <summary>
    /// Client-only state machine for team pick and dedicated-server rejoin ship-resume flow.
    /// Written by <see cref="RejoinShipResultClientSystem"/>, team UI, and
    /// <see cref="ClientCommandTargetSystem"/> gating. Prevents local ship control before the
    /// player confirms team or rejoin choice. Not replicated — server uses ShipState flags.
    /// </summary>
    public static class ClientTeamFlowState
    {
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

        public static bool HasRequestedTeamPick => _teamPickRequested;
        public static bool IsRejoinEligibilityLocked => _rejoinEligibilityLocked;

        public static void ConfirmTeamChoice()
        {
            // --- Server acknowledged team — unlock normal play ---
            TeamChoiceConfirmed = true;
            LockRejoinEligibility();
        }

        public static void Reset()
        {
            // --- Full session reset (disconnect, return to menu) ---
            TeamChoiceConfirmed = false;
            RejoinChoice = RejoinShipChoice.NotApplicable;
            _teamPickRequested = false;
            _rejoinEligibilityLocked = false;
        }

        /// <summary>Call when the player clicks a team button (before server ack).</summary>
        public static void NotifyTeamPickRequested()
        {
            // --- Optimistic team pick — block late rejoin prompts ---
            _teamPickRequested = true;
            LockRejoinEligibility();
        }

        /// <summary>Allow retry after a failed team RPC without treating the spawned ship as a rejoin.</summary>
        public static void ClearTeamPickRequest()
        {
            // --- RPC failed before confirm — allow another team click ---
            if (TeamChoiceConfirmed)
                return;
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
        }

        public static void ResetRejoinChoiceToPending()
        {
            if (RejoinChoice != RejoinShipChoice.NotApplicable)
                RejoinChoice = RejoinShipChoice.Pending;
        }

        /// <summary>Block command target / local ship tagging until the player finishes rejoin UI.</summary>
        public static bool ShouldSuppressLocalPlayerControl()
        {
            // --- Block input until rejoin UI or fresh-team flow completes ---
            if (RejoinChoice == RejoinShipChoice.Pending)
                return true;
            if (RejoinChoice == RejoinShipChoice.StartFresh && !TeamChoiceConfirmed)
                return true;
            return false;
        }

        public static bool ShouldBindCommandTarget() => !ShouldSuppressLocalPlayerControl();
    }
}
