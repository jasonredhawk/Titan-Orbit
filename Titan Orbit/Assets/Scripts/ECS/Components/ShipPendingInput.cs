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

        /// <summary>True while a cycle press is waiting to be applied (for floating-name UI).</summary>
        public static bool CycleBulletLatched => s_cycleBulletLatched;
    }
}
