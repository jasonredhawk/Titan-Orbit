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
    /// Server <c>ShipDepositFeedback</c> beats raise <see cref="LocalDepositBeat"/> and update
    /// optimistic Ship cargo <b>and</b> Bank together so Orbit Menu columns tick with real deposits
    /// (and the audible chunk). Live ledger / RPCs soft-reconcile only.
    /// </para>
    /// </summary>
    public static class MoonOrbitClientState
    {
        /// <summary>-1 means no pending contributed-gems reply; otherwise server-reported pool size.</summary>
        public static float PendingContributedGems = -1f;

        /// <summary>
        /// Last contributed-gems amount the client accepted (RPC or Local Host live read).
        /// Used to auto-seed optimistic Bank on the first deposit beat if the orbit menu has not
        /// called <see cref="EnsureOptimisticDepositBankSeed"/> yet this deposit session.
        /// </summary>
        public static float LastKnownContributedGems { get; private set; }

        /// <summary>
        /// Client-predicted cargo during local deposit (&lt; 0 = unused). Updated each metronome beat
        /// so the top ship-stats gems row drops by chunk size in sync with SFX, before the ghost
        /// snapshot catches up.
        /// </summary>
        public static float OptimisticDepositCargoGems { get; private set; } = -1f;

        /// <summary>
        /// Client-predicted Bank (contributed gems) during local deposit (&lt; 0 = unused).
        /// Bumped by the <b>same</b> chunk as cargo on each metronome beat so GEM DEPOSITS rises
        /// with the SFX — not on the independent live-ledger / RPC poll clock.
        /// </summary>
        public static float OptimisticDepositBankGems { get; private set; } = -1f;

        /// <summary>
        /// Bank total we seeded optimistic from. Used to correct a late RPC baseline without
        /// adopting every live-ledger tick (which would desync Bank from the client beat).
        /// </summary>
        static float s_OptimisticBankSeedBaseline;

        /// <summary>
        /// Fired on each local deposit metronome beat with the actual chunk amount.
        /// Orbit Menu UIs subscribe so they can refresh after cargo+Bank optimistic state updates.
        /// </summary>
        public static event Action<float> LocalDepositBeat;

        /// <summary>Last store purchase failure/success message from server; null when consumed.</summary>
        public static string PendingStoreMessage;

        /// <summary>
        /// Last card-spin offer ids (client mirror). Empty string = no card in that offer slot.
        /// Filled by MoonOrbitRpcClientSystem or Local Host direct apply.
        /// </summary>
        static readonly string[] s_spinOfferCardIds = { string.Empty, string.Empty, string.Empty };

        /// <summary>True when a new spin offer arrived and orbit UI should rebuild offer tiles.</summary>
        public static bool PendingSpinOfferReceived { get; private set; }

        /// <summary>True when the offer was consumed (take card) and UI should clear offer tiles.</summary>
        public static bool PendingSpinOfferConsumed { get; private set; }

        /// <summary>Called from MoonOrbitRpcClientSystem when ContributedGemsResultRpc arrives.
        /// Also remembers the amount for optimistic Bank seeding on the next deposit session.
        /// </summary>
        public static void SetContributedGems(float amount)
        {
            PendingContributedGems = amount;
            if (amount >= 0f)
                ApplyAuthoritativeBankAmount(amount);
        }

        /// <summary>
        /// Records a known Bank total from Orbit Menu (live Local Host read or UI cache) so the
        /// metronome can seed optimistic Bank without waiting for an RPC.
        /// </summary>
        public static void RememberContributedGems(float amount)
        {
            if (amount >= 0f)
                ApplyAuthoritativeBankAmount(amount);
        }

        /// <summary>
        /// Updates <see cref="LastKnownContributedGems"/>. When the ledger drops (orbit-store spend),
        /// snaps optimistic Bank down so GEM DEPOSITS falls immediately. Does <b>not</b> snap when
        /// live merely lags behind optimistic deposit beats (live still rising or flat).
        /// </summary>
        static void ApplyAuthoritativeBankAmount(float amount)
        {
            // --- Detect store spend: authority fell vs last known ledger ---
            // Deposit metronome keeps optimistic ahead of live — that is NOT a spend.
            // Prices are tens of gems, so a 1-gem threshold ignores float noise.
            if (LastKnownContributedGems > 0f && amount < LastKnownContributedGems - 1f)
                SnapOptimisticBankDownToAuthority(amount);

            LastKnownContributedGems = amount;
        }

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
        /// [TITAN-ORBIT] Seeds optimistic Ship cargo from ghost/UI so the Orbit Menu does not
        /// flash 0 while waiting for the first server deposit beat. Safe to call every frame —
        /// only the first call (while unset) writes.
        /// </summary>
        /// <param name="knownCargo">Current ship cargo from tagged ShipState.</param>
        public static void EnsureOptimisticDepositCargoSeed(float knownCargo)
        {
            if (OptimisticDepositCargoGems < 0f)
                OptimisticDepositCargoGems = Mathf.Max(0f, knownCargo);
        }

        /// <summary>
        /// [TITAN-ORBIT] Seeds optimistic Bank from the last known contributed balance so the first
        /// metronome beat can add its chunk. Safe to call every frame while depositing — only the
        /// first call (while unset) writes. Call from Orbit Menu before/while deposit is on.
        /// </summary>
        /// <param name="knownBank">Current Bank from UI / last RPC / live ledger.</param>
        public static void EnsureOptimisticDepositBankSeed(float knownBank)
        {
            // --- Seed once ---
            // [TITAN-ORBIT] Without a seed, the first beat cannot bump Bank and the column stays
            // on live ECS timing (the desync the metronome was meant to fix).
            if (OptimisticDepositBankGems < 0f)
            {
                float seed = Mathf.Max(0f, knownBank);
                OptimisticDepositBankGems = seed;
                s_OptimisticBankSeedBaseline = seed;
            }
        }

        /// <summary>
        /// Soft-reconcile authoritative Bank into the optimistic value while depositing.
        /// Never snaps the display <b>up</b> from the live ledger alone: live jumps on the server
        /// metronome and would make Bank rise independently of the client SFX beat. Only lock up
        /// when live is within ~half a gem of optimistic (caught up / float noise).
        /// Spends are handled by <see cref="ApplyAuthoritativeBankAmount"/> (ledger drop vs last known).
        /// </summary>
        /// <param name="authoritativeBank">Server / RPC contributed-gems amount.</param>
        public static void ReconcileOptimisticDepositBank(float authoritativeBank)
        {
            // --- No optimistic Bank yet — nothing to reconcile ---
            if (OptimisticDepositBankGems < 0f)
                return;

            // [TITAN-ORBIT] Abs epsilon only for catch-up — do NOT adopt live when it is a full
            // chunk ahead (Bank rising independently of the client deposit metronome).
            float delta = authoritativeBank - OptimisticDepositBankGems;
            if (Mathf.Abs(delta) <= 0.51f)
                OptimisticDepositBankGems = Mathf.Max(0f, authoritativeBank);
        }

        /// <summary>
        /// RPC / one-shot Bank reply while depositing. Corrects a <b>late baseline</b> (we seeded
        /// 0 or a stale total before the first contributed-gems reply) without adopting every
        /// mid-deposit live tick. Preserves gems already added by client metronome beats.
        /// Ledger drops (store purchases) snap optimistic Bank via <see cref="ApplyAuthoritativeBankAmount"/>.
        /// </summary>
        /// <param name="authoritativeBank">Contributed-gems amount from RPC.</param>
        public static void ApplyAuthoritativeBankBaseline(float authoritativeBank)
        {
            RememberContributedGems(authoritativeBank);
            if (OptimisticDepositBankGems < 0f)
                return;

            // Spend path already snapped optimistic ≤ authority.
            if (OptimisticDepositBankGems <= authoritativeBank + 0.51f)
            {
                ReconcileOptimisticDepositBank(authoritativeBank);
                return;
            }

            // How many gems the client metronome already painted on top of our seed.
            float addedByBeats = OptimisticDepositBankGems - s_OptimisticBankSeedBaseline;
            float impliedSeed = authoritativeBank - addedByBeats;

            // Authority implies a higher starting Bank than we seeded (late RPC / first poll).
            if (impliedSeed > s_OptimisticBankSeedBaseline + 0.51f)
            {
                s_OptimisticBankSeedBaseline = Mathf.Max(0f, impliedSeed);
                OptimisticDepositBankGems = s_OptimisticBankSeedBaseline + Mathf.Max(0f, addedByBeats);
                return;
            }

            ReconcileOptimisticDepositBank(authoritativeBank);
        }

        /// <summary>
        /// When server Bank is lower than optimistic (store purchase), snap optimistic down.
        /// Returns true when a snap happened.
        /// </summary>
        static bool SnapOptimisticBankDownToAuthority(float authoritativeBank)
        {
            if (OptimisticDepositBankGems < 0f || authoritativeBank < 0f)
                return false;

            // Authority lower than optimistic → spend (or correction). Adopt immediately.
            if (authoritativeBank < OptimisticDepositBankGems - 0.01f)
            {
                OptimisticDepositBankGems = Mathf.Max(0f, authoritativeBank);
                // Keep seed ≤ optimistic so later deposit beats do not inflate from a stale high seed.
                s_OptimisticBankSeedBaseline = Mathf.Min(s_OptimisticBankSeedBaseline, OptimisticDepositBankGems);
                return true;
            }

            return false;
        }

        /// <summary>
        /// [TITAN-ORBIT] Local deposit metronome fired one chunk — refresh optimistic cargo + Bank
        /// together, then notify Orbit Menu listeners. Called from <c>EcsFloatingCountPresenter</c>
        /// with the actual amount so Ship ↓ and Bank ↑ stay one atomic beat.
        /// </summary>
        /// <param name="chunkAmount">Gems credited this beat (ship level, or leftover cargo).</param>
        /// <param name="cargoAfterBeat">Ship cargo remaining after this beat (client estimate).</param>
        /// <param name="updateCargo">
        /// When false, only Bank is bumped (caller lacked a cargo baseline — Orbit Menu keeps ghost cargo).
        /// </param>
        public static void NotifyLocalDepositBeat(float chunkAmount, float cargoAfterBeat, bool updateCargo = true)
        {
            // --- Optimistic cargo (Ship column / ship-stats HUD) ---
            if (updateCargo)
                OptimisticDepositCargoGems = Mathf.Max(0f, cargoAfterBeat);

            // --- Optimistic Bank (GEM DEPOSITS) — same chunk, same frame ---
            if (OptimisticDepositBankGems < 0f)
            {
                float seed = Mathf.Max(0f, LastKnownContributedGems);
                OptimisticDepositBankGems = seed;
                s_OptimisticBankSeedBaseline = seed;
            }

            if (chunkAmount > 0.001f)
                OptimisticDepositBankGems += chunkAmount;

            if (chunkAmount > 0.001f)
                LocalDepositBeat?.Invoke(chunkAmount);
        }

        /// <summary>
        /// Clears optimistic cargo + Bank when deposit stops so HUD returns to ghost / RPC truth.
        /// </summary>
        public static void ClearOptimisticDepositCargo()
        {
            OptimisticDepositCargoGems = -1f;
            OptimisticDepositBankGems = -1f;
            s_OptimisticBankSeedBaseline = 0f;
        }

        /// <summary>
        /// True while the local metronome is driving a cargo display that may lead the ghost.
        /// </summary>
        public static bool TryGetOptimisticDepositCargo(out float cargoGems)
        {
            cargoGems = OptimisticDepositCargoGems;
            return cargoGems >= 0f;
        }

        /// <summary>
        /// True while the local metronome is driving Bank (GEM DEPOSITS) ahead of live ledger / RPC.
        /// </summary>
        public static bool TryGetOptimisticDepositBank(out float bankGems)
        {
            bankGems = OptimisticDepositBankGems;
            return bankGems >= 0f;
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

        /// <summary>
        /// Stores three spin-offer card ids and flags the orbit UI to refresh offer tiles.
        /// </summary>
        public static void SetSpinOffer(string cardId0, string cardId1, string cardId2, bool success)
        {
            // --- Spin offer mirror ---
            s_spinOfferCardIds[0] = cardId0 ?? string.Empty;
            s_spinOfferCardIds[1] = cardId1 ?? string.Empty;
            s_spinOfferCardIds[2] = cardId2 ?? string.Empty;
            if (success)
                PendingSpinOfferReceived = true;
            else
                PendingSpinOfferConsumed = true;
        }

        /// <summary>Reads one spin-offer card id (null when empty / out of range).</summary>
        public static string GetSpinOfferCardId(int index)
        {
            if ((uint)index >= 3u)
                return null;
            string s = s_spinOfferCardIds[index];
            return string.IsNullOrEmpty(s) ? null : s;
        }

        /// <summary>UI one-shot: returns true if a new spin offer should rebuild tiles.</summary>
        public static bool TryConsumeSpinOfferReceived()
        {
            if (!PendingSpinOfferReceived)
                return false;
            PendingSpinOfferReceived = false;
            return true;
        }

        /// <summary>UI one-shot: returns true if the spin offer was cleared after take-card.</summary>
        public static bool TryConsumeSpinOfferConsumed()
        {
            if (!PendingSpinOfferConsumed)
                return false;
            PendingSpinOfferConsumed = false;
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
            // Drop optimistic cargo + Bank as soon as deposit stops so HUD snaps back to authority.
            if (!wantDeposit)
            {
                ClearOptimisticDepositCargo();
                return;
            }

            // Seed Bank baseline immediately so the first metronome beat can bump GEM DEPOSITS
            // in the same frame as the first SFX (Windows client often beats before Orbit Menu refresh).
            EnsureOptimisticDepositBankSeed(LastKnownContributedGems);
        }
    }
}
