namespace TitanOrbit.Core
{
    /// <summary>Client-side moon orbit store feedback bridged from ECS RPC results to UI.</summary>
    public static class MoonOrbitClientState
    {
        public static float PendingContributedGems = -1f;
        public static string PendingStoreMessage;

        public static void SetContributedGems(float amount) => PendingContributedGems = amount;

        public static bool TryConsumeContributedGems(out float amount)
        {
            amount = PendingContributedGems;
            if (PendingContributedGems < 0f)
                return false;
            PendingContributedGems = -1f;
            return true;
        }

        public static void SetStoreMessage(string message) => PendingStoreMessage = message;

        public static bool TryConsumeStoreMessage(out string message)
        {
            message = PendingStoreMessage;
            if (string.IsNullOrEmpty(message))
                return false;
            PendingStoreMessage = null;
            return true;
        }

        public static bool IsOrbitMenuVisible { get; private set; }

        public static void SetOrbitMenuVisible(bool visible) => IsOrbitMenuVisible = visible;

        public static bool WantDepositGems { get; private set; }

        public static void SetWantDepositGems(bool wantDeposit) => WantDepositGems = wantDeposit;
    }
}
