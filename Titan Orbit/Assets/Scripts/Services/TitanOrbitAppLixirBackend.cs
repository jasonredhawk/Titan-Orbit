using System.Runtime.InteropServices;
using UnityEngine;

namespace TitanOrbit.Services
{
    /// <summary>
    /// Thin C# wrapper around AppLixir's official WebGL jslib
    /// (<c>Assets/Plugins/WebGL/AppLixirBridge.jslib</c>).
    /// <para>
    /// [TITAN-ORBIT] We do not rewrite AppLixir's player. This type only passes the
    /// GameObject name, callback method, and publisher API key into
    /// <c>PlayRewardedAd</c>. Results arrive on <see cref="TitanOrbitRewardedAds.OnAppLixirStatus"/>.
    /// Editor / non-WebGL builds compile a no-op so Assembly-CSharp and Services stay green.
    /// </para>
    /// Official sample: https://github.com/applixirinc/applixir-integration
    /// </summary>
    public static class TitanOrbitAppLixirBackend
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        /// <summary>
        /// [UNITY] DllImport("__Internal") is how WebGL C# calls a jslib function
        /// merged into LibraryManager.library.
        /// </summary>
        [DllImport("__Internal")]
        static extern void PlayRewardedAd(string callbackObjectName, string callbackMethodName, string apiKey);
#endif

        /// <summary>
        /// Opens the AppLixir rewarded player over the Unity canvas.
        /// No-op outside a WebGL player (the facade simulates in the Editor).
        /// </summary>
        /// <param name="callbackObjectName">GameObject name SendMessage will target.</param>
        /// <param name="callbackMethodName">MonoBehaviour method that accepts a string status.</param>
        /// <param name="apiKey">Publisher key from the AppLixir dashboard.</param>
        public static void Play(string callbackObjectName, string callbackMethodName, string apiKey)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            PlayRewardedAd(callbackObjectName, callbackMethodName, apiKey ?? "");
#else
            Debug.Log("[TitanOrbitAppLixirBackend] Play is WebGL-only; facade should have simulated already.");
#endif
        }
    }
}
