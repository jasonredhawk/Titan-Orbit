using System;
using System.Threading;
using System.Threading.Tasks;
using Unity.Services.Authentication;
using Unity.Services.Authentication.PlayerAccounts;
using Unity.Services.Core;
using System.Linq;
using UnityEngine;

namespace TitanOrbit.Services
{
    /// <summary>
    /// Single entry point for Unity Gaming Services init and Authentication.
    /// Configure Unity Player Accounts in the Editor (Services &gt; Unity Player Accounts) and set <see cref="UnityPlayerAccountSettings"/> client id.
    ///
    /// Session persistence: Unity Authentication stores a session token after anonymous sign-in /
    /// Unity Player Account link. The next launch calls <see cref="SignInAnonymouslyAsync"/> which
    /// <b>restores</b> that cached session (including Unity-linked players) — players should not need
    /// to browser-sign-in every load. We also remember a local PlayerPrefs flag so the Main Menu
    /// shows "Sign out" while PlayerInfo catches up after cold start.
    /// </summary>
    public static class UnityGameServicesBootstrap
    {
        /// <summary>
        /// Local remember-me flag: set after a successful Unity Player Account link / sign-in,
        /// cleared only on explicit Sign out (clearAuthenticationSession).
        /// </summary>
        const string UnityAccountLinkedPrefsKey = "TitanOrbit_UnityAccountLinked_v1";

        /// <summary>Last UGS PlayerId that had a confirmed Unity identity (debug / future validation).</summary>
        const string UnityAccountLinkedPlayerIdPrefsKey = "TitanOrbit_UnityAccountPlayerId_v1";

        static bool _authEventsHooked;
        static bool _playerAccountHooksHooked;
        static TaskCompletionSource<bool> _pendingUnityAuthCompletion;
        static bool _pendingLinkInsteadOfSignIn;
        /// <summary>Serializes guest init so IAP, MainMenu, and session bootstrap do not trip "already signing in".</summary>
        static readonly SemaphoreSlim EnsureGuestSessionGate = new SemaphoreSlim(1, 1);

        /// <summary>Invoked after sign-in, sign-out, or failed Unity Player Account completion.</summary>
        public static event Action AuthStateChanged;

        /// <summary>Used by WebGL OAuth resume path (Player Accounts SDK has no browser binding on WebGL).</summary>
        public static void NotifyAuthStateChangedFromWebGlOAuthResume() => AuthStateChanged?.Invoke();

        /// <remarks>Do not touch <see cref="AuthenticationService.Instance"/> until <see cref="UnityServices"/> has finished initializing.</remarks>
        public static bool IsSignedIn =>
            UnityServices.State == ServicesInitializationState.Initialized && AuthenticationService.Instance.IsSignedIn;

        public static string PlayerId =>
            UnityServices.State == ServicesInitializationState.Initialized && AuthenticationService.Instance.IsSignedIn
                ? AuthenticationService.Instance.PlayerId
                : null;

        public static bool UnityServicesNotReadyYet() =>
            UnityServices.State != ServicesInitializationState.Initialized;

        public static bool IsAuthorizedSession() =>
            UnityServices.State == ServicesInitializationState.Initialized &&
            AuthenticationService.Instance.IsAuthorized;

        /// <summary>
        /// WebGL browsers only treat <c>window.open</c> as user-initiated if it runs in the same synchronous turn as the
        /// click. Any <c>await</c> before <c>PlayerAccountService.Instance.StartSignInAsync</c> drops that user gesture and
        /// the login window is blocked (often with no visible error). Callers should ensure guest init finished first.
        /// </summary>
        static bool WebGlUnityPlayerAccountGestureSafePrerequisitesMet()
        {
            // --- WebGlUnityPlayerAccountGestureSafePrerequisitesMet ---
#if UNITY_WEBGL && !UNITY_EDITOR
            return UnityServices.State == ServicesInitializationState.Initialized &&
                   AuthenticationService.Instance.IsSignedIn &&
                   AuthenticationService.Instance.IsAuthorized;
#else
            return true;
#endif
        }

