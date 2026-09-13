using UnityEngine;

namespace TitanOrbit.Services
{
    /// <summary>
    /// Entry point for ad eligibility (remove-ads IAP).
    /// Interstitials stay a no-op. Rewarded video goes through
    /// <see cref="TitanOrbitRewardedAds"/> after this gate.
    /// </summary>
    public static class TitanOrbitAdsGate
    {
        /// <summary>
        /// True when this client should play a video (no remove-ads entitlement).
        /// Remove-ads owners still get death / extra-slot rewards instantly via the facade.
        /// </summary>
        public static bool ShouldShowAds => !TitanOrbitEntitlements.IsRemoveAdsOwned;

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
                skipReason = "remove_ads_owned";
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
