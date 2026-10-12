using System.Threading.Tasks;
using TitanOrbit.Services;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Main-menu account control: Sign in / Sign out. Client-only presentation.
    /// Sign in opens <see cref="UsernamePasswordSignInScreen"/>, which offers Google and
    /// a username with a password.
    ///
    /// Intentionally shows only the action button — no "Guest · player id" detail text.
    /// After a successful sign-in, Authentication keeps a session token so the next launch
    /// restores that player via <see cref="UnityGameServicesBootstrap.EnsureGuestSessionForOnlineAsync"/>.
    /// </summary>
    public class MainMenuAccountBar : MonoBehaviour
    {
        /// <summary>Optional status TMP (usually null — we hide Guest details by design).</summary>
        [SerializeField] TextMeshProUGUI statusLabel;

        /// <summary>Primary action: Sign in, or Sign out when a durable account is active.</summary>
        [SerializeField] Button actionButton;

        /// <summary>TMP label on <see cref="actionButton"/> (rewritten each refresh).</summary>
        [SerializeField] TextMeshProUGUI actionButtonLabel;

        /// <summary>True while a browser sign-in / link is in flight — blocks double-clicks.</summary>
        bool _busy;

        /// <summary>
        /// Wires UI references when the account bar is built at runtime by <see cref="MainMenuPresenter"/>.
        /// Pass <paramref name="status"/> as null to keep the button-only layout.
        /// </summary>
        public void Configure(TextMeshProUGUI status, Button button, TextMeshProUGUI buttonLabel)
        {
            // --- Assign runtime-built refs ---
            statusLabel = status;
            actionButton = button;
            actionButtonLabel = buttonLabel;

            if (statusLabel != null)
                statusLabel.gameObject.SetActive(false);

            if (actionButton != null)
            {
                actionButton.onClick.RemoveListener(OnActionClicked);
                actionButton.onClick.AddListener(OnActionClicked);
            }

            RefreshUi();
        }

        /// <summary>
        /// [UNITY] Subscribe to auth changes when this bar becomes active on the main menu.
        /// </summary>
        void OnEnable()
        {
            if (actionButton != null)
            {
                actionButton.onClick.RemoveListener(OnActionClicked);
                actionButton.onClick.AddListener(OnActionClicked);
            }

            if (statusLabel != null)
                statusLabel.gameObject.SetActive(false);

            // [TITAN-ORBIT] Bootstrap fires this after sign-in, sign-out, and PlayerInfo refresh.
            UnityGameServicesBootstrap.AuthStateChanged -= OnAuthStateChanged;
            UnityGameServicesBootstrap.AuthStateChanged += OnAuthStateChanged;

            RefreshUi();
            _ = RefreshSessionAsync();
        }

        /// <summary>[UNITY] Unsubscribe so destroyed menus do not keep callbacks alive.</summary>
        void OnDisable()
        {
            if (actionButton != null)
                actionButton.onClick.RemoveListener(OnActionClicked);

            UnityGameServicesBootstrap.AuthStateChanged -= OnAuthStateChanged;
        }

        void OnAuthStateChanged()
        {
            RefreshUi();
        }

        /// <summary>
        /// Restores the cached Authentication session (guest or Unity-linked) and refreshes PlayerInfo
        /// so the button shows Sign out after a previous successful link.
        /// </summary>
        async Task RefreshSessionAsync()
        {
            // --- Soft restore (never blocks the menu) ---
            // EnsureGuestSessionForOnlineAsync restores SessionTokenExists via SignInAnonymously
            // (including Unity-linked players) and syncs the remember-me flag from PlayerInfo.
            await UnityGameServicesBootstrap.EnsureGuestSessionForOnlineAsync();
            RefreshUi();
        }

        /// <summary>
        /// Updates the Sign in / Sign out label from the current Authentication session.
        /// </summary>
        public void RefreshUi()
        {
            // --- Button label only ---
            // [TITAN-ORBIT] IsUnityAccountActiveForUi covers linked identity + Player Accounts token
            // + our PlayerPrefs remember flag while PlayerInfo catches up after cold start.
            bool unityLinked = UnityGameServicesBootstrap.IsUnityAccountActiveForUi();

            if (actionButtonLabel != null)
                actionButtonLabel.text = unityLinked ? "Sign out" : "Sign in";

            if (actionButton != null)
                actionButton.interactable = !_busy;

            // Keep any leftover status label hidden — never show Guest · id next to the button.
            if (statusLabel != null)
            {
                statusLabel.gameObject.SetActive(false);
                statusLabel.text = string.Empty;
            }
        }

        /// <summary>
        /// Button handler: sign in / link Unity Player Account, or sign out when already linked.
        /// </summary>
        async void OnActionClicked()
        {
            if (_busy)
                return;

            // --- Sign out path ---
            if (UnityGameServicesBootstrap.IsUnityAccountActiveForUi())
            {
                // clearAuthenticationSession=true forgets the cached token — next launch is guest again.
                UnityGameServicesBootstrap.SignOutAllSessions(clearAuthenticationSession: true);
                _busy = true;
                RefreshUi();
                try
                {
                    // Re-establish anonymous session so Join game / Lobby still work after sign-out.
                    await UnityGameServicesBootstrap.EnsureGuestSessionForOnlineAsync();
                }
                finally
                {
                    _busy = false;
                    RefreshUi();
                }

                return;
            }

            // --- Sign in path ---
            // One card: Sign in with Google, or a username and password.
            var canvas = GetComponentInParent<Canvas>();
            if (canvas != null)
                UsernamePasswordSignInScreen.Open(canvas.transform);
            else
                Debug.LogWarning("[MainMenuAccountBar] Sign in form needs a Canvas parent.");
        }
    }
}
