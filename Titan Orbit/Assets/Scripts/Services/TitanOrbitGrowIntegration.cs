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
            IsAdvertisementInitialized = false;
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

            Debug.Log("[TitanOrbitGrowIntegration] Ads SDK not integrated (Unity Ads package removed); Game ID ignored until package is restored.");
        }

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
