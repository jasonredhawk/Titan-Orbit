using System.Runtime.InteropServices;
using UnityEngine;

namespace TitanOrbit.Services
{
    /// <summary>
    /// WebGL rewarded-video backend using Google IMA (Interactive Media Ads) HTML5
    /// plus a VAST ad tag. No publisher traffic minimum — works on self-hosted
    /// Cloudflare Pages from day one.
    /// <para>
    /// [TITAN-ORBIT] AppLixir required ~100k monthly users. IMA plays a VAST tag
    /// you own (Google Ad Manager, AdSense for Games, or Google's sample tag).
    /// The jslib only forwards the tag and a SendMessage callback; the player
    /// lives in the WebGL template as <c>TitanOrbitImaPlay</c>.
    /// Grant only on status <c>complete</c>.
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
            string vastAdTagUrl);
#endif

        /// <summary>
        /// Opens the IMA overlay and requests one linear VAST ad.
        /// No-op outside a WebGL player (the facade simulates in the Editor).
        /// </summary>
        /// <param name="callbackObjectName">GameObject name SendMessage will target.</param>
        /// <param name="callbackMethodName">MonoBehaviour method that accepts a string status.</param>
        /// <param name="vastAdTagUrl">VAST / Google Ad Manager tag. Sample tags work for testing.</param>
        public static void Play(string callbackObjectName, string callbackMethodName, string vastAdTagUrl)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            TitanOrbitIma_PlayRewarded(callbackObjectName, callbackMethodName, vastAdTagUrl ?? "");
#else
            Debug.Log("[TitanOrbitImaBackend] Play is WebGL-only; facade should have simulated already.");
#endif
        }
    }
}
