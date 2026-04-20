using UnityEngine;
using UnityEngine.Advertisements;

namespace TitanOrbit.Services
{
    /// <summary>
    /// Unity Ads initialization and hooks for Grow / User Acquisition workflows.
    /// Set platform Game IDs from the Unity Ads dashboard (must match UA / MMP configuration). MMP SDKs (AppsFlyer, Adjust, etc.) are integrated separately per partner docs.
    /// </summary>
    public class TitanOrbitGrowIntegration : MonoBehaviour, IUnityAdsInitializationListener
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

        /// <summary>Initializes the Unity Ads SDK when a Game ID is set for the current platform.</summary>
        public void TryInitializeAdvertisement()
        {
            if (!TitanOrbitAdsGate.ShouldShowAds)
            {
                Debug.Log("[TitanOrbitGrowIntegration] Skipping Ads init (remove-ads entitlement active).");
                return;
            }

#if UNITY_ANDROID
            string gameId = androidGameId;
#elif UNITY_IOS
            string gameId = iOSGameId;
#else
            string gameId = null;
#endif
            if (string.IsNullOrWhiteSpace(gameId))
            {
                Debug.Log("[TitanOrbitGrowIntegration] Skipping Ads init (no Game ID for this platform).");
                return;
            }

            Advertisement.Initialize(gameId.Trim(), testMode, this);
        }

        public void OnInitializationComplete()
        {
            IsAdvertisementInitialized = true;
            Debug.Log("[TitanOrbitGrowIntegration] Unity Ads initialized.");
        }

        public void OnInitializationFailed(UnityAdsInitializationError error, string message)
        {
            IsAdvertisementInitialized = false;
            Debug.LogWarning("[TitanOrbitGrowIntegration] Unity Ads init failed: " + error + " — " + message);
        }

        /// <summary>Forward key gameplay events to your MMP or Analytics for UA attribution (configure mapping in partner dashboards).</summary>
        public static void LogUaFunnelEvent(string eventName, string parameterJson = null)
        {
            if (string.IsNullOrEmpty(eventName))
                return;
            if (string.IsNullOrEmpty(parameterJson))
                Debug.Log("[TitanOrbitGrowIntegration] UA event: " + eventName);
            else
                Debug.Log("[TitanOrbitGrowIntegration] UA event: " + eventName + " data=" + parameterJson);
        }
    }
}
