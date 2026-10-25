using System;
using System.Text;
using System.Threading.Tasks;
using Unity.Services.Authentication;
using Unity.Services.Core;
using UnityEngine;
using UnityEngine.Networking;

namespace TitanOrbit.Services
{
    /// <summary>
    /// Writes Orbit Unlocked onto the signed-in Unity player via Cloud Save, and
    /// reads it back on the next sign-in so the purchase follows the account.
    /// Local PlayerPrefs stay the cache when Cloud Save is off or the request fails.
    /// One-shot on sign-in and purchase — not a per-frame path.
    /// </summary>
    public static class TitanOrbitAccountEntitlementSync
    {
        const string CloudKey = "orbit_unlocked";
        const string BetaCloudKey = "beta_tester";
        const string CloudSaveHost = "https://cloud-save.services.api.unity.com";

        static string _pushedPlayerId;
        static bool _pushedValue;
        static string _pushedBetaPlayerId;
        static bool _pushedBetaValue;
        static bool _refreshInFlight;
#if UNITY_WEBGL && !UNITY_EDITOR
        static bool _returnRefreshStarted;
#endif

        /// <summary>Saves the beta-tester flag for <paramref name="playerId"/>. Failures stay local.</summary>
        public static void PushBetaAsync(string playerId, bool owned)
        {
            // --- Push beta tester ---
            _pushedBetaPlayerId = playerId;
            _pushedBetaValue = owned;
            _ = PushKeyAsync(playerId, BetaCloudKey, owned);
        }

        /// <summary>Saves <paramref name="owned"/> for <paramref name="playerId"/>. Failures stay local.</summary>
        public static void PushAsync(string playerId, bool owned)
        {
            // --- Push to Cloud Save ---
            _pushedPlayerId = playerId;
            _pushedValue = owned;
            _ = PushAsyncInternal(playerId, owned);
        }

        /// <summary>
        /// After Stripe sends the browser back, Cloud Save may land a moment later.
        /// The page stores that return and removes it from the address bar. A few reads on that return only.
        /// </summary>
        public static void RefreshIfCheckoutReturn()
        {
            // --- Checkout return ---
#if UNITY_WEBGL && !UNITY_EDITOR
            if (_returnRefreshStarted)
                return;
            string flag = TitanOrbitWebCheckout.TakeReturnFlag();
            string url = Application.absoluteURL ?? "";
            bool success = string.Equals(flag, "success", StringComparison.OrdinalIgnoreCase)
                || url.IndexOf("orbit_unlock=success", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!success)
                return;
            _returnRefreshStarted = true;
            _ = RetryOwnedAfterCheckoutAsync();
#endif
        }

        /// <summary>Loads Cloud Save for the current Unity player and grants when the cloud copy is owned.</summary>
        public static void RefreshForCurrentPlayerAsync()
        {
            // --- Pull from Cloud Save ---
            if (!UnityGameServicesBootstrap.HasDurablePlayerAccount())
                return;
            string playerId = UnityGameServicesBootstrap.PlayerId;
            if (string.IsNullOrEmpty(playerId) || _refreshInFlight)
                return;
            _ = RefreshAsyncInternal(playerId);
        }

        static async Task PushAsyncInternal(string playerId, bool owned)
        {
            await PushKeyAsync(playerId, CloudKey, owned);
        }

