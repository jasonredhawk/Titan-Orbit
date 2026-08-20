using TitanOrbit.ECS;
using TitanOrbit.Game;
using UnityEngine;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Client-only HUD coordination. The full Netcode-for-GameObjects HUD was removed; this class
    /// keeps shared visibility flags so gameplay chrome can hide together.
    /// <para>
    /// Three hide reasons: the ship upgrade tree overlay, the expanded full-map minimap, and
    /// local-player death (so the explosion and <see cref="DeathScreenController"/> plaque stay
    /// unobstructed). Widgets read the static properties each frame — they do not write ship state.
    /// </para>
    /// </summary>
    public class HUDController : MonoBehaviour
    {
        // [TITAN-ORBIT] When true, bottom HUD stats defer to the full-screen upgrade tree overlay.
        static bool s_shipUpgradeTreeObscuresHud;

        // [TITAN-ORBIT] When true, gameplay chrome defers to the expanded full-map minimap.
        static bool s_minimapExpandedObscuresHud;

        /// <summary>
        /// Frame stamp for the cached death-hide answer. <see cref="Time.frameCount"/> so every
        /// HUD widget can ask without each one hitting ECS again the same frame.
        /// </summary>
        static int s_deathGateFrame = -1;

        /// <summary>Cached result of <see cref="ComputeLocalPlayerDeathHidesHud"/> for this frame.</summary>
        static bool s_deathHidesHud;

        /// <summary>
        /// [UNITY] Domain Reload off leaves statics sticky across Play Mode. Clear so a second
        /// Play does not start with HUD already hidden.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            s_shipUpgradeTreeObscuresHud = false;
            s_minimapExpandedObscuresHud = false;
            s_deathGateFrame = -1;
            s_deathHidesHud = false;
        }

        /// <summary>
        /// Called from ship upgrade tree UI when opening/closing — other HUD widgets read this flag.
        /// </summary>
        public static void SetShipUpgradeTreeObscuresHud(bool obscures) =>
            s_shipUpgradeTreeObscuresHud = obscures;

        /// <summary>True while the upgrade tree panel should hide conflicting HUD chrome.</summary>
        public static bool ShipUpgradeTreeObscuresHud => s_shipUpgradeTreeObscuresHud;

        /// <summary>
        /// Called from <see cref="MinimapController"/> when expanding or collapsing the full-map
        /// overlay — other HUD widgets read this flag so they do not un-hide themselves next frame.
        /// </summary>
        public static void SetMinimapExpandedObscuresHud(bool obscures) =>
            s_minimapExpandedObscuresHud = obscures;

        /// <summary>True while the expanded minimap should hide the rest of the gameplay HUD.</summary>
        public static bool MinimapExpandedObscuresHud => s_minimapExpandedObscuresHud;

        /// <summary>
        /// True while the local ship is destroyed and waiting to respawn. Gameplay HUD
        /// (minimap, rockets, brakes, leaderboard, vitals) should hide; the death plaque stays.
        /// Cached once per frame so many widgets can ask cheaply.
        /// </summary>
        public static bool LocalPlayerDeathHidesHud
        {
            get
            {
                int frame = Time.frameCount;
                if (s_deathGateFrame == frame)
                    return s_deathHidesHud;

                s_deathGateFrame = frame;
                s_deathHidesHud = ComputeLocalPlayerDeathHidesHud();
                return s_deathHidesHud;
            }
        }

        /// <summary>
        /// Reads the local ghost's <c>ShipState.IsDead</c>. False when we are not in a match
        /// or the ship state is not available (join settle skips the gather).
        /// </summary>
        static bool ComputeLocalPlayerDeathHidesHud()
        {
            // [HYBRID] EcsGameBridge is the GameObject window into the client ECS world.
            // Ghost — NetCode replica of the ship on this client (not a visual sprite).
            if (!EcsGameBridge.IsNetworkInGame() || !EcsGameBridge.HasLocalPlayerShip())
                return false;

            return EcsGameBridge.TryGetLocalShipState(out var ship) && ship.IsDead;
        }
    }
}
