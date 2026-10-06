#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Unity.Services.Authentication;
using UnityEngine;
using UnityEngine.Networking;

namespace TitanOrbit.Services
{
    /// <summary>
    /// Unity's <see cref="PlayerAccountService"/> uses <c>BrowserUtils</c> only for Editor/Standalone, Android, and iOS.
    /// On WebGL it is <c>null</c>, so <c>StartSignInAsync</c> cannot open the OAuth flow. This helper runs the same
    /// PKCE redirect flow using <see cref="Application.OpenURL"/> and completes it after the redirect reloads the game.
    /// Register the redirect URL from <see cref="GetExpectedOAuthRedirectUri"/> in the Unity Dashboard (Player Accounts / Authentication).
    /// </summary>
    public static class WebGlUnityPlayerAccountBrowser
    {
        const string AuthUrl = "https://player-login.unity.com/v1/oauth2/auth";
        const string TokenUrl = "https://player-login.unity.com/v1/oauth2/token";
        const string CodeChallengeMethod = "S256";

        const string PrefPending = "TitanOrbitPa_Pending";
        const string PrefVerifier = "TitanOrbitPa_Verifier";
        const string PrefState = "TitanOrbitPa_State";
        const string PrefRedirect = "TitanOrbitPa_Redirect";
        const string PrefLink = "TitanOrbitPa_Link";

        [DllImport("__Internal")]
        static extern int TitanOrbitOAuth_ReplaceUrl(string url);

        [DllImport("__Internal")]
        static extern int TitanOrbitOAuth_SetItem(string key, string value);

        [DllImport("__Internal")]
        static extern IntPtr TitanOrbitOAuth_GetItem(string key);

        [DllImport("__Internal")]
        static extern void TitanOrbitOAuth_Free(IntPtr ptr);

        /// <summary>
        /// Only scope this Player Accounts client accepts. <c>email</c> and <c>offline_access</c>
        /// are rejected as <c>invalid_scope</c>. An https redirect that is not on the client
        /// allow-list is shown as <c>invalid_request</c> on player-login instead.
        /// </summary>
        const string AuthorizedScope = "openid";

        /// <summary>Redirect URI sent to Unity OAuth; register this exact string for your WebGL deployment.</summary>
        public static string GetExpectedOAuthRedirectUri()
        {
            return BuildRedirectUriFromAbsolute(Application.absoluteURL);
        }

        /// <summary>Begins Unity Player Accounts OAuth (same-tab redirect). Returns after the browser navigation is requested.</summary>
        public static Task<bool> BeginOAuthInBrowserAsync(bool linkWithExistingAuthSession)
        {
            // --- BeginOAuthInBrowserAsync ---
            if (!TryLoadUnityPlayerAccountOAuthSettings(out string clientId, out _))
            {
                Debug.LogWarning("[WebGlUnityPlayerAccountBrowser] Missing Unity Player Accounts Client ID (Resources/UnityPlayerAccountSettings).");
                return Task.FromResult(false);
            }

            string redirectUri = GetExpectedOAuthRedirectUri();
            if (string.IsNullOrEmpty(redirectUri))
            {
                Debug.LogWarning("[WebGlUnityPlayerAccountBrowser] Could not derive OAuth redirect URI from Application.absoluteURL.");
                return Task.FromResult(false);
            }

            ClearPendingOAuthState();

            string verifier = GenerateCodeVerifier();
            string state = Guid.NewGuid().ToString("N");
            string challenge = S256UrlSafeChallenge(verifier);
            // Settings may still list email / offline_access. This client rejects those.
            string authUrl = BuildAuthorizationUrl(clientId, redirectUri, challenge, state, isSigningUp: false);

            WriteOAuthValue(PrefPending, "1");
            WriteOAuthValue(PrefVerifier, verifier);
            WriteOAuthValue(PrefState, state);
            WriteOAuthValue(PrefRedirect, redirectUri);
            WriteOAuthValue(PrefLink, linkWithExistingAuthSession ? "1" : "0");

            Debug.Log(
                "[WebGlUnityPlayerAccountBrowser] OAuth redirect_uri (add this exact URL on the Unity Player Accounts client): " +
                redirectUri);

            Application.OpenURL(authUrl);
            return Task.FromResult(true);
        }

