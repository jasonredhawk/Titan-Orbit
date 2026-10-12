using System.Runtime.InteropServices;

namespace TitanOrbit.Services
{
    /// <summary>
    /// Places the official Sign in with Google button over the sign-in card on WebGL.
    /// Google only issues an ID token from that button, so the game slot and the
    /// page button share the same rectangle.
    /// </summary>
    public static class TitanOrbitGoogleSignInBrowser
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")]
        static extern void TitanOrbitGoogle_SyncButton(
            string clientId,
            string callbackObjectName,
            string callbackMethodName,
            float x0,
            float y0,
            float x1,
            float y1);

        [DllImport("__Internal")]
        static extern void TitanOrbitGoogle_Dismiss();
#endif

        /// <summary>
        /// Moves or creates the Google button. <paramref name="x0"/>/<paramref name="y0"/> is the
        /// bottom-left screen corner and <paramref name="x1"/>/<paramref name="y1"/> is the top-right,
        /// in Unity screen pixels (origin at the bottom-left of the canvas).
        /// </summary>
        public static void SyncButton(
            string callbackObjectName,
            string callbackMethodName,
            float x0,
            float y0,
            float x1,
            float y1)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            string clientId = TitanOrbitGoogleSignInKeys.WebClientId;
            if (string.IsNullOrEmpty(clientId))
                return;
            TitanOrbitGoogle_SyncButton(clientId, callbackObjectName, callbackMethodName, x0, y0, x1, y1);
#else
            _ = callbackObjectName;
            _ = callbackMethodName;
            _ = x0;
            _ = y0;
            _ = x1;
            _ = y1;
#endif
        }

        /// <summary>Removes the page button so it does not sit over the game after the card closes.</summary>
        public static void Dismiss()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            TitanOrbitGoogle_Dismiss();
#endif
        }
    }
}
