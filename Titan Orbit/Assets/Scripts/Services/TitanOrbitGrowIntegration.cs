using UnityEngine;

namespace TitanOrbit.Services
{
    /// <summary>
    /// Placeholder for Grow / UA hooks. Unity Ads (<c>com.unity.ads</c>) is not included in this project so Android resolves do not pull the unity-ads AAR.
    /// </summary>
    public class TitanOrbitGrowIntegration : MonoBehaviour
    {
        [SerializeField] bool initializeOnAwake = true;
        [SerializeField] bool testMode = true;
        [SerializeField] string androidGameId;
        [SerializeField] string iOSGameId;

        public bool IsAdvertisementInitialized { get; private set; }

        void Awake()
        {
            if (initializeOnAwake)
                TryInitializeAdvertisement();
        }

        public void TryInitializeAdvertisement()
        {
            // --- Attempt resolution ---
            IsAdvertisementInitialized = false;
            if (!TitanOrbitAdsGate.ShouldShowAds)
            {
                Debug.Log("[TitanOrbitGrowIntegration] Skipping Ads init (remove-ads entitlement active).");
                return;
            }

            // Rewarded video lives on TitanOrbitRewardedAds (Google IMA WebGL / LevelPlay mobile).
            // androidGameId / iOSGameId stay as Inspector placeholders for a future UA dashboard.
            var rewarded = GetComponent<TitanOrbitRewardedAds>();
            if (rewarded != null)
                rewarded.TryInitializeBackends();

#if UNITY_ANDROID
            string gameId = androidGameId;
#elif UNITY_IOS
            string gameId = iOSGameId;
#else
            string gameId = null;
#endif
            if (string.IsNullOrWhiteSpace(gameId))
            {
                Debug.Log("[TitanOrbitGrowIntegration] No legacy Game ID for this platform (rewarded facade still inits).");
                return;
            }

            IsAdvertisementInitialized = true;
            Debug.Log("[TitanOrbitGrowIntegration] Rewarded ads facade initialized; legacy Unity Ads Game ID is unused.");
        }

        public static void LogUaFunnelEvent(string eventName, string parameterJson = null)
        {
            // --- LogUaFunnelEvent ---
            if (string.IsNullOrEmpty(eventName))
                return;
            if (string.IsNullOrEmpty(parameterJson))
                Debug.Log("[TitanOrbitGrowIntegration] UA event: " + eventName);
            else
                Debug.Log("[TitanOrbitGrowIntegration] UA event: " + eventName + " data=" + parameterJson);
        }
    }
}