        public static void ClearPendingOAuthState()
        {
            // --- Clear state ---
            WriteOAuthValue(PrefPending, "");
            WriteOAuthValue(PrefVerifier, "");
            WriteOAuthValue(PrefState, "");
            WriteOAuthValue(PrefRedirect, "");
            WriteOAuthValue(PrefLink, "");
        }

        /// <summary>Call after UGS init and guest session restore when the page may contain an OAuth <c>code</c> query parameter.</summary>
        public static async Task TryResumeOAuthRedirectIfPresentAsync()
        {
            // --- Attempt resolution ---
            if (ReadOAuthValue(PrefPending) != "1")
                return;

            if (!TryParseOAuthQuery(Application.absoluteURL, out string code, out string state, out string oauthError))
                return;

            if (!string.IsNullOrEmpty(oauthError))
            {
                Debug.LogWarning("[WebGlUnityPlayerAccountBrowser] OAuth error from redirect: " + oauthError);
                ClearPendingOAuthState();
                TryStripOAuthQueryFromBrowserUrl();
                return;
            }

            string expectedState = ReadOAuthValue(PrefState);
            if (string.IsNullOrEmpty(expectedState) || !string.Equals(state, expectedState, StringComparison.Ordinal))
            {
                Debug.LogWarning("[WebGlUnityPlayerAccountBrowser] OAuth state mismatch; ignoring redirect.");
                ClearPendingOAuthState();
                TryStripOAuthQueryFromBrowserUrl();
                return;
            }

            string verifier = ReadOAuthValue(PrefVerifier);
            string redirectUri = ReadOAuthValue(PrefRedirect);
            bool link = ReadOAuthValue(PrefLink) != "0";
            if (!TryLoadUnityPlayerAccountOAuthSettings(out string clientId, out _))
                clientId = null;

            if (string.IsNullOrEmpty(verifier) || string.IsNullOrEmpty(redirectUri) || string.IsNullOrEmpty(clientId))
            {
                Debug.LogWarning("[WebGlUnityPlayerAccountBrowser] Missing stored OAuth data.");
                ClearPendingOAuthState();
                TryStripOAuthQueryFromBrowserUrl();
                return;
            }

            string body =
                "code=" + Uri.EscapeDataString(code) +
                "&redirect_uri=" + Uri.EscapeDataString(redirectUri) +
                "&client_id=" + Uri.EscapeDataString(clientId) +
                "&code_verifier=" + Uri.EscapeDataString(verifier) +
                "&grant_type=authorization_code";

            using (var request = new UnityWebRequest(TokenUrl, "POST"))
            {
                byte[] raw = Encoding.UTF8.GetBytes(body);
                request.uploadHandler = new UploadHandlerRaw(raw);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/x-www-form-urlencoded");

                var op = request.SendWebRequest();
                while (!op.isDone)
                    await Task.Yield();

#if UNITY_2020_2_OR_NEWER
                if (request.result != UnityWebRequest.Result.Success)
#else
                if (request.isNetworkError || request.isHttpError)
#endif
                {
                    Debug.LogWarning("[WebGlUnityPlayerAccountBrowser] Token exchange failed: " + request.error + " " + request.downloadHandler.text);
                    ClearPendingOAuthState();
                    TryStripOAuthQueryFromBrowserUrl();
                    return;
                }

                string json = request.downloadHandler.text;
                if (TryExtractJsonStringField(json, "error", out string tokenErr) && !string.IsNullOrEmpty(tokenErr))
                {
                    Debug.LogWarning("[WebGlUnityPlayerAccountBrowser] Token endpoint error: " + tokenErr);
                    ClearPendingOAuthState();
                    TryStripOAuthQueryFromBrowserUrl();
                    return;
                }

                if (!TryExtractJsonStringField(json, "access_token", out string accessToken) || string.IsNullOrEmpty(accessToken))
                {
                    Debug.LogWarning("[WebGlUnityPlayerAccountBrowser] Token response missing access_token.");
                    ClearPendingOAuthState();
                    TryStripOAuthQueryFromBrowserUrl();
                    return;
                }

                // Shared path: Link, or on AccountAlreadyLinked → SignOut + SignInWithUnity (persists session).
                bool ok = await UnityGameServicesBootstrap.CompleteAuthenticationWithUnityAccessTokenAsync(
                    accessToken,
                    preferLink: link);
                if (!ok)
                {
                    Debug.LogWarning("[WebGlUnityPlayerAccountBrowser] Completing Unity auth failed.");
                    ClearPendingOAuthState();
                    TryStripOAuthQueryFromBrowserUrl();
                    return;
                }

                UnityGameServicesBootstrap.NotifyAuthStateChangedFromWebGlOAuthResume();
                ClearPendingOAuthState();
                TryStripOAuthQueryFromBrowserUrl();
            }
        }

