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
    /// </summary>
    public static class UnityGameServicesBootstrap
    {
        static bool _authEventsHooked;
        static bool _playerAccountHooksHooked;
        static TaskCompletionSource<bool> _pendingUnityAuthCompletion;
        static bool _pendingLinkInsteadOfSignIn;
        /// <summary>Serializes guest init so IAP, MainMenu, and <see cref="Networking.NetworkGameManager"/> do not trip "already signing in".</summary>
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
            string id = PlayerId;
            if (string.IsNullOrEmpty(id))
                return "—";
            if (id.Length <= 12)
                return id;
            return id.Substring(0, 4) + "…" + id.Substring(id.Length - 6);
        }

        public static async Task InitializeUnityServicesAsync()
        {
            if (UnityServices.State == ServicesInitializationState.Initialized)
                return;
            await UnityServices.InitializeAsync();
            RegisterCoreAuthEventsOnce();
        }

        /// <summary>
        /// Uses <see cref="AuthenticationService.Instance.SignInAnonymouslyAsync"/> to create a guest session or
        /// <b>restore a cached session</b> (including Unity Player Account–linked players) per Unity session docs.
        /// </summary>
        static async Task EnsureAuthenticationSessionRestoredAsync()
        {
            var auth = AuthenticationService.Instance;
            if (auth.IsAuthorized)
                return;

            const int maxAttempts = 12;
            Exception last = null;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    await auth.SignInAnonymouslyAsync();
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
            try
            {
                if (UnityServices.State == ServicesInitializationState.Initialized &&
                    AuthenticationService.Instance.IsSignedIn &&
                    AuthenticationService.Instance.IsAuthorized)
                {
#if UNITY_WEBGL && !UNITY_EDITOR
                    await WebGlUnityPlayerAccountBrowser.TryResumeOAuthRedirectIfPresentAsync();
#endif
                    return true;
                }

                await EnsureUnityServicesAndAnonymousAuthLockedAsync();
                bool ok = AuthenticationService.Instance.IsSignedIn && AuthenticationService.Instance.IsAuthorized;
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
        /// Use for menus and session checks: true if <see cref="HasUnityPlayerAccountLinked"/> or Unity Player Accounts
        /// still has an access token (common when <see cref="GetPlayerInfoAsync"/> temporarily drops the "unity" identity).
        /// </summary>
        public static bool IsUnityAccountActiveForUi()
        {
            if (HasUnityPlayerAccountLinked())
                return true;
            if (UnityServicesNotReadyYet() || !IsAuthorizedSession())
                return false;
            try
            {
                return PlayerAccountService.Instance.IsSignedIn &&
                       !string.IsNullOrEmpty(PlayerAccountService.Instance.AccessToken);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// For players already in an anonymous UGS session, links Unity Player Accounts (keeps this player profile).
        /// Otherwise performs a full Unity sign-in.
        /// </summary>
        public static async Task<bool> SignInOrLinkUnityPlayerAccountUsingBrowserAsync()
        {
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
            if (IsUnityAccountActiveForUi())
                return true;
#if UNITY_WEBGL && !UNITY_EDITOR
            return await WebGlUnityPlayerAccountBrowser.BeginOAuthInBrowserAsync(AuthenticationService.Instance.IsSignedIn);
#else
            if (AuthenticationService.Instance.IsSignedIn)
                return await LinkUnityPlayerAccountUsingBrowserAsync();
            return await SignInWithUnityPlayerAccountUsingBrowserAsync();
#endif
        }

        /// <summary>Opens the Unity Player Accounts browser flow, then signs into Authentication with the returned token.</summary>
        public static async Task<bool> SignInWithUnityPlayerAccountUsingBrowserAsync()
        {
            await InitializeUnityServicesAsync();
            RegisterPlayerAccountHooksOnce();
            _pendingLinkInsteadOfSignIn = false;
            _pendingUnityAuthCompletion = new TaskCompletionSource<bool>();

            try
            {
                if (PlayerAccountService.Instance.IsSignedIn &&
                    !string.IsNullOrEmpty(PlayerAccountService.Instance.AccessToken))
                {
                    await AuthenticationService.Instance.SignInWithUnityAsync(PlayerAccountService.Instance.AccessToken);
                    TitanOrbitFriendsCoordinator.ResetAfterAuthChange();
                    await TryFetchPlayerInfoForUiAsync(allowReplacePlayerInfo: false);
                    AuthStateChanged?.Invoke();
                    _pendingUnityAuthCompletion.TrySetResult(AuthenticationService.Instance.IsSignedIn);
                    return AuthenticationService.Instance.IsSignedIn;
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
                    await AuthenticationService.Instance.LinkWithUnityAsync(PlayerAccountService.Instance.AccessToken);
                    TitanOrbitFriendsCoordinator.ResetAfterAuthChange();
                    await TryFetchPlayerInfoForUiAsync(allowReplacePlayerInfo: false);
                    _pendingUnityAuthCompletion.TrySetResult(true);
                    AuthStateChanged?.Invoke();
                    return true;
                }

                await PlayerAccountService.Instance.StartSignInAsync();
                return await WaitForPendingAuthAsync(TimeSpan.FromMinutes(5));
            }
            catch (AuthenticationException ex) when (ex.ErrorCode == AuthenticationErrorCodes.AccountAlreadyLinked)
            {
                Debug.LogWarning("[UnityGameServicesBootstrap] Account already linked.");
                _pendingUnityAuthCompletion?.TrySetResult(false);
                return false;
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
        public static void SignOutAllSessions(bool clearAuthenticationSession = true)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            WebGlUnityPlayerAccountBrowser.ClearPendingOAuthState();
#endif
            if (UnityServices.State != ServicesInitializationState.Initialized)
                return;
            if (AuthenticationService.Instance.IsSignedIn)
                AuthenticationService.Instance.SignOut(clearAuthenticationSession);
            PlayerAccountService.Instance.SignOut();
            TitanOrbitFriendsCoordinator.ResetAfterAuthChange();
            AuthStateChanged?.Invoke();
        }

        public static string GetAuthStatusSummary()
        {
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
            TitanOrbitFriendsCoordinator.ResetAfterAuthChange();
            AuthStateChanged?.Invoke();
            TrySilentSessionRefreshFromExpiredAsync();
        }

        static async void TrySilentSessionRefreshFromExpiredAsync()
        {
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
            try
            {
                string token = PlayerAccountService.Instance.AccessToken;
                if (string.IsNullOrEmpty(token))
                {
                    _pendingUnityAuthCompletion?.TrySetResult(false);
                    return;
                }

                if (_pendingLinkInsteadOfSignIn)
                    await AuthenticationService.Instance.LinkWithUnityAsync(token);
                else
                    await AuthenticationService.Instance.SignInWithUnityAsync(token);

                TitanOrbitFriendsCoordinator.ResetAfterAuthChange();
                await TryFetchPlayerInfoForUiAsync(allowReplacePlayerInfo: false);
                AuthStateChanged?.Invoke();
                _pendingUnityAuthCompletion?.TrySetResult(AuthenticationService.Instance.IsSignedIn);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[UnityGameServicesBootstrap] Completing Unity auth failed: " + ex.Message);
                _pendingUnityAuthCompletion?.TrySetResult(false);
            }
        }
    }
}
