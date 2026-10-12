using System.Runtime.InteropServices;
using UnityEngine;

namespace TitanOrbit.Services
{
    /// <summary>
    /// WebGL rewarded-video backend using the AdSense H5 Ad Placement API.
    /// The page script is <c>adsbygoogle.js</c> for <c>ca-pub-6601000494638397</c>.
    /// <para>
    /// [TITAN-ORBIT] The jslib forwards the placement name and a SendMessage
    /// callback. The page function is <c>TitanOrbitH5RewardedPlay</c>.
    /// Grant only on status <c>complete</c> (the player finished the ad).
    /// </para>
    /// </summary>
    public static class TitanOrbitImaBackend
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        /// <summary>
        /// [UNITY] DllImport("__Internal") calls <c>TitanOrbitIma_PlayRewarded</c>
        /// in <c>Assets/Plugins/WebGL/TitanOrbitImaBridge.jslib</c>.
        /// </summary>
        [DllImport("__Internal")]
        static extern void TitanOrbitIma_PlayRewarded(
            string callbackObjectName,
            string callbackMethodName,
            string placementName);
#endif

        /// <summary>
        /// Asks the page for one rewarded AdSense placement.
        /// No-op outside a WebGL player (the facade simulates in the Editor).
        /// </summary>
        /// <param name="callbackObjectName">GameObject name SendMessage will target.</param>
        /// <param name="callbackMethodName">MonoBehaviour method that accepts a string status.</param>
        /// <param name="placementName">Watch-ad placement id (keep loadout, bonus slot, comms).</param>
        public static void Play(string callbackObjectName, string callbackMethodName, string placementName)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            TitanOrbitIma_PlayRewarded(callbackObjectName, callbackMethodName, placementName ?? "");
#else
            Debug.Log("[TitanOrbitImaBackend] Play is WebGL-only; facade should have simulated already.");
#endif
        }
    }
}
