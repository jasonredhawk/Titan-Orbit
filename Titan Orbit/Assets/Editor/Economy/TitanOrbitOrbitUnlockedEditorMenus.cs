using TitanOrbit.Services;
using UnityEditor;
using UnityEngine;

namespace TitanOrbit.Editor.Economy
{
    /// <summary>
    /// [EDITOR] Play Mode helpers for Orbit Unlocked without a store receipt.
    /// Grants or revokes the local PlayerPrefs entitlement so customize locks,
    /// ads, and the auto +1 slot can be tested in the Editor.
    /// Not compiled into player or dedicated-server binaries.
    /// </summary>
    public static class TitanOrbitOrbitUnlockedEditorMenus
    {
        const string GrantPath = "TitanOrbit/Economy/Grant Orbit Unlocked (Editor)";
        const string RevokePath = "TitanOrbit/Economy/Revoke Orbit Unlocked (Editor)";

        /// <summary>Marks Orbit Unlocked owned on this machine. Immediate — no store.</summary>
        [MenuItem(GrantPath)]
        public static void GrantOrbitUnlocked()
        {
            // --- Editor grant ---
            TitanOrbitEntitlements.SetOrbitUnlockedOwned(true);
            Debug.Log("[TitanOrbit] Editor grant: Orbit Unlocked is now owned.");
        }

        /// <summary>Clears the local entitlement so free-player locks return.</summary>
        [MenuItem(RevokePath)]
        public static void RevokeOrbitUnlocked()
        {
            // --- Editor revoke ---
            TitanOrbitEntitlements.SetOrbitUnlockedOwned(false);
            Debug.Log("[TitanOrbit] Editor revoke: Orbit Unlocked is no longer owned.");
        }

        /// <summary>Checkmark when the local flag is already on.</summary>
        [MenuItem(GrantPath, true)]
        public static bool ValidateGrant()
        {
            Menu.SetChecked(GrantPath, TitanOrbitEntitlements.IsOrbitUnlockedOwned);
            return true;
        }

        /// <summary>Checkmark when the local flag is already off.</summary>
        [MenuItem(RevokePath, true)]
        public static bool ValidateRevoke()
        {
            Menu.SetChecked(RevokePath, !TitanOrbitEntitlements.IsOrbitUnlockedOwned);
            return true;
        }
    }
}