        /// <summary>Shortened player id for UI (full id is still used by services).</summary>
        public static string GetDisplayPlayerId()
        {
            // --- Compute value ---
            string id = PlayerId;
            if (string.IsNullOrEmpty(id))
                return "—";
            if (id.Length <= 12)
                return id;
            return id.Substring(0, 4) + "…" + id.Substring(id.Length - 6);
        }

        public static async Task InitializeUnityServicesAsync()
        {
            // --- InitializeUnityServicesAsync ---
            if (UnityServices.State == ServicesInitializationState.Initialized)
                return;
            await UnityServices.InitializeAsync();
            RegisterCoreAuthEventsOnce();
        }

        /// <summary>
        /// Uses <see cref="AuthenticationService.Instance.SignInAnonymouslyAsync"/> to create a guest session or
        /// <b>restore a cached session</b> (including Unity Player Account–linked players) per Unity session docs.
        /// When <see cref="IAuthenticationService.SessionTokenExists"/> is true, this refreshes that player —
        /// it does not create a new anonymous account.
        /// </summary>
        static async Task EnsureAuthenticationSessionRestoredAsync()
        {
            // --- Ensure setup ---
            var auth = AuthenticationService.Instance;
            if (auth.IsAuthorized)
                return;

            // [UGS] Log cache state so "have to sign in every launch" is diagnosable in Player.log.
            bool hadCachedToken = auth.SessionTokenExists;
            bool rememberedUnity = PlayerPrefs.GetInt(UnityAccountLinkedPrefsKey, 0) != 0;
            Debug.Log(
                "[UnityGameServicesBootstrap] Restoring auth session. SessionTokenExists=" +
                hadCachedToken + " RememberUnity=" + rememberedUnity);

            const int maxAttempts = 12;
            Exception last = null;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    await auth.SignInAnonymouslyAsync();
                    Debug.Log(
                        "[UnityGameServicesBootstrap] Auth ready. PlayerId=" + auth.PlayerId +
                        " restoredFromCache=" + hadCachedToken);
                    return;
                }
                catch (Exception e)
                {
                    last = e;
                    string msg = e.Message ?? string.Empty;
                    bool waitForConcurrentSignIn = msg.IndexOf("already signing in", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        msg.IndexOf("invalid state", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!waitForConcurrentSignIn || attempt >= maxAttempts)
                        throw;

                    int delayMs = 80 + attempt * 40;
                    await Task.Delay(delayMs);
                    if (auth.IsAuthorized)
                        return;
                }
            }

            if (last != null)
                throw last;
        }

        /// <summary>
        /// WebGL is single-threaded; <see cref="SemaphoreSlim.WaitAsync"/> continuations can fail to resume on the main thread and stall forever.
        /// </summary>
        static async Task AcquireGuestSessionGateAsync()
        {
            // --- AcquireGuestSessionGateAsync ---
#if UNITY_WEBGL && !UNITY_EDITOR
            while (!EnsureGuestSessionGate.Wait(0))
                await Task.Yield();
#else
            await EnsureGuestSessionGate.WaitAsync();
#endif
        }

        /// <summary>Initializes core UGS and anonymous auth; serialized so callers do not hit "already signing in".</summary>
        static async Task EnsureUnityServicesAndAnonymousAuthLockedAsync()
        {
            // --- Ensure setup ---
            await AcquireGuestSessionGateAsync();
            try
            {
                await InitializeUnityServicesAsync();
                await EnsureAuthenticationSessionRestoredAsync();
            }
            finally
            {
                EnsureGuestSessionGate.Release();
            }
        }

        /// <summary>Anonymous session for Relay/Lobby when the player has not used a Unity account.</summary>
        public static Task SignInGuestAsync() => EnsureUnityServicesAndAnonymousAuthLockedAsync();

