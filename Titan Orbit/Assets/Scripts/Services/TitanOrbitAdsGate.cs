using UnityEngine;

namespace TitanOrbit.Services
{
    /// <summary>
    /// Entry point for ad eligibility. Orbit Unlocked (<see cref="TitanOrbitEntitlements.IsOrbitUnlockedOwned"/>)
    /// turns videos off. Interstitials stay a no-op. Rewarded video goes through
    /// <see cref="TitanOrbitRewardedAds"/> after this gate.
    /// </summary>
    public static class TitanOrbitAdsGate
    {
        /// <summary>
        /// True when this client should play a video (no Orbit Unlocked entitlement).
        /// Owners still get death keep-loadout instantly via the rewarded facade.
        /// The +1 slot is auto-granted for owners and never shows this gate.
        /// </summary>
        public static bool ShouldShowAds => !TitanOrbitEntitlements.IsOrbitUnlockedOwned;

        public static bool TryBeginInterstitial(string placementId, out string skipReason)
        {
            // --- Attempt resolution ---
            skipReason = null;
            if (string.IsNullOrEmpty(placementId))
            {
                skipReason = "empty_placement";
                return false;
            }

            if (!ShouldShowAds)
            {
                skipReason = "orbit_unlocked_owned";
                return false;
            }

            return true;
        }

        public static bool TryShowInterstitial(string placementId)
        {
            // --- Attempt resolution ---
            if (!TryBeginInterstitial(placementId, out _))
            {
                return false;
            }

            Debug.Log("[TitanOrbitAdsGate] Interstitial not shown (Unity Ads package removed).");
            return false;
        }
    }
}
