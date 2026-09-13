using UnityEngine;

namespace TitanOrbit.Services
{
    /// <summary>
    /// Ensures a persistent host exists for IAP and Unity Ads when none is placed in a scene.
    /// If you add your own <see cref="TitanOrbitIapManager"/> to a scene, this skips creation.
    /// </summary>
    public static class TitanOrbitServicesRuntimeBootstrap
    {
        const string HostObjectName = "TitanOrbitServices";

        /// <summary>Idempotent; safe from <see cref="TitanOrbit.UI.MainMenu"/> or other early boot code.</summary>
        public static void EnsureHostIfNeeded()
        {
            // --- Dedicated server: no IAP / ads host ---
            // [TITAN-ORBIT] Headless Linux never shows a video. Creating Purchasing here
            // only wastes startup and can log store errors in the server log.
#if UNITY_SERVER && !UNITY_EDITOR
            return;
#endif

            // --- Ensure setup ---
            var existingIap = Object.FindFirstObjectByType<TitanOrbitIapManager>();
            if (existingIap != null)
            {
                if (existingIap.GetComponent<TitanOrbitRewardedAds>() == null)
                    existingIap.gameObject.AddComponent<TitanOrbitRewardedAds>();
                return;
            }

            var go = new GameObject(HostObjectName);
            go.AddComponent<TitanOrbitServiceHub>();
            go.AddComponent<TitanOrbitIapManager>();
            go.AddComponent<TitanOrbitGrowIntegration>();
            go.AddComponent<TitanOrbitRewardedAds>();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AfterSceneLoad()
        {
            EnsureHostIfNeeded();
        }
    }
}
