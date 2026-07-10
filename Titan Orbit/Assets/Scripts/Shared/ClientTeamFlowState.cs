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

        public static void ConfirmTeamChoice() => TeamChoiceConfirmed = true;

        public static void Reset()
        {
            TeamChoiceConfirmed = false;
            RejoinChoice = RejoinShipChoice.NotApplicable;
        }

        public static void NotifyRejoinableShipDetected()
        {
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