        static void TryStripOAuthQueryFromBrowserUrl()
        {
            // --- Attempt resolution ---
            try
            {
                string clean = BuildRedirectUriFromAbsolute(Application.absoluteURL);
                if (!string.IsNullOrEmpty(clean))
                    TitanOrbitOAuth_ReplaceUrl(clean);
            }
            catch (Exception)
            {
                // Optional .jslib not present or replaceState unsupported.
            }
        }

        internal static string BuildRedirectUriFromAbsolute(string absoluteUrl)
        {
            // --- Build data ---
            if (string.IsNullOrEmpty(absoluteUrl))
                return null;
            try
            {
                var uri = new Uri(absoluteUrl);
                return uri.GetLeftPart(UriPartial.Path);
            }
            catch (Exception)
            {
                return null;
            }
        }

        static string BuildAuthorizationUrl(string clientId, string redirectUri, string codeChallenge, string state, bool isSigningUp)
        {
            // --- Build data ---
            var sb = new StringBuilder(512);
            sb.Append(AuthUrl);
            sb.Append("?response_type=code&redirect_uri=").Append(Uri.EscapeDataString(redirectUri));
            sb.Append("&response_mode=query&client_id=").Append(Uri.EscapeDataString(clientId));
            sb.Append("&state=").Append(Uri.EscapeDataString(state));
            sb.Append("&code_challenge=").Append(Uri.EscapeDataString(codeChallenge));
            sb.Append("&code_challenge_method=").Append(CodeChallengeMethod);
            if (isSigningUp)
                sb.Append("&action=sign-up");
            // Always openid. Semicolon scopes (openid;email;offline_access) are unauthorized
            // on this client and Unity answers invalid_scope / invalid_request.
            sb.Append("&scope=").Append(AuthorizedScope);
            return sb.ToString();
        }

        /// <summary>
        /// Writes PlayerPrefs and localStorage. localStorage commits before the browser leaves the page.
        /// </summary>
        static void WriteOAuthValue(string key, string value)
        {
            // --- Persist OAuth field ---
            if (string.IsNullOrEmpty(value))
                PlayerPrefs.DeleteKey(key);
            else if (key == PrefPending)
                PlayerPrefs.SetInt(key, value == "1" ? 1 : 0);
            else
                PlayerPrefs.SetString(key, value);
            PlayerPrefs.Save();

            try
            {
                TitanOrbitOAuth_SetItem(key, value ?? "");
            }
            catch (Exception)
            {
                // .jslib missing in this build.
            }
        }

        /// <summary>localStorage first, then PlayerPrefs. Empty when neither has the key.</summary>
        static string ReadOAuthValue(string key)
        {
            // --- Read OAuth field ---
            string local = ReadLocalStorage(key);
            if (!string.IsNullOrEmpty(local))
                return local;

            if (key == PrefPending)
                return PlayerPrefs.GetInt(key, 0) == 1 ? "1" : "";
            return PlayerPrefs.GetString(key, "");
        }

        static string ReadLocalStorage(string key)
        {
            // --- Read localStorage ---
            try
            {
                IntPtr ptr = TitanOrbitOAuth_GetItem(key);
                if (ptr == IntPtr.Zero)
                    return "";
                try
                {
                    return Marshal.PtrToStringUTF8(ptr) ?? "";
                }
                finally
                {
                    TitanOrbitOAuth_Free(ptr);
                }
            }
            catch (Exception)
            {
                return "";
            }
        }