        /// <summary>Initializes UGS and ensures an anonymous or existing session for online multiplayer APIs.</summary>
        public static async Task<bool> EnsureGuestSessionForOnlineAsync()
        {
            // --- Ensure setup ---
            try
            {
                if (UnityServices.State == ServicesInitializationState.Initialized &&
                    AuthenticationService.Instance.IsSignedIn &&
                    AuthenticationService.Instance.IsAuthorized)
                {
                    // Still refresh PlayerInfo so Unity-link shows after domain reload / cold start.
                    await TryFetchPlayerInfoForUiAsync(allowReplacePlayerInfo: true);
                    SyncRememberFlagFromPlayerInfo();
#if UNITY_WEBGL && !UNITY_EDITOR
                    await WebGlUnityPlayerAccountBrowser.TryResumeOAuthRedirectIfPresentAsync();
#endif
                    return true;
                }

                await EnsureUnityServicesAndAnonymousAuthLockedAsync();
                bool ok = AuthenticationService.Instance.IsSignedIn && AuthenticationService.Instance.IsAuthorized;
                if (ok)
                {
                    // [UGS] Pull identities so HasUnityPlayerAccountLinked is accurate after session restore.
                    await TryFetchPlayerInfoForUiAsync(allowReplacePlayerInfo: true);
                    SyncRememberFlagFromPlayerInfo();
                }
#if UNITY_WEBGL && !UNITY_EDITOR
                if (ok)
                    await WebGlUnityPlayerAccountBrowser.TryResumeOAuthRedirectIfPresentAsync();
#endif
                return ok;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[UnityGameServicesBootstrap] Unity Services failed (offline or build not linked). " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// Refreshes <see cref="AuthenticationService.Instance.PlayerInfo"/> from the server so Unity ID / identities
        /// are available after a cold start (local summary alone can be incomplete until this runs).
        /// </summary>
        /// <param name="allowReplacePlayerInfo">
        /// When false, skips the network call if the local profile already shows a Unity link (GetPlayerInfoAsync replaces
        /// the whole object and can drop a freshly linked identity while the backend catches up).
        /// </param>
        public static async Task TryFetchPlayerInfoForUiAsync(bool allowReplacePlayerInfo = true)
        {
            // --- Attempt resolution ---
            if (UnityServices.State != ServicesInitializationState.Initialized)
                return;
            if (!AuthenticationService.Instance.IsSignedIn)
                return;
            if (!allowReplacePlayerInfo && IsUnityAccountActiveForUi())
                return;
            try
            {
                await AuthenticationService.Instance.GetPlayerInfoAsync();
                AuthStateChanged?.Invoke();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[UnityGameServicesBootstrap] GetPlayerInfoAsync: " + ex.Message);
            }
        }

        /// <summary>True when this Authentication player is linked to a Unity (Player Account) identity.</summary>
        public static bool HasUnityPlayerAccountLinked()
        {
            // --- HasUnityPlayerAccountLinked ---
            if (UnityServices.State != ServicesInitializationState.Initialized || !AuthenticationService.Instance.IsSignedIn)
                return false;
            var info = AuthenticationService.Instance.PlayerInfo;
            if (info == null)
                return false;
            if (!string.IsNullOrEmpty(info.GetUnityId()))
                return true;
            if (info.Identities == null || info.Identities.Count == 0)
                return false;
            // IdProviderKeys.Unity is "unity" (see Unity Authentication package).
            return info.Identities.Any(id =>
                id != null &&
                !string.IsNullOrEmpty(id.TypeId) &&
                string.Equals(id.TypeId, "unity", StringComparison.Ordinal));
        }

        /// <summary>
        /// Use for menus and session checks. True when Authentication has a Unity identity, or when we
        /// previously persisted a successful Unity link and a session token / signed-in session still exists
        /// (PlayerInfo can lag one frame after cold-start restore).
        /// </summary>
        /// <remarks>
        /// Do <b>not</b> treat <see cref="PlayerAccountService"/> access-token alone as signed-in.
        /// That token is in-memory only and vanishes on the next launch — using it made the button say
        /// "Sign out" during the browser session even when Auth never cached a Unity-linked player.
        /// </remarks>
        public static bool IsUnityAccountActiveForUi()
        {
            // --- IsUnityAccountActiveForUi ---
            if (HasUnityPlayerAccountLinked())
                return true;

            if (PlayerPrefs.GetInt(UnityAccountLinkedPrefsKey, 0) == 0)
                return false;

            if (UnityServicesNotReadyYet())
            {
                // Optimistic: last launch linked Unity and we have not finished UGS init yet.
                return true;
            }

            var auth = AuthenticationService.Instance;
            // Remember-me + cached session token (or already signed in) → treat as Unity until proven otherwise.
            return auth.SessionTokenExists || auth.IsSignedIn;
        }

        /// <summary>
        /// Marks that this device successfully authenticated a Unity-linked player.
        /// Only call after <see cref="HasUnityPlayerAccountLinked"/> is true (or SignInWithUnity succeeded
        /// and PlayerInfo confirms the unity identity).
        /// </summary>
        static void RememberUnityAccountLinked()
        {
            PlayerPrefs.SetInt(UnityAccountLinkedPrefsKey, 1);
            if (!string.IsNullOrEmpty(PlayerId))
                PlayerPrefs.SetString(UnityAccountLinkedPlayerIdPrefsKey, PlayerId);
            PlayerPrefs.Save();
            Debug.Log("[UnityGameServicesBootstrap] Remembered Unity account link. PlayerId=" + PlayerId);
        }

        /// <summary>Clears the local remember-me flag (explicit Sign out only).</summary>
        static void ForgetUnityAccountLinked()
        {
            PlayerPrefs.DeleteKey(UnityAccountLinkedPrefsKey);
            PlayerPrefs.DeleteKey(UnityAccountLinkedPlayerIdPrefsKey);
            PlayerPrefs.Save();
        }

        /// <summary>Sync remember-me from live PlayerInfo after session restore / GetPlayerInfo.</summary>
        static void SyncRememberFlagFromPlayerInfo()
        {
            if (HasUnityPlayerAccountLinked())
            {
                RememberUnityAccountLinked();
                return;
            }

            // PlayerInfo loaded and has no Unity identity → clear a stale remember-me from a prior
            // browser session that never successfully cached a Unity-linked Auth player.
            var info = AuthenticationService.Instance.PlayerInfo;
            if (info != null && PlayerPrefs.GetInt(UnityAccountLinkedPrefsKey, 0) != 0)
            {
                Debug.LogWarning(
                    "[UnityGameServicesBootstrap] Cleared stale Unity remember-me — restored session has no unity identity.");
                ForgetUnityAccountLinked();
            }
        }

        /// <summary>
        /// Completes Authentication with a Unity Player Accounts access token.
        /// Prefers link (keeps current player id); on AccountAlreadyLinked, switches to the Unity-owned
        /// player via SignOut(clear) + SignInWithUnityAsync so the session token actually persists.
        /// </summary>
        /// <summary>Used by WebGL OAuth resume and browser SignedIn hooks.</summary>
        internal static async Task<bool> CompleteAuthenticationWithUnityAccessTokenAsync(string accessToken, bool preferLink)
        {
            // --- CompleteAuthenticationWithUnityAccessTokenAsync ---
            if (string.IsNullOrEmpty(accessToken))
                return false;

            var auth = AuthenticationService.Instance;

            try
            {
                if (preferLink && auth.IsSignedIn)
                {
                    try
                    {
                        await auth.LinkWithUnityAsync(accessToken);
                    }
                    catch (AuthenticationException ex) when (ex.ErrorCode == AuthenticationErrorCodes.AccountAlreadyLinked)
                    {
                        // [UGS] This Unity login is already tied to a different UGS player id.
                        // Linking the current guest fails — switch to that Unity player so the
                        // session token we cache is the one that will restore next launch.
                        Debug.LogWarning(
                            "[UnityGameServicesBootstrap] Unity account already linked to another player. " +
                            "Signing out guest and signing in with Unity so persistence works.");
                        auth.SignOut(clearCredentials: true);
                        await auth.SignInWithUnityAsync(accessToken);
                    }
                }
                else
                {
                    if (auth.IsSignedIn)
                        auth.SignOut(clearCredentials: true);
                    await auth.SignInWithUnityAsync(accessToken);
                }

                TitanOrbitFriendsCoordinator.ResetAfterAuthChange();
                await TryFetchPlayerInfoForUiAsync(allowReplacePlayerInfo: false);

                if (HasUnityPlayerAccountLinked() || auth.IsSignedIn)
                {
                    // SignInWithUnity always yields a Unity-authenticated player even if GetPlayerInfo lags.
                    RememberUnityAccountLinked();
                    AuthStateChanged?.Invoke();
                    return true;
                }

                Debug.LogWarning(
                    "[UnityGameServicesBootstrap] Unity token accepted but player has no unity identity yet.");
                AuthStateChanged?.Invoke();
                return auth.IsSignedIn;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[UnityGameServicesBootstrap] Completing Unity auth failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// For players already in an anonymous UGS session, links Unity Player Accounts (keeps this player profile).
        /// Otherwise performs a full Unity sign-in.
        /// </summary>
        public static async Task<bool> SignInOrLinkUnityPlayerAccountUsingBrowserAsync()
        {
            // --- SignInOrLinkUnityPlayerAccountUsingBrowserAsync ---
#if UNITY_WEBGL && !UNITY_EDITOR
            if (!WebGlUnityPlayerAccountGestureSafePrerequisitesMet())
            {
                await EnsureGuestSessionForOnlineAsync();
                if (!WebGlUnityPlayerAccountGestureSafePrerequisitesMet())
                {
                    Debug.LogWarning(
                        "[UnityGameServicesBootstrap] WebGL: Unity account sign-in needs an active guest session first. " +
                        "Wait until you are online, then try again. Also allow popups for this site, and add your exact " +
                        "game URL under Unity Dashboard → Player Accounts / Authentication redirect settings if required.");
#if DEVELOPMENT_BUILD
                    Debug.Log("[UnityGameServicesBootstrap] WebGL current page (redirect context): " + Application.absoluteURL);
#endif
                    return false;
                }
            }
#else
            await EnsureGuestSessionForOnlineAsync();
#endif
            // Already have a Unity-linked Authentication player — nothing to do.
            if (HasUnityPlayerAccountLinked())
            {
                RememberUnityAccountLinked();
                return true;
            }

#if UNITY_WEBGL && !UNITY_EDITOR
            // WebGL: OAuth must start in the same click turn (gesture). Prefer link when a guest exists.
            return await WebGlUnityPlayerAccountBrowser.BeginOAuthInBrowserAsync(AuthenticationService.Instance.IsSignedIn);
#else
            // Prefer link when a guest session exists (keeps lobby player id); CompleteAuthentication…
            // falls back to SignInWithUnity when the Unity account is already tied to another player.
            return AuthenticationService.Instance.IsSignedIn
                ? await LinkUnityPlayerAccountUsingBrowserAsync()
                : await SignInWithUnityPlayerAccountUsingBrowserAsync();
#endif
        }

        /// <summary>Opens the Unity Player Accounts browser flow, then signs into Authentication with the returned token.</summary>
        public static async Task<bool> SignInWithUnityPlayerAccountUsingBrowserAsync()
        {
            // --- SignInWithUnityPlayerAccountUsingBrowserAsync ---
            await InitializeUnityServicesAsync();
            RegisterPlayerAccountHooksOnce();
            _pendingLinkInsteadOfSignIn = false;
            _pendingUnityAuthCompletion = new TaskCompletionSource<bool>();

            try
            {
                if (PlayerAccountService.Instance.IsSignedIn &&
                    !string.IsNullOrEmpty(PlayerAccountService.Instance.AccessToken))
                {
                    bool ok = await CompleteAuthenticationWithUnityAccessTokenAsync(
                        PlayerAccountService.Instance.AccessToken,
                        preferLink: false);
                    _pendingUnityAuthCompletion.TrySetResult(ok);
                    return ok;
                }

                await PlayerAccountService.Instance.StartSignInAsync();
                return await WaitForPendingAuthAsync(TimeSpan.FromMinutes(5));
            }
            catch (PlayerAccountsException ex)
            {
                Debug.LogWarning("[UnityGameServicesBootstrap] Player Accounts: " + ex.Message);
                _pendingUnityAuthCompletion?.TrySetResult(false);
                return false;
            }
            catch (RequestFailedException ex)
            {
                Debug.LogWarning("[UnityGameServicesBootstrap] Request failed: " + ex.Message);
                _pendingUnityAuthCompletion?.TrySetResult(false);
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[UnityGameServicesBootstrap] " + ex.Message);
                _pendingUnityAuthCompletion?.TrySetResult(false);
                return false;
            }
        }

        /// <summary>Links the current Authentication session (e.g. anonymous) to Unity Player Accounts after browser sign-in.</summary>
        public static async Task<bool> LinkUnityPlayerAccountUsingBrowserAsync()
        {
            // --- LinkUnityPlayerAccountUsingBrowserAsync ---
            if (!AuthenticationService.Instance.IsSignedIn)
            {
                Debug.LogWarning("[UnityGameServicesBootstrap] Must be signed in before linking.");
                return false;
            }

            await InitializeUnityServicesAsync();
            RegisterPlayerAccountHooksOnce();
            _pendingLinkInsteadOfSignIn = true;
            _pendingUnityAuthCompletion = new TaskCompletionSource<bool>();

            try
            {
                if (PlayerAccountService.Instance.IsSignedIn &&
                    !string.IsNullOrEmpty(PlayerAccountService.Instance.AccessToken))
                {
                    bool ok = await CompleteAuthenticationWithUnityAccessTokenAsync(
                        PlayerAccountService.Instance.AccessToken,
                        preferLink: true);
                    _pendingUnityAuthCompletion.TrySetResult(ok);
                    return ok;
                }

                await PlayerAccountService.Instance.StartSignInAsync();
                return await WaitForPendingAuthAsync(TimeSpan.FromMinutes(5));
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[UnityGameServicesBootstrap] Link failed: " + ex.Message);
                _pendingUnityAuthCompletion?.TrySetResult(false);
                return false;
            }
        }

        static async Task<bool> WaitForPendingAuthAsync(TimeSpan timeout)
        {
            // --- WaitForPendingAuthAsync ---
            if (_pendingUnityAuthCompletion == null)
                return false;
            Task completed = await Task.WhenAny(_pendingUnityAuthCompletion.Task, Task.Delay(timeout));
            if (completed != _pendingUnityAuthCompletion.Task)
            {
                Debug.LogWarning("[UnityGameServicesBootstrap] Unity Player Account flow timed out.");
                _pendingUnityAuthCompletion.TrySetResult(false);
            }
            return _pendingUnityAuthCompletion.Task.Status == TaskStatus.RanToCompletion &&
                   _pendingUnityAuthCompletion.Task.Result;
        }

        /// <summary>Signs out of Unity Authentication and Unity Player Accounts.</summary>
        /// <param name="clearAuthenticationSession">
        /// When true (default for the Sign out button), deletes the cached session token so the next
        /// launch does <b>not</b> restore the Unity-linked player. Pass false only for soft sign-out tests.
        /// </param>
        public static void SignOutAllSessions(bool clearAuthenticationSession = true)
        {
            // --- SignOutAllSessions ---
#if UNITY_WEBGL && !UNITY_EDITOR
            WebGlUnityPlayerAccountBrowser.ClearPendingOAuthState();
#endif
            if (clearAuthenticationSession)
                ForgetUnityAccountLinked();

            if (UnityServices.State != ServicesInitializationState.Initialized)
            {
                AuthStateChanged?.Invoke();
                return;
            }

            if (AuthenticationService.Instance.IsSignedIn)
                AuthenticationService.Instance.SignOut(clearAuthenticationSession);
            PlayerAccountService.Instance.SignOut();
            TitanOrbitFriendsCoordinator.ResetAfterAuthChange();
            AuthStateChanged?.Invoke();
        }

        public static string GetAuthStatusSummary()
        {
            // --- Compute value ---
            if (UnityServices.State != ServicesInitializationState.Initialized)
                return "Connecting…";

            if (!AuthenticationService.Instance.IsSignedIn)
                return "Not signed in";

            if (IsUnityAccountActiveForUi())
                return "Unity account";

            return "Guest";
        }

        static void RegisterCoreAuthEventsOnce()
        {
            // --- RegisterCoreAuthEventsOnce ---
            if (_authEventsHooked)
                return;
            _authEventsHooked = true;
            AuthenticationService.Instance.SignedIn += OnAuthenticationSignedIn;
            AuthenticationService.Instance.SignedOut += OnAuthenticationSignedOut;
            AuthenticationService.Instance.Expired += OnAuthenticationExpired;
            AuthenticationService.Instance.PlayerInfoChanged += OnAuthenticationPlayerInfoChanged;
        }

        static void OnAuthenticationPlayerInfoChanged(PlayerInfo _)
        {
            AuthStateChanged?.Invoke();
        }

        static void RegisterPlayerAccountHooksOnce()
        {
            // --- RegisterPlayerAccountHooksOnce ---
            if (_playerAccountHooksHooked)
                return;
            _playerAccountHooksHooked = true;
            PlayerAccountService.Instance.SignedIn += OnPlayerAccountSignedIn;
        }

        static void OnAuthenticationSignedIn()
        {
            AuthStateChanged?.Invoke();
        }

        static void OnAuthenticationSignedOut()
        {
            TitanOrbitFriendsCoordinator.ResetAfterAuthChange();
            AuthStateChanged?.Invoke();
        }

        static void OnAuthenticationExpired()
        {
            // --- OnAuthenticationExpired ---
            TitanOrbitFriendsCoordinator.ResetAfterAuthChange();
            AuthStateChanged?.Invoke();
            TrySilentSessionRefreshFromExpiredAsync();
        }

        static async void TrySilentSessionRefreshFromExpiredAsync()
        {
            // --- Attempt resolution ---
            try
            {
                if (UnityServices.State != ServicesInitializationState.Initialized)
                    return;
                if (!AuthenticationService.Instance.SessionTokenExists)
                    return;
                await AuthenticationService.Instance.SignInAnonymouslyAsync();
                await TryFetchPlayerInfoForUiAsync();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[UnityGameServicesBootstrap] Session refresh after expiry failed: " + ex.Message);
            }
            finally
            {
                AuthStateChanged?.Invoke();
            }
        }

        static async void OnPlayerAccountSignedIn()
        {
            // --- OnPlayerAccountSignedIn ---
            try
            {
                string token = PlayerAccountService.Instance.AccessToken;
                if (string.IsNullOrEmpty(token))
                {
                    _pendingUnityAuthCompletion?.TrySetResult(false);
                    return;
                }

                // Shared path handles Link vs SignInWithUnity and AccountAlreadyLinked → switch player.
                bool ok = await CompleteAuthenticationWithUnityAccessTokenAsync(
                    token,
                    preferLink: _pendingLinkInsteadOfSignIn);
                _pendingUnityAuthCompletion?.TrySetResult(ok);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[UnityGameServicesBootstrap] Completing Unity auth failed: " + ex.Message);
                _pendingUnityAuthCompletion?.TrySetResult(false);
            }
        }
    }
}
