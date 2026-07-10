namespace TitanOrbit.ECS
{
    /// <summary>
    /// Main-thread input snapshot written by MonoBehaviour (<see cref="Game.ShipInputBridge"/>)
    /// and consumed by ECS during <see cref="GhostInputSystemGroup"/> (<see cref="ShipInputApplySystem"/>).
    /// Bridges Unity's Update loop and NetCode's fixed-step input group — they run on different schedules.
    /// </summary>
    public static class ShipPendingInput
    {
        /// <summary>Most recent ShipInput built from keyboard/mouse this frame.</summary>
        public static ShipInput Latest;

        /// <summary>False until ShipInputBridge has written at least one frame of input.</summary>
        public static bool HasValue;

        /// <summary>True when client and server share a process (MPPM / local host).</summary>
        public static bool LocalHostMode;

        /// <summary>
        /// Called from ShipInputBridge.Update. Stores input for the next GhostInputSystemGroup tick.
        /// </summary>
        public static void Set(ShipInput input, bool localHostMode)
        {
            Latest = input;
            HasValue = true;
            LocalHostMode = localHostMode;
        }
    }
}
