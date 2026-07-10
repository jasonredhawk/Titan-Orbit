namespace TitanOrbit.Core
{
    /// <summary>Client-side team pick and dedicated rejoin ship-resume flow.</summary>
    public static class ClientTeamFlowState
    {
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
            TeamChoiceConfirmed = true;
            LockRejoinEligibility();
        }

        public static void Reset()
        {
            TeamChoiceConfirmed = false;
            RejoinChoice = RejoinShipChoice.NotApplicable;
            _teamPickRequested = false;
            _rejoinEligibilityLocked = false;
        }

        /// <summary>Call when the player clicks a team button (before server ack).</summary>
        public static void NotifyTeamPickRequested()
        {
            _teamPickRequested = true;
            LockRejoinEligibility();
        }

        /// <summary>Allow retry after a failed team RPC without treating the spawned ship as a rejoin.</summary>
        public static void ClearTeamPickRequest()
        {
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
            if (RejoinChoice == RejoinShipChoice.Pending)
                return true;
            if (RejoinChoice == RejoinShipChoice.StartFresh && !TeamChoiceConfirmed)
                return true;
            return false;
        }

        public static bool ShouldBindCommandTarget() => !ShouldSuppressLocalPlayerControl();
    }
}
