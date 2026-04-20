using UnityEngine;
using UnityEngine.Advertisements;

namespace TitanOrbit.Services
{
    /// <summary>
    /// Single entry point for deciding whether ads may run. All future <see cref="Advertisement.Show"/> calls should go through here.
    /// </summary>
    public static class TitanOrbitAdsGate
    {
        /// <summary>True when the player has not purchased remove-ads (or local entitlement is unknown).</summary>
        public static bool ShouldShowAds => !TitanOrbitEntitlements.IsRemoveAdsOwned;

        /// <summary>
        /// Use for interstitial/rewarded placements. Returns false when ads are disabled (remove-ads owned).
        /// When you add real placements, call <c>Advertisement.Show(placementId, listener)</c> only after this returns true.
        /// </summary>
        public static bool TryBeginInterstitial(string placementId, out string skipReason)
        {
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

        /// <summary>Optional helper when you already have a listener instance.</summary>
        public static bool TryShowInterstitial(string placementId, IUnityAdsShowListener listener)
        {
            if (!TryBeginInterstitial(placementId, out _))
                return false;
            if (listener == null)
                return false;
            Advertisement.Show(placementId, listener);
            return true;
        }
    }
}
