using System;
using UnityEngine;

namespace TitanOrbit.Core
{
    /// <summary>
    /// Client-only scratch state for moon orbit store UI. <see cref="MoonOrbitRpcClientSystem"/>
    /// writes contributed-gem balances and store messages here; <see cref="UI.OrbitStationUI"/>
    /// consumes them on the next UI tick. Not replicated — ephemeral bridge between NetCode RPC
    /// entities and MonoBehaviour UI. Also tracks orbit menu visibility and deposit toggle mirror.
    /// <para>
    /// Local deposit metronome beats raise <see cref="LocalDepositBeat"/> so Orbit Menu Bank /
    /// Ship columns tick with the audible chunk (RPCs alone were too slow / out of phase).
    /// </para>
    /// </summary>
    public static class MoonOrbitClientState
    {
        /// <summary>-1 means no pending contributed-gems reply; otherwise server-reported pool size.</summary>
        public static float PendingContributedGems = -1f;

        /// <summary>
        /// Client-predicted cargo during local deposit (&lt; 0 = unused). Updated each metronome beat
        /// so the top ship-stats gems row drops by chunk size in sync with SFX, before the ghost
        /// snapshot catches up.
        /// </summary>
        public static float OptimisticDepositCargoGems { get; private set; } = -1f;

        /// <summary>
        /// Fired on each local deposit metronome beat with the actual chunk amount.
        /// Orbit Menu UIs subscribe so Bank can increment in sync with SFX (multi-listener safe).
        /// </summary>
        public static event Action<float> LocalDepositBeat;

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

        /// <summary>
        /// [TITAN-ORBIT] Local deposit metronome fired one chunk — refresh optimistic cargo and notify
        /// Orbit Menu listeners. Called from <c>EcsFloatingCountPresenter</c> with the actual amount.
        /// </summary>
        /// <param name="chunkAmount">Gems credited this beat (ship level, or leftover cargo).</param>
        /// <param name="cargoAfterBeat">Ship cargo remaining after this beat (client estimate).</param>
        public static void NotifyLocalDepositBeat(float chunkAmount, float cargoAfterBeat)
        {
            // --- Optimistic cargo + fan-out to Orbit Menu ---
            OptimisticDepositCargoGems = Mathf.Max(0f, cargoAfterBeat);
            if (chunkAmount > 0.001f)
                LocalDepositBeat?.Invoke(chunkAmount);
        }

        /// <summary>
        /// Clears optimistic cargo when deposit stops so HUD returns to ghost <c>CurrentGems</c>.
        /// </summary>
        public static void ClearOptimisticDepositCargo() => OptimisticDepositCargoGems = -1f;

        /// <summary>
        /// True while the local metronome is driving a cargo display that may lead the ghost.
        /// </summary>
        public static bool TryGetOptimisticDepositCargo(out float cargoGems)
        {
            cargoGems = OptimisticDepositCargoGems;
            return cargoGems >= 0f;
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
        public static void SetWantDepositGems(bool wantDeposit)
        {
            WantDepositGems = wantDeposit;
            // Drop optimistic cargo as soon as deposit stops so HUD snaps back to ghost CurrentGems.
            if (!wantDeposit)
                ClearOptimisticDepositCargo();
        }
    }
}
