using UnityEngine;

namespace TitanOrbit.Services
{
    /// <summary>
    /// Entry point for ad eligibility (remove-ads IAP). Showing interstitials is a no-op while Unity Ads is not in the project.
    /// </summary>
    public static class TitanOrbitAdsGate
    {
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
