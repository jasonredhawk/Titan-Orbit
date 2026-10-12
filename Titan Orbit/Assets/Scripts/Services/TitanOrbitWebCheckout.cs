using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Unity.Services.Authentication;
using Unity.Services.Core;
using UnityEngine;
using UnityEngine.Networking;

namespace TitanOrbit.Services
{
    /// <summary>
    /// WebGL Orbit Unlocked buy path. Asks the Cloudflare Worker for a Stripe Checkout
    /// URL, then sends the browser there. The Worker grants the account after payment.
    /// One click, not a per-frame path. Editor and mobile builds keep Unity IAP.
    /// </summary>
    public static class TitanOrbitWebCheckout
    {
        const string CheckoutUrl = "https://titanorbit-checkout.jason-redhawk.workers.dev/checkout";
        const string PriceUrl = "https://titanorbit-checkout.jason-redhawk.workers.dev/price";
        const string AllowedCheckoutPrefix = "https://checkout.stripe.com/";

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")]
        static extern void TitanOrbitNavigateBrowser(string url);

        [DllImport("__Internal")]
        static extern IntPtr TitanOrbitTakeCheckoutReturn();

        [DllImport("__Internal")]
        static extern void TitanOrbitFreeCheckoutReturn(IntPtr ptr);
#endif

        /// <summary>
        /// "success" or "cancel" after Stripe returns, then the value is cleared.
        /// The page removes the matching query from the address bar before the game starts.
        /// </summary>
        public static string TakeReturnFlag()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            try
            {
                IntPtr ptr = TitanOrbitTakeCheckoutReturn();
                if (ptr == IntPtr.Zero)
                    return "";
                try
                {
                    return Marshal.PtrToStringUTF8(ptr) ?? "";
                }
                finally
                {
                    TitanOrbitFreeCheckoutReturn(ptr);
                }
            }
            catch (Exception)
            {
                return "";
            }
#else
            return "";
#endif
        }

        /// <summary>
        /// Label for the active Stripe Price, such as "$20". Empty when the Worker
        /// cannot read the product. One request when the buy sheet opens.
        /// </summary>
        public static async Task<string> FetchDisplayPriceAsync()
        {
            using (var request = UnityWebRequest.Get(PriceUrl))
            {
                await SendAsync(request);
                if (!IsSuccess(request))
                    return "";
                return ReadJsonString(request.downloadHandler.text ?? "", "display");
            }
        }

        /// <summary>Creates a Checkout session for the signed-in player and opens it.</summary>
        public static async Task<string> BeginAsync()
        {
            // --- Begin checkout ---
            if (UnityServices.State != ServicesInitializationState.Initialized ||
                !AuthenticationService.Instance.IsSignedIn)
                return "Sign in again, then try UNLOCK.";

            string playerId = AuthenticationService.Instance.PlayerId;
            string accessToken = AuthenticationService.Instance.AccessToken;
            if (string.IsNullOrEmpty(playerId) || string.IsNullOrEmpty(accessToken))
                return "Sign in again, then try UNLOCK.";

            string body =
                "{\"playerId\":\"" + JsonEscape(playerId) +
                "\",\"accessToken\":\"" + JsonEscape(accessToken) + "\"}";

            using (var request = new UnityWebRequest(CheckoutUrl, "POST"))
            {
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                await SendAsync(request);

                string response = request.downloadHandler.text ?? "";
                if (!IsSuccess(request))
                {
                    if (response.IndexOf("sign_in_required", StringComparison.Ordinal) >= 0)
                        return "Sign in again, then try UNLOCK.";
                    Debug.LogWarning("[TitanOrbitWebCheckout] Checkout failed: " + request.responseCode);
                    return "Checkout didn't open. Try again.";
                }

                string url = ReadJsonString(response, "url");
                if (string.IsNullOrEmpty(url) || !url.StartsWith(AllowedCheckoutPrefix, StringComparison.Ordinal))
                    return "Checkout didn't open. Try again.";

                Navigate(url);
                return null;
            }
        }

        static void Navigate(string url)
        {
            // --- Leave the game for Stripe ---
#if UNITY_WEBGL && !UNITY_EDITOR
            TitanOrbitNavigateBrowser(url);
#else
            Application.OpenURL(url);
#endif
        }

        static string JsonEscape(string value)
        {
            // --- JSON string ---
            if (string.IsNullOrEmpty(value))
                return "";
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        static string ReadJsonString(string json, string key)
        {
            // --- Read one JSON string field ---
            string marker = "\"" + key + "\":\"";
            int start = json.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0)
                return "";
            start += marker.Length;
            int end = json.IndexOf('"', start);
            if (end < 0)
                return "";
            return json.Substring(start, end - start).Replace("\\/", "/");
        }

        static async Task SendAsync(UnityWebRequest request)
        {
            // --- Send ---
            var op = request.SendWebRequest();
            while (!op.isDone)
                await Task.Yield();
        }

        static bool IsSuccess(UnityWebRequest request)
        {
#if UNITY_2020_2_OR_NEWER
            return request.result == UnityWebRequest.Result.Success;
#else
            return !request.isNetworkError && !request.isHttpError;
#endif
        }
    }
}
