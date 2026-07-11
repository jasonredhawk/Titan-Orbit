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
            // --- Ensure setup ---
            if (Object.FindFirstObjectByType<TitanOrbitIapManager>() != null)
                return;

            var go = new GameObject(HostObjectName);
            go.AddComponent<TitanOrbitServiceHub>();
            go.AddComponent<TitanOrbitIapManager>();
            go.AddComponent<TitanOrbitGrowIntegration>();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AfterSceneLoad()
        {
            EnsureHostIfNeeded();
        }
    }
}
