namespace TitanOrbit.ECS
{
    /// <summary>
    /// [HYBRID] Main-thread input snapshot written by MonoBehaviour (<see cref="Game.ShipInputBridge"/>)
    /// in Update and consumed by ECS during <see cref="GhostInputSystemGroup"/>
    /// (<see cref="ShipInputApplySystem"/>). Bridges Unity's frame-rate Update loop and NetCode's
    /// fixed-step input group — they run on different schedules and threads.
    /// <para>
    /// One-shot actions (B-key cycle) are latched until <see cref="ShipInputApplySystem"/> copies
    /// them onto the ghost. Without a latch, <c>WasPressedThisFrame</c> is cleared on the next
    /// Unity Update before GhostInputSystemGroup runs — the server never sees the press.
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
        /// </summary>
        public static void ConsumeCycleBulletLatch()
        {
            s_cycleBulletLatched = false;
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
        }

        /// <summary>True while a cycle press is waiting to be applied (for floating-name UI).</summary>
        public static bool CycleBulletLatched => s_cycleBulletLatched;

        /// <summary>True while a rocket press is waiting to be applied.</summary>
        public static bool FireRocketLatched => s_fireRocketLatched;
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
