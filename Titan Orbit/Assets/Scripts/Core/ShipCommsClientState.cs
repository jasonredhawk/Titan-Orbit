using UnityEngine;

namespace TitanOrbit.Core
{
    /// <summary>
    /// Client-only flag for the hold-S comms matrix. <c>ShipCommsPanel</c> writes this while
    /// the overlay is visible; input and cursor code read it so left-click selects keywords
    /// instead of firing the gun.
    /// <para>
    /// Lives in Core (not the UI assembly) so <c>ShipInputBridge</c> and
    /// <c>GameplayCursorController</c> can see it without a Game → Assembly-CSharp reference.
    /// Not replicated — the server never needs to know the panel is open.
    /// </para>
    /// </summary>
    public static class ShipCommsClientState
    {
        /// <summary>
        /// True while the local player is holding S and the compose card is on screen.
        /// </summary>
        public static bool IsOpen { get; private set; }

        /// <summary>
        /// [UNITY] Domain Reload off leaves statics sticky across Play Mode. Clear so a second
        /// Play does not start with fire already suppressed.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => IsOpen = false;

        /// <summary>
        /// Called from <c>ShipCommsPanel</c> when the matrix shows or hides.
        /// </summary>
        /// <param name="open">True while S is held and the card is interactable.</param>
        public static void SetOpen(bool open) => IsOpen = open;
    }
}
