using UnityEngine;

namespace TitanOrbit.UI
{
    /// <summary>
    /// ECS-era HUD stub. Preserves the ship-tree obscuring hook used by OrbitStationUI.
    /// Full NGO HUD stats live in git history if needed later.
    /// </summary>
    public class HUDController : MonoBehaviour
    {
        static bool s_shipUpgradeTreeObscuresHud;

        public static void SetShipUpgradeTreeObscuresHud(bool obscures) =>
            s_shipUpgradeTreeObscuresHud = obscures;

        public static bool ShipUpgradeTreeObscuresHud => s_shipUpgradeTreeObscuresHud;
    }
}