        static async Task PushKeyAsync(string playerId, string key, bool owned)
        {
            // --- Push one Cloud Save key ---
            string body = "{\"key\":\"" + key + "\",\"value\":" + (owned ? "true" : "false") + "}";
            string url = ItemUrl(playerId);
            if (url == null)
                return;

            using (var request = new UnityWebRequest(url, "POST"))
            {
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                ApplyAuthHeaders(request);
                await SendAsync(request);
                if (!IsSuccess(request))
                {
                    Debug.LogWarning(
                        "[TitanOrbitAccountEntitlementSync] Cloud Save write failed: " +
                        request.responseCode + " " + request.error);
                }
            }
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        static async Task RetryOwnedAfterCheckoutAsync()
        {
            // --- Retry Cloud Save after Checkout ---
            for (int attempt = 0; attempt < 6; attempt++)
            {
                if (UnityGameServicesBootstrap.HasDurablePlayerAccount())
                {
                    string playerId = UnityGameServicesBootstrap.PlayerId;
                    if (!string.IsNullOrEmpty(playerId))
                    {
                        while (_refreshInFlight)
                            await Task.Yield();
                        await RefreshAsyncInternal(playerId);
                        if (TitanOrbitEntitlements.IsOrbitUnlockedOwned)
                            return;
                    }
                }

                await Task.Delay(2000);
            }
        }
#endif

        static async Task RefreshAsyncInternal(string playerId)
        {
            // --- RefreshAsyncInternal ---
            _refreshInFlight = true;
            try
            {
                string url = ItemUrl(playerId);
                if (url == null)
                    return;
                url += "?keys=" + CloudKey;

                using (var request = UnityWebRequest.Get(url))
                {
                    ApplyAuthHeaders(request);
                    await SendAsync(request);
                    if (!IsSuccess(request))
                    {
                        Debug.LogWarning(
                            "[TitanOrbitAccountEntitlementSync] Cloud Save read failed: " +
                            request.responseCode + " " + request.error);
                    }
                    else if (string.Equals(_pushedPlayerId, playerId, StringComparison.Ordinal) && _pushedValue)
                    {
                        // A write that just happened wins over a stale false from Cloud Save.
                        TitanOrbitEntitlements.ApplyCloudOwned(playerId, true);
                    }
                    else
                    {
                        string json = request.downloadHandler.text ?? "";
                        if (json.IndexOf("\"value\":true", StringComparison.Ordinal) >= 0 ||
                            json.IndexOf("\"value\": true", StringComparison.Ordinal) >= 0)
                        {
                            TitanOrbitEntitlements.ApplyCloudOwned(playerId, true);
                        }
                    }
                }

                // Second key, its own response, so a true on Orbit Unlocked cannot
                // be mistaken for the beta-tester item.
                await RefreshBetaAsync(playerId);
            }
            finally
            {
                _refreshInFlight = false;
            }
        }

        static async Task RefreshBetaAsync(string playerId)
        {
            string url = ItemUrl(playerId);
            if (url == null)
                return;
            url += "?keys=" + BetaCloudKey;

            using (var request = UnityWebRequest.Get(url))
            {
                ApplyAuthHeaders(request);
                await SendAsync(request);
                if (!IsSuccess(request))
                {
                    Debug.LogWarning(
                        "[TitanOrbitAccountEntitlementSync] Beta Cloud Save read failed: " +
                        request.responseCode + " " + request.error);
                    return;
                }

                if (string.Equals(_pushedBetaPlayerId, playerId, StringComparison.Ordinal) && _pushedBetaValue)
                {
                    TitanOrbitEntitlements.ApplyCloudBetaOwned(playerId, true);
                    return;
                }

                string json = request.downloadHandler.text ?? "";
                if (json.IndexOf("\"value\":true", StringComparison.Ordinal) >= 0 ||
                    json.IndexOf("\"value\": true", StringComparison.Ordinal) >= 0)
                {
                    TitanOrbitEntitlements.ApplyCloudBetaOwned(playerId, true);
                }
            }
        }

        static string ItemUrl(string playerId)
        {
            // --- Build Cloud Save URL ---
            if (string.IsNullOrEmpty(playerId))
                return null;
            string projectId = Application.cloudProjectId;
            if (string.IsNullOrEmpty(projectId))
                return null;
            return CloudSaveHost + "/v1/data/projects/" + projectId + "/players/" + playerId + "/items";
        }

        static void ApplyAuthHeaders(UnityWebRequest request)
        {
            // --- Auth headers ---
            if (UnityServices.State != ServicesInitializationState.Initialized)
                return;
            string token = AuthenticationService.Instance.AccessToken;
            if (!string.IsNullOrEmpty(token))
                request.SetRequestHeader("Authorization", "Bearer " + token);
            if (!string.IsNullOrEmpty(Application.cloudProjectId))
                request.SetRequestHeader("ProjectId", Application.cloudProjectId);
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