        static bool TryParseOAuthQuery(string absoluteUrl, out string code, out string state, out string error)
        {
            // --- Attempt resolution ---
            code = null;
            state = null;
            error = null;
            if (string.IsNullOrEmpty(absoluteUrl))
                return false;
            int hash = absoluteUrl.IndexOf('#', StringComparison.Ordinal);
            if (hash >= 0)
                absoluteUrl = absoluteUrl.Substring(0, hash);
            int q = absoluteUrl.IndexOf('?', StringComparison.Ordinal);
            if (q < 0 || q >= absoluteUrl.Length - 1)
                return false;
            string query = absoluteUrl.Substring(q + 1);
            if (string.IsNullOrEmpty(query))
                return false;

            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string pair in query.Split('&'))
            {
                int eq = pair.IndexOf('=');
                if (eq <= 0)
                    continue;
                string k = Uri.UnescapeDataString(pair.Substring(0, eq));
                string v = Uri.UnescapeDataString(pair.Substring(eq + 1));
                dict[k] = v;
            }

            dict.TryGetValue("code", out code);
            dict.TryGetValue("state", out state);
            dict.TryGetValue("error", out error);
            return !string.IsNullOrEmpty(code);
        }

        static bool TryExtractJsonStringField(string json, string fieldName, out string value)
        {
            // --- Attempt resolution ---
            value = null;
            if (string.IsNullOrEmpty(json))
                return false;
            string needle = "\"" + fieldName + "\":\"";
            int i = json.IndexOf(needle, StringComparison.Ordinal);
            if (i < 0)
                return false;
            i += needle.Length;
            int end = json.IndexOf('"', i);
            if (end < 0)
                return false;
            value = json.Substring(i, end - i);
            return true;
        }

        const string CodeChars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

        static string GenerateCodeVerifier()
        {
            // --- GenerateCodeVerifier ---
            const int length = 128;
            var bytes = new byte[length];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);
            var sb = new StringBuilder(length);
            for (int i = 0; i < length; i++)
                sb.Append(CodeChars[bytes[i] % CodeChars.Length]);
            return sb.ToString();
        }

        static string S256UrlSafeChallenge(string verifier)
        {
            // --- S256UrlSafeChallenge ---
            byte[] data = Encoding.UTF8.GetBytes(verifier);
            byte[] hash;
            using (var sha = SHA256.Create())
                hash = sha.ComputeHash(data);
            return Convert.ToBase64String(hash)
                .Replace('+', '-')
                .Replace('/', '_')
                .TrimEnd('=');
        }

        /// <summary>
        /// <c>UnityPlayerAccountSettings</c> is internal to Unity's Player Accounts assembly, so it cannot be
        /// referenced from game code in WebGL player builds. Read the same <c>Resources/UnityPlayerAccountSettings</c>
        /// asset via reflection.
        /// </summary>
        static bool TryLoadUnityPlayerAccountOAuthSettings(out string clientId, out string scope)
        {
            // --- Attempt resolution ---
            clientId = null;
            scope = null;
            var asset = Resources.Load("UnityPlayerAccountSettings");
            if (asset == null)
                return false;

            const string expectedTypeName = "Unity.Services.Authentication.PlayerAccounts.UnityPlayerAccountSettings";
            var t = asset.GetType();
            if (!string.Equals(t.FullName, expectedTypeName, StringComparison.Ordinal))
            {
                Debug.LogWarning("[WebGlUnityPlayerAccountBrowser] Unexpected asset type: " + t.FullName);
                return false;
            }

            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
            var pClient = t.GetProperty("ClientId", flags);
            var pScope = t.GetProperty("Scope", flags);
            if (pClient == null || pScope == null)
                return false;

            clientId = pClient.GetValue(asset, null) as string;
            scope = pScope.GetValue(asset, null) as string;
            return !string.IsNullOrEmpty(clientId);
        }
    }
}
#else
using System.Threading.Tasks;

namespace TitanOrbit.Services
{
    /// <summary>WebGL-only OAuth bridge; other platforms use Unity Player Accounts SDK browser support.</summary>
    public static class WebGlUnityPlayerAccountBrowser
    {
        public static string GetExpectedOAuthRedirectUri() => null;

        public static Task<bool> BeginOAuthInBrowserAsync(bool linkWithExistingAuthSession) => Task.FromResult(false);

        public static void ClearPendingOAuthState() { }

        public static Task TryResumeOAuthRedirectIfPresentAsync() => Task.CompletedTask;
    }
}
#endif
