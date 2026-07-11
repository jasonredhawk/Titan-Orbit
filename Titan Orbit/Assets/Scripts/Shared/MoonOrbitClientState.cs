namespace TitanOrbit.Core
{
    /// <summary>
    /// Client-only scratch state for moon orbit store UI. <see cref="MoonOrbitRpcClientSystem"/>
    /// writes contributed-gem balances and store messages here; <see cref="UI.OrbitStationUI"/>
    /// consumes them on the next UI tick. Not replicated — ephemeral bridge between NetCode RPC
    /// entities and MonoBehaviour UI. Also tracks orbit menu visibility and deposit toggle mirror.
    /// </summary>
    public static class MoonOrbitClientState
    {
        /// <summary>-1 means no pending contributed-gems reply; otherwise server-reported pool size.</summary>
        public static float PendingContributedGems = -1f;

        /// <summary>Last store purchase failure/success message from server; null when consumed.</summary>
        public static string PendingStoreMessage;

        /// <summary>Called from MoonOrbitRpcClientSystem when ContributedGemsResultRpc arrives.</summary>
        public static void SetContributedGems(float amount) => PendingContributedGems = amount;

        /// <summary>
        /// UI polls once per frame — returns true and clears pending amount if a reply was queued.
        /// </summary>
        public static bool TryConsumeContributedGems(out float amount)
        {
            // --- One-shot read for orbit store UI ---
            amount = PendingContributedGems;
            if (PendingContributedGems < 0f)
                return false;
            PendingContributedGems = -1f;
            return true;
        }

        /// <summary>Queues a user-visible store result string from OrbitStoreResultRpc.</summary>
        public static void SetStoreMessage(string message) => PendingStoreMessage = message;

        /// <summary>UI reads and clears the pending store message (one-shot).</summary>
        public static bool TryConsumeStoreMessage(out string message)
        {
            // --- One-shot toast / error string from store RPC ---
            message = PendingStoreMessage;
            if (string.IsNullOrEmpty(message))
                return false;
            PendingStoreMessage = null;
            return true;
        }

        /// <summary>True while orbit station panel is open — HUD suppressors read this.</summary>
        public static bool IsOrbitMenuVisible { get; private set; }

        /// <summary>OrbitStationUI sets when opening/closing the dock sidebar.</summary>
        public static void SetOrbitMenuVisible(bool visible) => IsOrbitMenuVisible = visible;

        /// <summary>Local mirror of gem auto-deposit toggle for UI checkbox state.</summary>
        public static bool WantDepositGems { get; private set; }

        /// <summary>Updated when player toggles deposit; RPC syncs authoritative ShipDepositIntent.</summary>
        public static void SetWantDepositGems(bool wantDeposit) => WantDepositGems = wantDeposit;
    }
}
