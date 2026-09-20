using UnityEngine;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [HYBRID] Main-thread input snapshot written by MonoBehaviour (<see cref="Game.ShipInputBridge"/>)
    /// in Update and consumed by ECS during <see cref="GhostInputSystemGroup"/>
    /// (<see cref="ShipInputApplySystem"/>). Bridges Unity's frame-rate Update loop and NetCode's
    /// fixed-step input group — they run on different schedules and threads.
    /// <para>
    /// One-shot actions (B-key cycle, HUD SetBulletBank) are latched until
    /// <see cref="ShipInputApplySystem"/> copies them onto the ghost. Without a latch,
    /// <c>WasPressedThisFrame</c> is cleared on the next Unity Update before
    /// GhostInputSystemGroup runs — the server never sees the press.
    /// </para>
    /// </summary>
    public static class ShipPendingInput
    {
        /// <summary>[ECS/DOTS] Most recent <see cref="ShipInput"/> built from keyboard/mouse this frame.</summary>
        public static ShipInput Latest;

        /// <summary>[STANDARD] False until ShipInputBridge has written at least one frame of input.</summary>
        public static bool HasValue;

        /// <summary>
        /// [NETCODE] True when client and server share a process (MPPM / local host) — affects which
        /// path feeds input onto the predicted ghost.
        /// </summary>
        public static bool LocalHostMode;

        /// <summary>
        /// [TITAN-ORBIT] Latched B / CycleBullet press waiting to be copied into <see cref="ShipInput"/>.
        /// Set by ShipInputBridge; cleared by <see cref="ShipInputApplySystem"/> after apply.
        /// </summary>
        static bool s_cycleBulletLatched;

        /// <summary>
        /// [TITAN-ORBIT] Latched ALT / FireRocket press. Same reason as CycleBullet — Unity
        /// Update can clear WasPressedThisFrame before GhostInputSystemGroup runs.
        /// </summary>
        static bool s_fireRocketLatched;

        /// <summary>
        /// [TITAN-ORBIT] Latched ALT / PlaceMine press (caret on a mine pack). Same reason
        /// as FireRocket — Unity Update can clear WasPressedThisFrame before GhostInputSystemGroup.
        /// </summary>
        static bool s_placeMineLatched;

        /// <summary>
        /// [TITAN-ORBIT] Latched bullet-type HUD click. Same reason as CycleBullet — Unity
        /// Update can finish before GhostInputSystemGroup copies the one-shot onto the ghost.
        /// </summary>
        static bool s_setBulletBankLatched;

        /// <summary>
        /// [HYBRID] Called from ShipInputBridge.Update each frame. Stores input for the next
        /// GhostInputSystemGroup fixed tick. Preserves latched CycleBullet across frames until
        /// the input apply system consumes it.
        /// </summary>
        /// <param name="input">Fresh input snapshot from keyboard/mouse/touch.</param>
        /// <param name="localHostMode">True when running as local host (client + server same process).</param>
        public static void Set(ShipInput input, bool localHostMode)
        {
            // --- Merge latched one-shots into this frame's snapshot ---
            // BuildInput may have already Set CycleBullet this frame; keep latch OR until Apply clears.
            if (s_cycleBulletLatched)
            {
                var cycle = new Unity.NetCode.InputEvent();
                cycle.Set();
                input.CycleBullet = cycle;
            }

            if (s_fireRocketLatched)
            {
                var rocket = new Unity.NetCode.InputEvent();
                rocket.Set();
                input.FireRocket = rocket;
            }

            if (s_placeMineLatched)
            {
                var mine = new Unity.NetCode.InputEvent();
                mine.Set();
                input.PlaceMine = mine;
            }

            // HUD tile click — one-shot, same latch rule as B. Copy the requested
            // category so the predicted cycle system can jump instead of increment.
            if (s_setBulletBankLatched)
            {
                var setBank = new Unity.NetCode.InputEvent();
                setBank.Set();
                input.SetBulletBank = setBank;
                input.SelectedBulletBank = BulletBankSelection.RequestedBankIndex;
            }

            Latest = input;
            HasValue = true;
            LocalHostMode = localHostMode;
        }

        /// <summary>
        /// Call when the player presses B. Stays true until <see cref="ConsumeCycleBulletLatch"/> so
        /// NetCode fixed ticks that run after the Unity frame still see the press.
        /// </summary>
        public static void LatchCycleBullet()
        {
            s_cycleBulletLatched = true;
        }

        /// <summary>
        /// Clears the B-key latch after ShipInput has been copied onto the local ghost this tick.
        /// Also zeros <see cref="Latest"/>.CycleBullet so a second GhostInput tick in the
        /// same Unity frame cannot reuse the same press (that skipped HUD rows).
        /// </summary>
        public static void ConsumeCycleBulletLatch()
        {
            s_cycleBulletLatched = false;
            if (!HasValue)
                return;

            // --- One apply per press ---
            // [NETCODE] GhostInputSystemGroup can run more than once per Update when
            // the sim catches up. Latest still held CycleBullet.IsSet, so each extra
            // tick incremented the bank again and the Weapons caret jumped.
            var input = Latest;
            input.CycleBullet = default;
            Latest = input;
        }

        /// <summary>Call when the player presses ALT (or the rocket HUD). Stays true until consumed.</summary>
        public static void LatchFireRocket()
        {
            s_fireRocketLatched = true;
        }

        /// <summary>Clears the ALT latch after ShipInput has been copied onto the local ghost.</summary>
        public static void ConsumeFireRocketLatch()
        {
            s_fireRocketLatched = false;
            if (!HasValue)
                return;
            var input = Latest;
            input.FireRocket = default;
            Latest = input;
        }

        /// <summary>True while a cycle press is waiting to be applied (for floating-name UI).</summary>
        public static bool CycleBulletLatched => s_cycleBulletLatched;

        /// <summary>True while a rocket press is waiting to be applied.</summary>
        public static bool FireRocketLatched => s_fireRocketLatched;

        /// <summary>Call when ALT should place the focused mine pack. Stays true until consumed.</summary>
        public static void LatchPlaceMine()
        {
            s_placeMineLatched = true;
        }

        /// <summary>Clears the mine latch after ShipInput has been copied onto the local ghost.</summary>
        public static void ConsumePlaceMineLatch()
        {
            s_placeMineLatched = false;
            if (!HasValue)
                return;
            var input = Latest;
            input.PlaceMine = default;
            Latest = input;
        }

        /// <summary>True while a mine press is waiting to be applied.</summary>
        public static bool PlaceMineLatched => s_placeMineLatched;

        /// <summary>Call when the bullet-type HUD click picks a bank. Stays true until consumed.</summary>
        public static void LatchSetBulletBank()
        {
            s_setBulletBankLatched = true;
        }

        /// <summary>Clears the HUD-click latch after ShipInput has been copied onto the local ghost.</summary>
        public static void ConsumeSetBulletBankLatch()
        {
            s_setBulletBankLatched = false;
            if (!HasValue)
                return;
            var input = Latest;
            input.SetBulletBank = default;
            Latest = input;
        }

        /// <summary>True while a HUD bank click is waiting to be applied.</summary>
        public static bool SetBulletBankLatched => s_setBulletBankLatched;
    }

    /// <summary>
    /// Client-side bank the Weapons HUD wants to fire. Tile clicks and B write this;
    /// <c>ShipInputBridge</c> copies it onto <see cref="ShipInput.SelectedBulletBank"/> only
    /// on the latched SetBulletBank tick so a sticky every-frame index cannot fight
    /// the next press. The HUD caret reads <see cref="ResolveCaretBank"/> so the
    /// highlight moves on the same Unity frame as the key, before the ghost snapshot.
    /// </summary>
    public static class BulletBankSelection
    {
        /// <summary>
        /// How long the Weapons caret may sit on a B / click request before we fall
        /// back to the ghosted runtime index. Covers one NetCode tick plus a hitch.
        /// </summary>
        const float OptimisticCaretSeconds = 0.75f;

        /// <summary><c>BulletVfxBank</c> category index from the last HUD click or B (−1 = none).</summary>
        public static int RequestedBankIndex { get; private set; } = -1;

        /// <summary>
        /// <see cref="Time.unscaledTime"/> of the last <see cref="Request"/>. Used so
        /// the caret stays on the type the player just picked until prediction writes
        /// <see cref="ShipLoadoutState.RuntimeBulletIndex"/>.
        /// </summary>
        static float s_requestUnscaledTime = -999f;

        /// <summary>
        /// Records a tile click or B-key step and latches the one-shot so NetCode's
        /// next fixed tick sees it. The Weapons HUD highlights this bank immediately.
        /// </summary>
        /// <param name="bankIndex">Category index the player tapped or cycled to.</param>
        public static void Request(int bankIndex)
        {
            RequestedBankIndex = bankIndex;
            s_requestUnscaledTime = Time.unscaledTime;
            ShipPendingInput.LatchSetBulletBank();
        }

        /// <summary>
        /// Bank the Weapons caret should paint. Prefers the last B / click while the
        /// latch is pending or the optimistic window is open, then the ghosted runtime
        /// index once prediction or the snapshot catches up.
        /// </summary>
        /// <param name="runtimeBank">Ghosted <see cref="ShipLoadoutState.RuntimeBulletIndex"/>.</param>
        /// <returns>Category index to highlight.</returns>
        public static int ResolveCaretBank(int runtimeBank)
        {
            int runtime = runtimeBank < 0 ? 0 : runtimeBank;
            if (RequestedBankIndex < 0)
                return runtime;

            // --- Optimistic caret ---
            // [TITAN-ORBIT] B used to spawn floating text instantly while the HUD
            // waited on the ghost. The strip is the only feedback now, so it must
            // move on the same frame as the key.
            bool pending = ShipPendingInput.SetBulletBankLatched;
            bool recent = Time.unscaledTime - s_requestUnscaledTime <= OptimisticCaretSeconds;
            if (pending || recent || RequestedBankIndex == runtime)
                return RequestedBankIndex;

            return runtime;
        }

        /// <summary>
        /// Drops a stale request (no local ship, HUD hidden). Next paint uses the
        /// ghosted runtime index until the player presses B or clicks again.
        /// </summary>
        public static void Clear()
        {
            RequestedBankIndex = -1;
            s_requestUnscaledTime = -999f;
        }
    }

    /// <summary>
    /// Client-side which rocket pack will fire next. HUD UP/DOWN and row clicks write this;
    /// <c>ShipInputBridge</c> copies it onto <see cref="ShipInput.SelectedRocketSlot"/> each tick.
    /// Index is among rocket HUD rows (not the raw equipment buffer).
    /// </summary>
    public static class RocketSlotSelection
    {
        /// <summary>0-based HUD row. Clamped whenever the pack list changes.</summary>
        public static int SelectedIndex { get; private set; }

        /// <summary>Moves the caret by <paramref name="delta"/> and wraps.</summary>
        public static void Cycle(int delta, int count)
        {
            if (count <= 0)
            {
                SelectedIndex = 0;
                return;
            }

            int next = SelectedIndex + delta;
            while (next < 0)
                next += count;
            SelectedIndex = next % count;
        }

        /// <summary>Jumps to a HUD row (click). No-op when the list is empty.</summary>
        public static void Select(int index, int count)
        {
            if (count <= 0)
            {
                SelectedIndex = 0;
                return;
            }

            if (index < 0)
                index = 0;
            if (index >= count)
                index = count - 1;
            SelectedIndex = index;
        }

        /// <summary>Keeps the caret valid after a purchase or consume.</summary>
        public static int Clamp(int count)
        {
            if (count <= 0)
            {
                SelectedIndex = 0;
                return 0;
            }

            if (SelectedIndex < 0)
                SelectedIndex = 0;
            if (SelectedIndex >= count)
                SelectedIndex = count - 1;
            return SelectedIndex;
        }
    }
}
