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
        const string CloudSaveHost = "https://cloud-save.services.api.unity.com";

        static string _pushedPlayerId;
        static bool _pushedValue;
        static bool _refreshInFlight;

        /// <summary>Saves <paramref name="owned"/> for <paramref name="playerId"/>. Failures stay local.</summary>
        public static void PushAsync(string playerId, bool owned)
        {
            // --- Push to Cloud Save ---
            _pushedPlayerId = playerId;
            _pushedValue = owned;
            _ = PushAsyncInternal(playerId, owned);
        }

        /// <summary>Loads Cloud Save for the current Unity player and grants when the cloud copy is owned.</summary>
        public static void RefreshForCurrentPlayerAsync()
        {
            // --- Pull from Cloud Save ---
            if (!UnityGameServicesBootstrap.HasUnityPlayerAccountLinked())
                return;
            string playerId = UnityGameServicesBootstrap.PlayerId;
            if (string.IsNullOrEmpty(playerId) || _refreshInFlight)
                return;
            _ = RefreshAsyncInternal(playerId);
        }

        static async Task PushAsyncInternal(string playerId, bool owned)
        {
            // --- PushAsyncInternal ---
            string body = "{\"key\":\"" + CloudKey + "\",\"value\":" + (owned ? "true" : "false") + "}";
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
                        return;
                    }

                    // A write that just happened wins over a stale false from Cloud Save.
                    if (string.Equals(_pushedPlayerId, playerId, StringComparison.Ordinal) && _pushedValue)
                    {
                        TitanOrbitEntitlements.ApplyCloudOwned(playerId, true);
                        return;
                    }

                    string json = request.downloadHandler.text ?? "";
                    if (json.IndexOf("\"value\":true", StringComparison.Ordinal) >= 0 ||
                        json.IndexOf("\"value\": true", StringComparison.Ordinal) >= 0)
                    {
                        TitanOrbitEntitlements.ApplyCloudOwned(playerId, true);
                    }
                }
            }
            finally
            {
                _refreshInFlight = false;
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
