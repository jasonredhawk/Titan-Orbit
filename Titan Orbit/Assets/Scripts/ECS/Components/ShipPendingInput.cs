namespace TitanOrbit.ECS
{
    /// <summary>
    /// [HYBRID] Main-thread input snapshot written by MonoBehaviour (<see cref="Game.ShipInputBridge"/>)
    /// in Update and consumed by ECS during <see cref="GhostInputSystemGroup"/>
    /// (<see cref="ShipInputApplySystem"/>). Bridges Unity's frame-rate Update loop and NetCode's
    /// fixed-step input group — they run on different schedules and threads.
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
        /// [HYBRID] Called from ShipInputBridge.Update each frame. Stores input for the next
        /// GhostInputSystemGroup fixed tick.
        /// </summary>
        /// <param name="input">Fresh input snapshot from keyboard/mouse/touch.</param>
        /// <param name="localHostMode">True when running as local host (client + server same process).</param>
        public static void Set(ShipInput input, bool localHostMode)
        {
            // --- Cache for ECS input apply system ---
            Latest = input;
            HasValue = true;
            LocalHostMode = localHostMode;
        }
    }
}
