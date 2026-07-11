using UnityEngine;

namespace TitanOrbit.UI
{
    // --- Type members ---
    /// <summary>
    /// Minimal HUD coordination stub for the ECS client era. The full Netcode-for-GameObjects HUD
    /// was removed; this class preserves one static flag so <see cref="OrbitStationUI"/> can hide
    /// overlapping HUD elements when the ship upgrade tree panel is open. Client only.
    /// </summary>
    public class HUDController : MonoBehaviour
    {
        // [TITAN-ORBIT] When true, bottom HUD stats defer to the full-screen upgrade tree overlay.
        static bool s_shipUpgradeTreeObscuresHud;

        /// <summary>
        /// Called from ship upgrade tree UI when opening/closing — other HUD widgets read this flag.
        /// </summary>
        public static void SetShipUpgradeTreeObscuresHud(bool obscures) =>
            s_shipUpgradeTreeObscuresHud = obscures;

        /// <summary>True while the upgrade tree panel should hide conflicting HUD chrome.</summary>
        public static bool ShipUpgradeTreeObscuresHud => s_shipUpgradeTreeObscuresHud;
    }
}
