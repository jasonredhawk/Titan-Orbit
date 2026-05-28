using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Purchasing;
using TMPro;
using Unity.Netcode;
using TitanOrbit.Networking;
using TitanOrbit.Services;
using System.Threading.Tasks;
using System.Collections.Generic;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Main menu for lobby creation/joining and team selection
    /// </summary>
    public class MainMenu : MonoBehaviour
    {
        [Header("UI References")]
        [SerializeField] private GameObject mainMenuPanel;
        [SerializeField] private GameObject lobbyPanel;
        [SerializeField] private GameObject teamSelectionPanel;
        [SerializeField] private LoadingScreenController loadingScreenController;
        [SerializeField] private Button playButton;
        [SerializeField] private Button hostOnlineButton; // wired as dedicated quick join (Relay client only)
        [SerializeField] private Button joinOnlineButton;
        [SerializeField] private Button teamAButton;
        [SerializeField] private Button teamBButton;
        [SerializeField] private Button teamCButton;
        [SerializeField] private Button teamDButton;
        [SerializeField] private Button teamEButton;
        [SerializeField] private TextMeshProUGUI teamALabel;
        [SerializeField] private TextMeshProUGUI teamBLabel;
        [SerializeField] private TextMeshProUGUI teamCLabel;
        [SerializeField] private TextMeshProUGUI teamDLabel;
        [SerializeField] private TextMeshProUGUI teamELabel;
        [SerializeField] private TMP_InputField joinCodeInputField;
        [SerializeField] private TextMeshProUGUI joinCodeDisplayText;
        [SerializeField] private TMP_InputField serverAddressInput;
        [SerializeField] private TextMeshProUGUI playerCountText;
        [SerializeField] private TextMeshProUGUI teamStatusText;
        [SerializeField] private TextMeshProUGUI roomNameText;
        [SerializeField] private TMP_InputField playerNameInputField;
        [SerializeField] private Button browseLobbiesButton;
        [SerializeField] private Button refreshLobbiesButton;
        [SerializeField] private Button joinSelectedLobbyButton;
        [SerializeField] private Transform lobbyListContainer;
        [SerializeField] private GameObject lobbyListRowPrefab;
        [SerializeField] private TextMeshProUGUI lobbyBrowserStatusText;
        [SerializeField] private bool latestOnlyFilter = true;

        private readonly List<NetworkGameManager.LobbySummary> cachedLobbySummaries = new List<NetworkGameManager.LobbySummary>();
        private readonly List<Button> lobbyRowButtons = new List<Button>();
        private readonly List<Image> lobbyRowBackgrounds = new List<Image>();
        private string selectedLobbyId;
        private int selectedLobbyRowIndex = -1;
        private GameObject lobbyBrowserRoot;
        private GameObject lobbyScreenRoot;
        private RectTransform lobbyScreenBodyRect;
        private Button lobbyScreenBackButton;
        private Button mainMenuStoreButton;
        private GameObject storeScreenRoot;
        private RectTransform storeScreenListRoot;
        private Button storeScreenBackButton;
        private Button storeRestorePurchasesButton;
        private Vector2 _lastMainMenuPanelSize = Vector2.negativeInfinity;
        private string pendingTeamJoinError;
        /// <summary>Runtime-created control; hosts a Relay+Lobby match in the browser (not on GCE).</summary>
        private Button _webGlBrowserHostButton;
        /// <summary>When <see cref="ShowLobby"/> runs without a loading screen, team panel is shown only after Netcode is in a client/host session.</summary>
        private bool deferTeamPanelUntilNetworkReady;
        private float _dbgLastLobbyRefreshRealtime = -1f;
        private int _dbgLobbyRefreshCount;

        private RectTransform _authMainCardRt;
        private Image _authMainCardBg;
        private TextMeshProUGUI _authMainHeadline;
        private TextMeshProUGUI _authMainSubtitle;
        private Button _authPrimaryButton;
        private GameObject _signInPopupRoot;
        private Button _signInPopupUnityButton;
        private Button _signInPopupGoogleButton;
        private Button _signInPopupFacebookButton;
        private Button _signInPopupCancelButton;

        private void Start()
        {
            TitanOrbitServicesRuntimeBootstrap.EnsureHostIfNeeded();
            RemoveLegacyLobbyAuthRow();
            EnsureLobbyScreenUi();
            EnsureMainMenuCoreUi();
            EnsureMainMenuUnityAuthCard();
            EnsureSignInOptionsPopup();
            EnsureStoreScreenUi();

            if (hostOnlineButton != null)
                hostOnlineButton.onClick.AddListener(OnQuickJoinDedicatedClicked);

            if (joinOnlineButton != null)
            {
                joinOnlineButton.onClick.AddListener(OnJoinOnlineClicked);
            }

            WireLobbyBrowserListeners();

            NetworkGameManager.OnTeamChosen += OnTeamChosen;
            NetworkGameManager.OnTeamChoiceFailed += OnTeamChoiceFailed;

            if (teamAButton != null) teamAButton.onClick.AddListener(() => OnTeamClicked(Core.TeamManager.Team.TeamA));
            if (teamBButton != null) teamBButton.onClick.AddListener(() => OnTeamClicked(Core.TeamManager.Team.TeamB));
            if (teamCButton != null) teamCButton.onClick.AddListener(() => OnTeamClicked(Core.TeamManager.Team.TeamC));
            if (teamDButton != null) teamDButton.onClick.AddListener(() => OnTeamClicked(Core.TeamManager.Team.TeamD));
            if (teamEButton != null) teamEButton.onClick.AddListener(() => OnTeamClicked(Core.TeamManager.Team.TeamE));

            const string playerNameKey = "TitanOrbit_PlayerName";
            if (playerNameInputField != null)
            {
                playerNameInputField.text = PlayerPrefs.GetString(playerNameKey, "");
                playerNameInputField.onEndEdit.AddListener(s => { PlayerPrefs.SetString(playerNameKey, s ?? ""); PlayerPrefs.Save(); });
                playerNameInputField.textComponent.fontSize = 60;
                playerNameInputField.textComponent.alignment = TMPro.TextAlignmentOptions.Center;
                if (playerNameInputField.placeholder as TextMeshProUGUI != null)
                {
                    (playerNameInputField.placeholder as TextMeshProUGUI).fontSize = 36;
                    (playerNameInputField.placeholder as TextMeshProUGUI).alignment = TMPro.TextAlignmentOptions.Center;
                }
            }

            if (joinSelectedLobbyButton != null)
                joinSelectedLobbyButton.interactable = false;
            SetLobbyBrowserStatus("Select a lobby to join.");

            LayoutMainMenuActionStack();
            if (mainMenuPanel != null)
            {
                var pr = mainMenuPanel.GetComponent<RectTransform>();
                if (pr != null)
                    _lastMainMenuPanelSize = pr.rect.size;
            }
            _ = RefreshLobbyListAsync();
            _ = PrimeGuestSessionAndRefreshAuthUiAsync();
            UnityGameServicesBootstrap.AuthStateChanged += OnUnityAuthStateChanged;
            TitanOrbitEntitlements.RemoveAdsOwnershipChanged += OnRemoveAdsOwnershipChanged;
            _ = WaitForIapStoreAndRefreshUiAsync();
        }

        private void OnDestroy()
        {
            TitanOrbitEntitlements.RemoveAdsOwnershipChanged -= OnRemoveAdsOwnershipChanged;
            UnityGameServicesBootstrap.AuthStateChanged -= OnUnityAuthStateChanged;
            NetworkGameManager.OnTeamChosen -= OnTeamChosen;
            NetworkGameManager.OnTeamChoiceFailed -= OnTeamChoiceFailed;
        }

        private void OnUnityAuthStateChanged()
        {
            RefreshMainMenuUnityAuthCard();
        }

        private async System.Threading.Tasks.Task WaitForIapStoreAndRefreshUiAsync()
        {
            for (int i = 0; i < 120; i++)
            {
                var iap = UnityEngine.Object.FindFirstObjectByType<TitanOrbitIapManager>();
                if (iap != null && iap.IsStoreReady)
                {
                    RefreshStoreProductList();
                    return;
                }

                await System.Threading.Tasks.Task.Delay(250);
            }
        }

        private async System.Threading.Tasks.Task PrimeGuestSessionAndRefreshAuthUiAsync()
        {
            await UnityGameServicesBootstrap.EnsureGuestSessionForOnlineAsync();
            await UnityGameServicesBootstrap.TryFetchPlayerInfoForUiAsync();
            RefreshMainMenuUnityAuthCard();
            TitanOrbitGrowIntegration.LogUaFunnelEvent("ugs_session_ready",
                "{\"playerId\":\"" + (UnityGameServicesBootstrap.PlayerId ?? "") + "\"}");
        }

        private void RefreshMainMenuUnityAuthCard()
        {
            if (_authMainHeadline == null || _authMainSubtitle == null)
                return;

            if (UnityGameServicesBootstrap.UnityServicesNotReadyYet())
            {
                SetAuthCardColors(new Color(0.12f, 0.13f, 0.16f, 0.96f), new Color(0.55f, 0.58f, 0.62f, 1f));
                _authMainHeadline.text = "Connecting…";
                _authMainSubtitle.text = "";
                ApplyPrimaryAuthButton(signInMode: true, interactable: false);
                PositionMainMenuAuthTopRight();
                return;
            }

            if (!UnityGameServicesBootstrap.IsSignedIn || !UnityGameServicesBootstrap.IsAuthorizedSession())
            {
                SetAuthCardColors(new Color(0.18f, 0.12f, 0.12f, 0.96f), new Color(0.95f, 0.55f, 0.52f, 1f));
                _authMainHeadline.text = "Offline";
                _authMainSubtitle.text = "";
                ApplyPrimaryAuthButton(signInMode: true, interactable: false);
                PositionMainMenuAuthTopRight();
                return;
            }

            bool unity = UnityGameServicesBootstrap.IsUnityAccountActiveForUi();
            if (unity)
            {
                SetAuthCardColors(new Color(0.06f, 0.22f, 0.14f, 0.98f), new Color(0.55f, 0.95f, 0.72f, 1f));
                _authMainHeadline.text = "Signed in";
                _authMainSubtitle.text = UnityGameServicesBootstrap.GetDisplayPlayerId();
                ApplyPrimaryAuthButton(signInMode: false, interactable: true);
            }
            else
            {
                SetAuthCardColors(new Color(0.08f, 0.12f, 0.2f, 0.98f), new Color(0.78f, 0.88f, 0.98f, 1f));
                _authMainHeadline.text = "Not signed in";
                _authMainSubtitle.text = "";
                ApplyPrimaryAuthButton(signInMode: true, interactable: true);
            }

            PositionMainMenuAuthTopRight();
        }

        private void SetAuthCardColors(Color bg, Color accentLine)
        {
            if (_authMainCardBg != null)
                _authMainCardBg.color = bg;
            if (_authMainHeadline != null)
                _authMainHeadline.color = accentLine;
        }

        /// <param name="signInMode">True = label "Sign In", false = "Sign Out".</param>
        private void ApplyPrimaryAuthButton(bool signInMode, bool interactable)
        {
            if (_authPrimaryButton == null)
                return;
            _authPrimaryButton.gameObject.SetActive(true);
            _authPrimaryButton.interactable = interactable;
            var t = _authPrimaryButton.GetComponentInChildren<TextMeshProUGUI>();
            if (t != null)
                t.text = signInMode ? "Sign In" : "Sign Out";
        }

        private void OnAuthPrimaryButtonClicked()
        {
            if (UnityGameServicesBootstrap.IsUnityAccountActiveForUi() && UnityGameServicesBootstrap.IsAuthorizedSession())
            {
                _ = OnAuthSignOutClickedAsync();
                return;
            }

            if (!UnityGameServicesBootstrap.IsAuthorizedSession())
                return;

            OpenSignInOptionsPopup();
        }

        private async System.Threading.Tasks.Task OnAuthSignOutClickedAsync()
        {
            if (_authPrimaryButton != null) _authPrimaryButton.interactable = false;
            try
            {
                CloseSignInOptionsPopup();
                UnityGameServicesBootstrap.SignOutAllSessions();
                await UnityGameServicesBootstrap.EnsureGuestSessionForOnlineAsync();
                await UnityGameServicesBootstrap.TryFetchPlayerInfoForUiAsync();
            }
            finally
            {
                if (_authPrimaryButton != null) _authPrimaryButton.interactable = true;
                RefreshMainMenuUnityAuthCard();
                await RefreshLobbyListAsync();
            }
        }

        private void OpenSignInOptionsPopup()
        {
            EnsureSignInOptionsPopup();
            if (_signInPopupRoot != null)
            {
                _signInPopupRoot.SetActive(true);
                _signInPopupRoot.transform.SetAsLastSibling();
            }
        }

        private void CloseSignInOptionsPopup()
        {
            if (_signInPopupRoot != null)
                _signInPopupRoot.SetActive(false);
        }

        private async void OnSignInPopupUnityAccountClicked()
        {
            if (_signInPopupUnityButton != null) _signInPopupUnityButton.interactable = false;
            try
            {
                await UnityGameServicesBootstrap.SignInOrLinkUnityPlayerAccountUsingBrowserAsync();
            }
            finally
            {
                if (_signInPopupUnityButton != null) _signInPopupUnityButton.interactable = true;
                CloseSignInOptionsPopup();
                RefreshMainMenuUnityAuthCard();
                await RefreshLobbyListAsync();
            }
        }

        private void EnsureSignInOptionsPopup()
        {
            if (mainMenuPanel == null || _signInPopupRoot != null)
                return;

            var canvas = mainMenuPanel.GetComponentInParent<Canvas>();
            Transform host = canvas != null ? canvas.transform : mainMenuPanel.transform;

            _signInPopupRoot = new GameObject("TitanOrbitSignInPopup", typeof(RectTransform));
            _signInPopupRoot.transform.SetParent(host, false);
            var rootRt = _signInPopupRoot.GetComponent<RectTransform>();
            rootRt.anchorMin = Vector2.zero;
            rootRt.anchorMax = Vector2.one;
            rootRt.offsetMin = Vector2.zero;
            rootRt.offsetMax = Vector2.zero;

            var dim = new GameObject("Dim", typeof(RectTransform), typeof(Image), typeof(Button));
            dim.transform.SetParent(_signInPopupRoot.transform, false);
            var dimRt = dim.GetComponent<RectTransform>();
            dimRt.anchorMin = Vector2.zero;
            dimRt.anchorMax = Vector2.one;
            dimRt.offsetMin = Vector2.zero;
            dimRt.offsetMax = Vector2.zero;
            var dimImg = dim.GetComponent<Image>();
            dimImg.color = new Color(0f, 0f, 0f, 0.55f);
            dimImg.raycastTarget = true;
            var dimBtn = dim.GetComponent<Button>();
            dimBtn.targetGraphic = dimImg;
            dimBtn.onClick.AddListener(CloseSignInOptionsPopup);

            var panel = new GameObject("Panel", typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup));
            panel.transform.SetParent(_signInPopupRoot.transform, false);
            var panelRt = panel.GetComponent<RectTransform>();
            panelRt.anchorMin = panelRt.anchorMax = new Vector2(0.5f, 0.5f);
            panelRt.pivot = new Vector2(0.5f, 0.5f);
            panelRt.sizeDelta = new Vector2(440f, 320f);
            panelRt.anchoredPosition = Vector2.zero;
            var panelImg = panel.GetComponent<Image>();
            panelImg.color = new Color(0.06f, 0.09f, 0.14f, 0.98f);
            panelImg.raycastTarget = true;
            var v = panel.GetComponent<VerticalLayoutGroup>();
            v.padding = new RectOffset(24, 24, 22, 20);
            v.spacing = 12f;
            v.childAlignment = TextAnchor.UpperCenter;
            v.childControlWidth = true;
            v.childControlHeight = true;
            v.childForceExpandWidth = true;
            v.childForceExpandHeight = false;

            var titleGo = CreateLabel("SignInTitle", "Sign in", Vector2.zero, 28f, panel.transform, raycastTarget: false);
            var titleTmp = titleGo.GetComponent<TextMeshProUGUI>();
            titleTmp.fontStyle = FontStyles.Bold;
            var titleLe = titleGo.AddComponent<LayoutElement>();
            titleLe.minHeight = 36f;
            titleLe.preferredHeight = 38f;

            _signInPopupUnityButton = CreateMenuButton("PopupUnity", "Unity account", Vector2.zero, new Vector2(360f, 48f), panel.GetComponent<RectTransform>(), isPrimary: true);
            _signInPopupUnityButton.onClick.AddListener(OnSignInPopupUnityAccountClicked);

            _signInPopupGoogleButton = CreateMenuButton("PopupGoogle", "Google Play", Vector2.zero, new Vector2(360f, 44f), panel.GetComponent<RectTransform>(), isPrimary: false);
            _signInPopupGoogleButton.interactable = false;
            var gLabel = _signInPopupGoogleButton.GetComponentInChildren<TextMeshProUGUI>();
            if (gLabel != null)
            {
                gLabel.text = "Google Play (soon)";
                gLabel.color = new Color(0.55f, 0.58f, 0.62f, 1f);
            }

            _signInPopupFacebookButton = CreateMenuButton("PopupFacebook", "Facebook", Vector2.zero, new Vector2(360f, 44f), panel.GetComponent<RectTransform>(), isPrimary: false);
            _signInPopupFacebookButton.interactable = false;
            var fLabel = _signInPopupFacebookButton.GetComponentInChildren<TextMeshProUGUI>();
            if (fLabel != null)
            {
                fLabel.text = "Facebook (soon)";
                fLabel.color = new Color(0.55f, 0.58f, 0.62f, 1f);
            }

            _signInPopupCancelButton = CreateMenuButton("PopupCancel", "Cancel", Vector2.zero, new Vector2(360f, 44f), panel.GetComponent<RectTransform>(), isPrimary: false);
            _signInPopupCancelButton.onClick.AddListener(CloseSignInOptionsPopup);

            _signInPopupRoot.SetActive(false);
        }

        private void RemoveLegacyLobbyAuthRow()
        {
            if (lobbyBrowserRoot == null)
                return;
            var legacy = lobbyBrowserRoot.transform.Find("TitanOrbitAuthRow");
            if (legacy != null)
                Destroy(legacy.gameObject);
        }

        /// <summary>Account status + single Sign In / Sign Out on the main menu (not inside the lobby list).</summary>
        private void EnsureMainMenuUnityAuthCard()
        {
            if (mainMenuPanel == null)
                return;

            var existing = mainMenuPanel.transform.Find("TitanOrbitAuthMainCard");
            if (existing != null)
            {
                _authMainCardRt = existing.GetComponent<RectTransform>();
                _authMainCardBg = existing.GetComponent<Image>();
                _authMainHeadline = existing.Find("Headline")?.GetComponent<TextMeshProUGUI>();
                _authMainSubtitle = existing.Find("Subtitle")?.GetComponent<TextMeshProUGUI>();
                var existingAuthButtons = existing.Find("AuthButtons");
                if (existingAuthButtons != null)
                {
                    if (existingAuthButtons.Find("PrimaryAuth") == null)
                    {
                        for (int i = existingAuthButtons.childCount - 1; i >= 0; i--)
                            Destroy(existingAuthButtons.GetChild(i).gameObject);
                        _authPrimaryButton = CreateMenuButton("PrimaryAuth", "Sign In", Vector2.zero, new Vector2(280f, 44f), existingAuthButtons.GetComponent<RectTransform>(), isPrimary: true);
                    }
                    else
                    {
                        _authPrimaryButton = existingAuthButtons.Find("PrimaryAuth")?.GetComponent<Button>();
                    }
                }
                WirePrimaryAuthButton();
                RefreshMainMenuUnityAuthCard();
                PositionMainMenuAuthTopRight();
                return;
            }

            var card = new GameObject("TitanOrbitAuthMainCard", typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup));
            card.transform.SetParent(mainMenuPanel.transform, false);
            _authMainCardRt = card.GetComponent<RectTransform>();
            _authMainCardBg = card.GetComponent<Image>();
            _authMainCardBg.raycastTarget = true;
            _authMainCardBg.color = new Color(0.08f, 0.12f, 0.2f, 0.98f);

            var v = card.GetComponent<VerticalLayoutGroup>();
            v.spacing = 8f;
            v.padding = new RectOffset(18, 18, 12, 12);
            v.childAlignment = TextAnchor.UpperCenter;
            v.childControlWidth = true;
            v.childControlHeight = true;
            v.childForceExpandWidth = true;
            v.childForceExpandHeight = false;

            const float authInnerTextW = 320f;

            var hl = CreateLabel("Headline", "Connecting…", Vector2.zero, 22f, card.transform, raycastTarget: false);
            var hlRt = hl.GetComponent<RectTransform>();
            hlRt.sizeDelta = new Vector2(authInnerTextW, 28f);
            _authMainHeadline = hl.GetComponent<TextMeshProUGUI>();
            _authMainHeadline.fontStyle = FontStyles.Bold;
            _authMainHeadline.enableWordWrapping = true;
            _authMainHeadline.alignment = TextAlignmentOptions.Center;
            var hlLe = hl.AddComponent<LayoutElement>();
            hlLe.minHeight = 26f;
            hlLe.preferredHeight = 28f;

            var sub = CreateLabel("Subtitle", "", Vector2.zero, 16f, card.transform, raycastTarget: false);
            var subRt = sub.GetComponent<RectTransform>();
            subRt.sizeDelta = new Vector2(authInnerTextW, 22f);
            _authMainSubtitle = sub.GetComponent<TextMeshProUGUI>();
            _authMainSubtitle.enableWordWrapping = true;
            _authMainSubtitle.alignment = TextAlignmentOptions.Center;
            _authMainSubtitle.color = new Color(0.82f, 0.9f, 0.98f, 0.92f);
            var subLe = sub.AddComponent<LayoutElement>();
            subLe.minHeight = 18f;
            subLe.preferredHeight = 22f;

            var authButtonsRow = new GameObject("AuthButtons", typeof(RectTransform), typeof(HorizontalLayoutGroup));
            authButtonsRow.transform.SetParent(card.transform, false);
            var h = authButtonsRow.GetComponent<HorizontalLayoutGroup>();
            h.spacing = 10f;
            h.padding = new RectOffset(4, 4, 4, 0);
            h.childAlignment = TextAnchor.MiddleCenter;
            h.childControlHeight = true;
            h.childControlWidth = true;
            h.childForceExpandWidth = true;
            h.childForceExpandHeight = false;
            var authButtonsRowLe = authButtonsRow.AddComponent<LayoutElement>();
            authButtonsRowLe.minHeight = 44f;
            authButtonsRowLe.preferredHeight = 48f;

            var authButtonsRowRect = authButtonsRow.GetComponent<RectTransform>();
            authButtonsRowRect.anchorMin = new Vector2(0f, 1f);
            authButtonsRowRect.anchorMax = new Vector2(1f, 1f);
            authButtonsRowRect.pivot = new Vector2(0.5f, 1f);
            authButtonsRowRect.sizeDelta = Vector2.zero;

            _authPrimaryButton = CreateMenuButton("PrimaryAuth", "Sign In", Vector2.zero, new Vector2(280f, 44f), authButtonsRow.GetComponent<RectTransform>(), isPrimary: true);

            WirePrimaryAuthButton();
            RefreshMainMenuUnityAuthCard();
            PositionMainMenuAuthTopRight();
        }

        /// <summary>Pins the Unity auth card to the top-right of the main menu panel (above other menu widgets).</summary>
        private void PositionMainMenuAuthTopRight()
        {
            if (_authMainCardRt == null && mainMenuPanel != null)
            {
                var t = mainMenuPanel.transform.Find("TitanOrbitAuthMainCard");
                if (t != null)
                    _authMainCardRt = t.GetComponent<RectTransform>();
            }
            if (_authMainCardRt == null || mainMenuPanel == null)
                return;

            if (_authMainCardRt.transform.parent != mainMenuPanel.transform)
                _authMainCardRt.SetParent(mainMenuPanel.transform, false);

            var pr = mainMenuPanel.GetComponent<RectTransform>();
            float refW = pr != null && pr.rect.width > 1f ? pr.rect.width : 1920f;

            const float padX = 20f;
            const float padY = 20f;
            float cardW = Mathf.Clamp(refW * 0.26f, 260f, 380f);
            float cardH = 128f;

            _authMainCardRt.anchorMin = _authMainCardRt.anchorMax = new Vector2(1f, 1f);
            _authMainCardRt.pivot = new Vector2(1f, 1f);
            _authMainCardRt.sizeDelta = new Vector2(cardW, cardH);
            _authMainCardRt.anchoredPosition = new Vector2(-padX, -padY);
            _authMainCardRt.SetAsLastSibling();
        }

        private void WirePrimaryAuthButton()
        {
            if (_authPrimaryButton == null)
                return;
            _authPrimaryButton.onClick.RemoveListener(OnAuthPrimaryButtonClicked);
            _authPrimaryButton.onClick.AddListener(OnAuthPrimaryButtonClicked);
        }

        private void OnRemoveAdsOwnershipChanged()
        {
            if (storeScreenRoot != null && storeScreenRoot.activeSelf)
                RefreshStoreProductList();
        }

        private void OnRestorePurchasesClicked()
        {
            var iap = UnityEngine.Object.FindFirstObjectByType<TitanOrbitIapManager>();
            if (iap == null)
            {
                Debug.LogWarning("[MainMenu] Restore purchases: no TitanOrbitIapManager in scene.");
                return;
            }

            iap.RestorePurchases((success, message) =>
            {
                if (!success && !string.IsNullOrEmpty(message))
                    Debug.LogWarning("[MainMenu] Restore purchases: " + message);
                RefreshStoreProductList();
            });
        }

        private void OnStorePurchaseProductClicked(string productId)
        {
            var iap = UnityEngine.Object.FindFirstObjectByType<TitanOrbitIapManager>();
            if (iap == null || string.IsNullOrWhiteSpace(productId))
                return;
            if (!iap.CanInitiatePurchase(productId))
                return;
            iap.InitiatePurchase(productId.Trim());
        }

        private void OnTeamChoiceFailed(string message)
        {
            pendingTeamJoinError = message ?? "";
            Debug.LogWarning("[MainMenu] " + message);
        }

        private void OnTeamChosen(Core.TeamManager.Team team)
        {
            pendingTeamJoinError = null;
            deferTeamPanelUntilNetworkReady = false;
            if (lobbyPanel != null) lobbyPanel.SetActive(false);
            if (teamSelectionPanel != null) teamSelectionPanel.SetActive(false);
        }

        private void LateUpdate()
        {
            if (mainMenuPanel == null || !mainMenuPanel.activeSelf)
                return;
            var pr = mainMenuPanel.GetComponent<RectTransform>();
            if (pr == null)
                return;
            Vector2 sz = pr.rect.size;
            if (sz.x > 1f && sz.y > 1f && (sz - _lastMainMenuPanelSize).sqrMagnitude > 4f)
            {
                _lastMainMenuPanelSize = sz;
                LayoutMainMenuActionStack();
            }
        }

        private void Update()
        {
            if (lobbyPanel != null && lobbyPanel.activeSelf)
            {
                if (deferTeamPanelUntilNetworkReady && teamSelectionPanel != null)
                {
                    var nm = NetworkManager.Singleton;
                    if (nm != null && (nm.IsClient || nm.IsServer))
                    {
                        teamSelectionPanel.SetActive(true);
                        deferTeamPanelUntilNetworkReady = false;
                    }
                }
                UpdateLobbyInfo();
                if (teamSelectionPanel != null && teamSelectionPanel.GetComponent<TeamSelectionUI>() != null)
                    ; // TeamSelectionUI refreshes itself
                else
                    RefreshTeamSelectionUI();
            }
        }

        private void RefreshTeamSelectionUI()
        {
            if (Core.TeamManager.Instance == null) return;
            int max = Core.TeamManager.Instance.MaxPlayersPerTeam;
            int active = Core.TeamManager.Instance.GetEffectiveTeamCountForUI();
            int a = Core.TeamManager.Instance.TeamACount;
            int b = Core.TeamManager.Instance.TeamBCount;
            int c = Core.TeamManager.Instance.TeamCCount;
            int d = Core.TeamManager.Instance.TeamDCount;
            int e = Core.TeamManager.Instance.TeamECount;

            int minCount = int.MaxValue;
            for (int i = 0; i < active; i++)
            {
                Core.TeamManager.Team t = (Core.TeamManager.Team)(i + 1);
                minCount = Mathf.Min(minCount, Core.TeamManager.Instance.GetTeamPlayerCount(t));
            }

            ulong localId = NetworkManager.Singleton != null ? NetworkManager.Singleton.LocalClientId : 0;
            bool localHasNoTeam = Core.TeamManager.Instance.GetPlayerTeam(localId) == Core.TeamManager.Team.None;

            void Row(int ord, TextMeshProUGUI label, Button btn, int count)
            {
                bool inMatch = ord <= active;
                if (label != null)
                {
                    label.gameObject.SetActive(inMatch);
                    if (inMatch) label.text = $"Team {(char)('A' + ord - 1)} ({count}/{max})";
                }
                if (btn != null)
                {
                    btn.gameObject.SetActive(inMatch);
                    if (inMatch) btn.interactable = count < max && (localHasNoTeam || count <= minCount + 1);
                }
            }

            Row(1, teamALabel, teamAButton, a);
            Row(2, teamBLabel, teamBButton, b);
            Row(3, teamCLabel, teamCButton, c);
            Row(4, teamDLabel, teamDButton, d);
            Row(5, teamELabel, teamEButton, e);
        }

        private void OnTeamClicked(Core.TeamManager.Team team)
        {
            NetworkGameManager.RequestTeamFromLocalPlayer(team);
        }

        private async void OnQuickJoinDedicatedClicked()
        {
            if (NetworkGameManager.Instance == null) return;
            if (hostOnlineButton != null) hostOnlineButton.interactable = false;
            try
            {
                string pname = playerNameInputField != null ? (playerNameInputField.text ?? "").Trim() : "";
                if (!string.IsNullOrEmpty(pname))
                {
                    PlayerPrefs.SetString("TitanOrbit_PlayerName", pname);
                    PlayerPrefs.Save();
                }
                NetworkGameManager.LocalPlayerDisplayName = string.IsNullOrEmpty(pname)
                    ? TitanOrbit.Data.GameNames.GetRandomPlayerName()
                    : pname;

                bool ok = await NetworkGameManager.Instance.PlayWebGLJoinAsync();
                if (ok)
                {
                    if (joinCodeDisplayText != null)
                    {
                        joinCodeDisplayText.gameObject.SetActive(true);
                        joinCodeDisplayText.text = "Joined a dedicated match.\nPick a team when the match screen appears.";
                    }
                    ShowLobby();
                }
                else
                {
                    if (joinCodeDisplayText != null)
                    {
                        joinCodeDisplayText.gameObject.SetActive(true);
                        joinCodeDisplayText.text = "No open dedicated lobby found. Ensure the headless server is running on Google Cloud, then refresh Open matches.";
                    }
                    Debug.LogError(
                        "Quick join failed: no matching lobby or Relay join failed. " +
                        "Confirm the Linux headless service is running and Player.log shows a lobby created; " +
                        "use \"Host match (browser)\" for a temporary player-hosted room, or pick a row under Open matches.");
                }
            }
            finally
            {
                if (hostOnlineButton != null) hostOnlineButton.interactable = true;
            }
        }

        private async void OnWebGlBrowserHostRelayClicked()
        {
            if (NetworkGameManager.Instance == null)
                return;
            if (_webGlBrowserHostButton != null)
                _webGlBrowserHostButton.interactable = false;
            try
            {
                string pname = playerNameInputField != null ? (playerNameInputField.text ?? "").Trim() : "";
                if (!string.IsNullOrEmpty(pname))
                {
                    PlayerPrefs.SetString("TitanOrbit_PlayerName", pname);
                    PlayerPrefs.Save();
                }

                NetworkGameManager.LocalPlayerDisplayName = string.IsNullOrEmpty(pname)
                    ? TitanOrbit.Data.GameNames.GetRandomPlayerName()
                    : pname;

                bool ok = await NetworkGameManager.Instance.PlayWebGLHostRelayMatchAsync();
                if (ok)
                {
                    if (joinCodeDisplayText != null)
                    {
                        joinCodeDisplayText.gameObject.SetActive(true);
                        joinCodeDisplayText.text =
                            "Hosting from this browser (Relay). Other players can Quick join or pick this room under Open matches.\n" +
                            "This does not run on your Google Cloud VM.";
                    }

                    ShowLobby();
                }
                else
                {
                    Debug.LogError(
                        "Browser host failed. Check Unity Services (same project as the build) and the Unity console for Relay/Lobby errors.");
                }
            }
            finally
            {
                if (_webGlBrowserHostButton != null)
                    _webGlBrowserHostButton.interactable = true;
            }
        }

        private async void OnJoinOnlineClicked()
        {
            if (NetworkGameManager.Instance == null) return;
            string code = joinCodeInputField != null ? joinCodeInputField.text : (serverAddressInput != null ? serverAddressInput.text : null);
            if (string.IsNullOrWhiteSpace(code))
            {
                Debug.LogWarning("Enter a join code first (or use the server address field).");
                return;
            }
            if (joinOnlineButton != null) joinOnlineButton.interactable = false;
            try
            {
                bool ok = await NetworkGameManager.Instance.StartClientWithRelayAsync(code);
                if (ok)
                    ShowLobby();
                else
                    Debug.LogError("Failed to join with Relay. Check the join code and connection.");
            }
            finally
            {
                if (joinOnlineButton != null) joinOnlineButton.interactable = true;
            }
        }

        private async void OnRefreshLobbiesClicked()
        {
            await RefreshLobbyListAsync();
        }

        private async void OnJoinSelectedLobbyClicked()
        {
            if (NetworkGameManager.Instance == null)
                return;
            if (string.IsNullOrWhiteSpace(selectedLobbyId))
            {
                SetLobbyBrowserStatus("Select a lobby first.");
                return;
            }

            if (joinSelectedLobbyButton != null)
                joinSelectedLobbyButton.interactable = false;

            try
            {
                SetLobbyBrowserStatus("Joining selected lobby...");
                string pname = playerNameInputField != null ? (playerNameInputField.text ?? "").Trim() : "";
                if (!string.IsNullOrEmpty(pname))
                {
                    PlayerPrefs.SetString("TitanOrbit_PlayerName", pname);
                    PlayerPrefs.Save();
                }
                NetworkGameManager.LocalPlayerDisplayName = string.IsNullOrEmpty(pname)
                    ? TitanOrbit.Data.GameNames.GetRandomPlayerName()
                    : pname;

                bool ok = await NetworkGameManager.Instance.JoinLobbyByIdAsync(selectedLobbyId);
                if (ok)
                {
                    SetLobbyBrowserStatus("Connected.");
                    ShowLobby();
                }
                else
                {
                    SetLobbyBrowserStatus("Join failed — that match may have ended. Refreshing the list…");
                    selectedLobbyId = null;
                    selectedLobbyRowIndex = -1;
                    await RefreshLobbyListAsync();
                }
            }
            finally
            {
                if (joinSelectedLobbyButton != null)
                    joinSelectedLobbyButton.interactable = !string.IsNullOrWhiteSpace(selectedLobbyId);
            }
        }

        private async Task RefreshLobbyListAsync()
        {
            if (NetworkGameManager.Instance == null)
            {
                SetLobbyBrowserStatus("Network not ready yet. Open this screen again or tap Refresh.");
                return;
            }

            _dbgLobbyRefreshCount++;
            float now = Time.realtimeSinceStartup;
            float delta = _dbgLastLobbyRefreshRealtime < 0f ? -1f : (now - _dbgLastLobbyRefreshRealtime) * 1000f;
            _dbgLastLobbyRefreshRealtime = now;

            SetLobbyBrowserStatus("Loading lobbies...");
            if (refreshLobbiesButton != null)
                refreshLobbiesButton.interactable = false;
            if (joinSelectedLobbyButton != null)
                joinSelectedLobbyButton.interactable = false;

            try
            {
                var fetched = await NetworkGameManager.Instance.QueryOpenLobbiesAsync(latestOnlyFilter, 40);
                var kind = NetworkGameManager.LastOpenLobbyQueryKind;
                // If strict/latest query returns empty while services are otherwise OK, immediately retry with latestOnly=false.
                // This avoids transient "no games" states when latest/index flags lag behind lobby creation.
                if (fetched.Count == 0 &&
                    latestOnlyFilter &&
                    kind == NetworkGameManager.OpenLobbyQueryResultKind.Ok)
                {
                    var retry = await NetworkGameManager.Instance.QueryOpenLobbiesAsync(false, 40);
                    var retryKind = NetworkGameManager.LastOpenLobbyQueryKind;
                    if (retryKind == NetworkGameManager.OpenLobbyQueryResultKind.Ok && retry.Count > 0)
                    {
                        fetched = NetworkGameManager.FilterToJoinableDedicatedLobbies(retry);
                        kind = retryKind;
                    }
                }

                fetched = NetworkGameManager.FilterToJoinableDedicatedLobbies(fetched);

                if (fetched.Count > 0)
                {
                    selectedLobbyId = null;
                    selectedLobbyRowIndex = -1;
                    cachedLobbySummaries.Clear();
                    cachedLobbySummaries.AddRange(fetched);
                    RenderLobbyList();
                    return;
                }

                if (kind == NetworkGameManager.OpenLobbyQueryResultKind.RateLimitBackoff)
                {
                    int waitSec = Mathf.Max(1, Mathf.CeilToInt(NetworkGameManager.LobbyRateLimitRemainingSeconds));
                    SetLobbyBrowserStatus(
                        "Lobby list is temporarily rate-limited by Unity. " +
                        (cachedLobbySummaries.Count > 0
                            ? $"Showing the previous list. Retry in about {waitSec}s."
                            : $"Wait about {waitSec}s, then tap Refresh."));
                    if (cachedLobbySummaries.Count > 0)
                        RenderLobbyList();
                    else
                        ClearLobbyListRows();
                    return;
                }

                if (kind == NetworkGameManager.OpenLobbyQueryResultKind.UnityServicesNotReady)
                {
                    SetLobbyBrowserStatus("Connecting to multiplayer services… try Refresh in a few seconds.");
                    if (cachedLobbySummaries.Count > 0)
                        RenderLobbyList();
                    else
                        ClearLobbyListRows();
                    return;
                }

                if (kind == NetworkGameManager.OpenLobbyQueryResultKind.Error)
                {
                    // Keep previous lobby list visible on transient query errors.
                    if (cachedLobbySummaries.Count > 0)
                    {
                        SetLobbyBrowserStatus("Lobby refresh failed. Showing previous list.");
                        RenderLobbyList();
                        return;
                    }

                    selectedLobbyId = null;
                    selectedLobbyRowIndex = -1;
                    cachedLobbySummaries.Clear();
                    ClearLobbyListRows();
                    if (!string.IsNullOrEmpty(NetworkGameManager.LastOpenLobbyQueryErrorDetail))
                    {
                        string detail = NetworkGameManager.LastOpenLobbyQueryErrorDetail;
                        if (detail.Length > 96)
                            detail = detail.Substring(0, 93) + "…";
                        SetLobbyBrowserStatus("Could not load lobbies: " + detail);
                    }
                    else
                        SetLobbyBrowserStatus("Could not load lobbies. Check your connection and tap Refresh.");
                    return;
                }

                selectedLobbyId = null;
                selectedLobbyRowIndex = -1;
                cachedLobbySummaries.Clear();
                cachedLobbySummaries.AddRange(fetched);
                RenderLobbyList();
            }
            finally
            {
                if (refreshLobbiesButton != null)
                    refreshLobbiesButton.interactable = true;
            }
        }

        private void RenderLobbyList()
        {
            ClearLobbyListRows();

            if (cachedLobbySummaries.Count == 0)
            {
                SetLobbyBrowserStatus("No open lobbies found.");
                return;
            }

            for (int i = 0; i < cachedLobbySummaries.Count; i++)
            {
                var summary = cachedLobbySummaries[i];
                if (lobbyListRowPrefab == null || lobbyListContainer == null)
                    continue;

                GameObject row = Instantiate(lobbyListRowPrefab, lobbyListContainer);
                row.SetActive(true);
                var button = row.GetComponent<Button>();
                var label = row.GetComponentInChildren<TextMeshProUGUI>();
                if (label != null)
                {
                    string latestTag = summary.IsLatest ? "  ·  Latest" : "";
                    label.text = $"<b>{summary.Name}</b>{latestTag}\n<size=85%><color=#9ec4e8>{summary.CurrentPlayers} / {summary.MaxPlayers} players</color></size>";
                }
                if (button != null)
                {
                    int capturedIndex = i;
                    button.onClick.AddListener(() => OnLobbyRowSelected(capturedIndex));
                    lobbyRowButtons.Add(button);
                    lobbyRowBackgrounds.Add(button.GetComponent<Image>());
                }
            }

            SetLobbyBrowserStatus("Select a lobby to join.");
        }

        private void OnLobbyRowSelected(int index)
        {
            if (index < 0 || index >= cachedLobbySummaries.Count)
                return;
            selectedLobbyId = cachedLobbySummaries[index].LobbyId;
            selectedLobbyRowIndex = index;
            if (joinSelectedLobbyButton != null)
                joinSelectedLobbyButton.interactable = true;
            SetLobbyBrowserStatus($"Selected: {cachedLobbySummaries[index].Name}");
            ApplyLobbyRowSelectionVisuals();
        }

        private void ApplyLobbyRowSelectionVisuals()
        {
            Color normal = new Color(0.11f, 0.17f, 0.28f, 0.98f);
            Color selected = new Color(0.18f, 0.38f, 0.62f, 0.98f);
            for (int i = 0; i < lobbyRowBackgrounds.Count; i++)
            {
                if (lobbyRowBackgrounds[i] == null)
                    continue;
                lobbyRowBackgrounds[i].color = i == selectedLobbyRowIndex ? selected : normal;
            }
        }

        private void ClearLobbyListRows()
        {
            lobbyRowButtons.Clear();
            lobbyRowBackgrounds.Clear();
            if (lobbyListContainer == null)
                return;
            for (int i = lobbyListContainer.childCount - 1; i >= 0; i--)
            {
                var child = lobbyListContainer.GetChild(i);
                if (child == null)
                    continue;
                Destroy(child.gameObject);
            }
        }

        private void SetLobbyBrowserStatus(string text)
        {
            if (lobbyBrowserStatusText != null)
                lobbyBrowserStatusText.text = text;
        }

        private void EnsureMainMenuCoreUi()
        {
            if (mainMenuPanel == null)
                return;

            var mainRect = mainMenuPanel.GetComponent<RectTransform>();
            if (mainRect == null)
                return;

            EnsureEventSystemExists();

            if (browseLobbiesButton != null)
                browseLobbiesButton.gameObject.SetActive(false);

            if (playButton == null)
                playButton = CreateMenuButton("MainPlayButton", "Play", new Vector2(0f, -48f), new Vector2(320f, 52f), mainRect, isPrimary: true);
            if (playButton != null)
                playButton.transform.SetParent(mainMenuPanel.transform, false);

            var playLabel = playButton != null ? playButton.GetComponentInChildren<TextMeshProUGUI>() : null;
            if (playLabel != null)
                playLabel.text = "Play";
            if (playButton != null)
            {
                playButton.onClick.RemoveListener(ShowLobbyScreen);
                playButton.onClick.AddListener(ShowLobbyScreen);
            }

            if (mainMenuStoreButton == null)
            {
                mainMenuStoreButton = CreateMenuButton("MainStoreButton", "Store", new Vector2(0f, -48f), new Vector2(320f, 48f), mainRect, isPrimary: false);
                var storeLabel = mainMenuStoreButton.GetComponentInChildren<TextMeshProUGUI>();
                if (storeLabel != null)
                    storeLabel.text = "Store";
            }
            if (mainMenuStoreButton != null)
            {
                mainMenuStoreButton.transform.SetParent(mainMenuPanel.transform, false);
                mainMenuStoreButton.onClick.RemoveListener(ShowStoreScreen);
                mainMenuStoreButton.onClick.AddListener(ShowStoreScreen);
            }
        }

        private void EnsureLobbyScreenUi()
        {
            if (mainMenuPanel == null || lobbyScreenRoot != null)
                return;

            var canvas = mainMenuPanel.GetComponentInParent<Canvas>();
            Transform canvasTr = canvas != null ? canvas.transform : mainMenuPanel.transform;

            lobbyScreenRoot = new GameObject("TitanOrbitLobbyScreen", typeof(RectTransform), typeof(Image));
            lobbyScreenRoot.transform.SetParent(canvasTr, false);
            var screenRt = lobbyScreenRoot.GetComponent<RectTransform>();
            screenRt.anchorMin = Vector2.zero;
            screenRt.anchorMax = Vector2.one;
            screenRt.offsetMin = Vector2.zero;
            screenRt.offsetMax = Vector2.zero;
            lobbyScreenRoot.GetComponent<Image>().color = new Color(0.02f, 0.04f, 0.08f, 1f);

            var topBar = new GameObject("LobbyTopBar", typeof(RectTransform), typeof(HorizontalLayoutGroup));
            topBar.transform.SetParent(lobbyScreenRoot.transform, false);
            var topRt = topBar.GetComponent<RectTransform>();
            topRt.anchorMin = new Vector2(0f, 1f);
            topRt.anchorMax = new Vector2(1f, 1f);
            topRt.pivot = new Vector2(0.5f, 1f);
            topRt.sizeDelta = new Vector2(0f, 56f);
            topRt.anchoredPosition = Vector2.zero;
            var topH = topBar.GetComponent<HorizontalLayoutGroup>();
            topH.padding = new RectOffset(16, 16, 8, 8);
            topH.spacing = 16f;
            topH.childAlignment = TextAnchor.MiddleLeft;
            topH.childControlHeight = true;
            topH.childControlWidth = false;
            topH.childForceExpandHeight = false;
            topH.childForceExpandWidth = false;

            lobbyScreenBackButton = CreateMenuButton("LobbyBackButton", "Back", Vector2.zero, new Vector2(120f, 44f), topBar.GetComponent<RectTransform>(), isPrimary: false);
            lobbyScreenBackButton.onClick.AddListener(HideLobbyScreen);

            var titleGo = CreateLabel("LobbyScreenTitle", "Online", Vector2.zero, 26f, topBar.transform, raycastTarget: false);
            var titleLe = titleGo.AddComponent<LayoutElement>();
            titleLe.flexibleWidth = 1f;
            titleLe.minWidth = 80f;

            var body = new GameObject("LobbyBody", typeof(RectTransform), typeof(VerticalLayoutGroup));
            body.transform.SetParent(lobbyScreenRoot.transform, false);
            lobbyScreenBodyRect = body.GetComponent<RectTransform>();
            lobbyScreenBodyRect.anchorMin = Vector2.zero;
            lobbyScreenBodyRect.anchorMax = Vector2.one;
            lobbyScreenBodyRect.offsetMin = new Vector2(24f, 24f);
            lobbyScreenBodyRect.offsetMax = new Vector2(-24f, -64f);
            var bodyV = body.GetComponent<VerticalLayoutGroup>();
            bodyV.spacing = 14f;
            bodyV.padding = new RectOffset(0, 0, 8, 8);
            bodyV.childAlignment = TextAnchor.UpperCenter;
            bodyV.childControlWidth = true;
            bodyV.childControlHeight = true;
            bodyV.childForceExpandWidth = true;
            bodyV.childForceExpandHeight = false;

            if (hostOnlineButton == null)
                hostOnlineButton = CreateMenuButton("QuickJoinDedicatedButton", "Quick join", Vector2.zero, new Vector2(360f, 48f), lobbyScreenBodyRect, isPrimary: true);
            else
                hostOnlineButton.transform.SetParent(lobbyScreenBodyRect, false);

            if (_webGlBrowserHostButton == null)
            {
                _webGlBrowserHostButton = CreateMenuButton(
                    "WebGlBrowserHostButton",
                    "Host match (browser)",
                    Vector2.zero,
                    new Vector2(360f, 48f),
                    lobbyScreenBodyRect,
                    isPrimary: false);
                _webGlBrowserHostButton.onClick.AddListener(OnWebGlBrowserHostRelayClicked);
            }
            else
            {
                _webGlBrowserHostButton.transform.SetParent(lobbyScreenBodyRect, false);
            }

            var joinRow = new GameObject("JoinRow", typeof(RectTransform), typeof(HorizontalLayoutGroup));
            joinRow.transform.SetParent(lobbyScreenBodyRect, false);
            var joinRowRt = joinRow.GetComponent<RectTransform>();
            var joinH = joinRow.GetComponent<HorizontalLayoutGroup>();
            joinH.spacing = 12f;
            joinH.padding = new RectOffset(0, 0, 0, 0);
            joinH.childAlignment = TextAnchor.MiddleCenter;
            joinH.childControlWidth = true;
            joinH.childControlHeight = true;
            joinH.childForceExpandWidth = true;
            joinH.childForceExpandHeight = false;
            var joinRowLe = joinRow.AddComponent<LayoutElement>();
            joinRowLe.minHeight = 52f;
            joinRowLe.preferredHeight = 56f;

            if (joinCodeInputField != null)
            {
                joinCodeInputField.transform.SetParent(joinRow.transform, false);
                var inputLe = joinCodeInputField.GetComponent<LayoutElement>() ?? joinCodeInputField.gameObject.AddComponent<LayoutElement>();
                inputLe.flexibleWidth = 1f;
                inputLe.minWidth = 200f;
                inputLe.preferredHeight = 48f;
            }

            if (joinOnlineButton == null)
                joinOnlineButton = CreateMenuButton("JoinOnlineButton", "Join with code", Vector2.zero, new Vector2(200f, 48f), joinRow.GetComponent<RectTransform>(), isPrimary: false);
            else
                joinOnlineButton.transform.SetParent(joinRow.transform, false);

            if (joinCodeDisplayText != null)
            {
                joinCodeDisplayText.transform.SetParent(lobbyScreenBodyRect, false);
                var dispLe = joinCodeDisplayText.GetComponent<LayoutElement>() ?? joinCodeDisplayText.gameObject.AddComponent<LayoutElement>();
                dispLe.minHeight = 36f;
                dispLe.preferredHeight = 44f;
            }

            if (lobbyBrowserRoot == null)
                BuildLobbyBrowserPanel(lobbyScreenBodyRect);
            else
            {
                lobbyBrowserRoot.transform.SetParent(lobbyScreenBodyRect, false);
                DestroyIapRowUnderLobbyBrowserIfPresent();
            }

            lobbyScreenRoot.SetActive(false);
        }

        private void DestroyIapRowUnderLobbyBrowserIfPresent()
        {
            if (lobbyBrowserRoot == null)
                return;
            var iapRow = lobbyBrowserRoot.transform.Find("TitanOrbitIapRow");
            if (iapRow != null)
                Destroy(iapRow.gameObject);
        }

        public void ShowLobbyScreen()
        {
            EnsureLobbyScreenUi();
            if (lobbyScreenRoot != null)
                lobbyScreenRoot.SetActive(true);
            if (mainMenuPanel != null)
                mainMenuPanel.SetActive(false);
            if (storeScreenRoot != null)
                storeScreenRoot.SetActive(false);
            _ = RefreshLobbyListAsync();
        }

        private void HideLobbyScreen()
        {
            if (lobbyScreenRoot != null)
                lobbyScreenRoot.SetActive(false);
            if (mainMenuPanel != null)
                mainMenuPanel.SetActive(true);
            LayoutMainMenuActionStack();
        }

        private void EnsureStoreScreenUi()
        {
            if (mainMenuPanel == null || storeScreenRoot != null)
                return;

            var canvas = mainMenuPanel.GetComponentInParent<Canvas>();
            Transform canvasTr = canvas != null ? canvas.transform : mainMenuPanel.transform;

            storeScreenRoot = new GameObject("TitanOrbitStoreScreen", typeof(RectTransform), typeof(Image));
            storeScreenRoot.transform.SetParent(canvasTr, false);
            var sRt = storeScreenRoot.GetComponent<RectTransform>();
            sRt.anchorMin = Vector2.zero;
            sRt.anchorMax = Vector2.one;
            sRt.offsetMin = Vector2.zero;
            sRt.offsetMax = Vector2.zero;
            storeScreenRoot.GetComponent<Image>().color = new Color(0.02f, 0.04f, 0.08f, 1f);

            var topBar = new GameObject("StoreTopBar", typeof(RectTransform), typeof(HorizontalLayoutGroup));
            topBar.transform.SetParent(storeScreenRoot.transform, false);
            var topRt = topBar.GetComponent<RectTransform>();
            topRt.anchorMin = new Vector2(0f, 1f);
            topRt.anchorMax = new Vector2(1f, 1f);
            topRt.pivot = new Vector2(0.5f, 1f);
            topRt.sizeDelta = new Vector2(0f, 56f);
            topRt.anchoredPosition = Vector2.zero;
            var topH = topBar.GetComponent<HorizontalLayoutGroup>();
            topH.padding = new RectOffset(16, 16, 8, 8);
            topH.spacing = 16f;
            topH.childAlignment = TextAnchor.MiddleLeft;

            storeScreenBackButton = CreateMenuButton("StoreBackButton", "Back", Vector2.zero, new Vector2(120f, 44f), topBar.GetComponent<RectTransform>(), isPrimary: false);
            storeScreenBackButton.onClick.AddListener(HideStoreScreen);

            var storeTitle = CreateLabel("StoreTitle", "Store", Vector2.zero, 26f, topBar.transform, raycastTarget: false);
            storeTitle.AddComponent<LayoutElement>().flexibleWidth = 1f;

            var scrollRoot = new GameObject("StoreScroll", typeof(RectTransform), typeof(Image), typeof(ScrollRect));
            scrollRoot.transform.SetParent(storeScreenRoot.transform, false);
            var scrollRt = scrollRoot.GetComponent<RectTransform>();
            scrollRt.anchorMin = Vector2.zero;
            scrollRt.anchorMax = Vector2.one;
            scrollRt.offsetMin = new Vector2(24f, 96f);
            scrollRt.offsetMax = new Vector2(-24f, -64f);
            scrollRoot.GetComponent<Image>().color = new Color(0.05f, 0.08f, 0.12f, 1f);

            var viewport = new GameObject("Viewport", typeof(RectTransform), typeof(Mask), typeof(Image));
            viewport.transform.SetParent(scrollRoot.transform, false);
            var vpRt = viewport.GetComponent<RectTransform>();
            vpRt.anchorMin = Vector2.zero;
            vpRt.anchorMax = Vector2.one;
            vpRt.offsetMin = Vector2.zero;
            vpRt.offsetMax = Vector2.zero;
            viewport.GetComponent<Image>().color = new Color(0.07f, 0.1f, 0.14f, 1f);
            viewport.GetComponent<Mask>().showMaskGraphic = false;

            var content = new GameObject("StoreList", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            content.transform.SetParent(viewport.transform, false);
            var cRt = content.GetComponent<RectTransform>();
            cRt.anchorMin = new Vector2(0f, 1f);
            cRt.anchorMax = new Vector2(1f, 1f);
            cRt.pivot = new Vector2(0.5f, 1f);
            cRt.anchoredPosition = Vector2.zero;
            cRt.sizeDelta = new Vector2(0f, 0f);
            storeScreenListRoot = cRt;
            var vlg = content.GetComponent<VerticalLayoutGroup>();
            vlg.spacing = 12f;
            vlg.padding = new RectOffset(12, 12, 12, 12);
            vlg.childAlignment = TextAnchor.UpperCenter;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            content.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var sr = scrollRoot.GetComponent<ScrollRect>();
            sr.viewport = vpRt;
            sr.content = cRt;
            sr.horizontal = false;
            sr.vertical = true;

            var footer = new GameObject("StoreFooter", typeof(RectTransform), typeof(HorizontalLayoutGroup));
            footer.transform.SetParent(storeScreenRoot.transform, false);
            var fRt = footer.GetComponent<RectTransform>();
            fRt.anchorMin = new Vector2(0f, 0f);
            fRt.anchorMax = new Vector2(1f, 0f);
            fRt.pivot = new Vector2(0.5f, 0f);
            fRt.sizeDelta = new Vector2(0f, 72f);
            fRt.anchoredPosition = new Vector2(0f, 16f);
            storeRestorePurchasesButton = CreateMenuButton("StoreRestorePurchases", "Restore purchases", Vector2.zero, new Vector2(320f, 48f), footer.GetComponent<RectTransform>(), isPrimary: false);
            storeRestorePurchasesButton.onClick.AddListener(OnRestorePurchasesClicked);

            storeScreenRoot.SetActive(false);
        }

        public void ShowStoreScreen()
        {
            EnsureStoreScreenUi();
            if (storeScreenRoot != null)
                storeScreenRoot.SetActive(true);
            if (mainMenuPanel != null)
                mainMenuPanel.SetActive(false);
            if (lobbyScreenRoot != null)
                lobbyScreenRoot.SetActive(false);
            CloseSignInOptionsPopup();
            RefreshStoreProductList();
        }

        private void HideStoreScreen()
        {
            if (storeScreenRoot != null)
                storeScreenRoot.SetActive(false);
            if (mainMenuPanel != null)
                mainMenuPanel.SetActive(true);
            LayoutMainMenuActionStack();
        }

        private void RefreshStoreProductList()
        {
            if (storeScreenListRoot == null)
                return;

            for (int i = storeScreenListRoot.childCount - 1; i >= 0; i--)
                Destroy(storeScreenListRoot.GetChild(i).gameObject);

            var iap = UnityEngine.Object.FindFirstObjectByType<TitanOrbitIapManager>();
            if (iap == null)
            {
                var msg = CreateLabel("StoreNoIap", "Store is not available.", Vector2.zero, 20f, storeScreenListRoot, raycastTarget: false);
                msg.AddComponent<LayoutElement>().minHeight = 40f;
                return;
            }

            foreach (var entry in iap.GetCatalogSnapshot())
            {
                string pid = entry.productId?.Trim();
                if (string.IsNullOrEmpty(pid))
                    continue;

                var row = new GameObject("StoreRow_" + pid, typeof(RectTransform), typeof(Image), typeof(HorizontalLayoutGroup));
                row.transform.SetParent(storeScreenListRoot, false);
                row.GetComponent<Image>().color = new Color(0.1f, 0.14f, 0.2f, 0.96f);
                var h = row.GetComponent<HorizontalLayoutGroup>();
                h.padding = new RectOffset(16, 16, 12, 12);
                h.spacing = 12f;
                h.childAlignment = TextAnchor.MiddleLeft;
                h.childControlWidth = true;
                h.childControlHeight = true;
                h.childForceExpandWidth = true;
                row.AddComponent<LayoutElement>().minHeight = 72f;

                string title = iap.GetProductLocalizedTitle(pid);
                string price = iap.IsStoreReady ? iap.GetLocalizedPriceString(pid) : "";
                string status = iap.GetUiOwnershipLabel(pid);
                string typeStr = entry.productType.ToString();
                string body = $"<b>{title}</b>\n<size=85%><color=#9ec4e8>{pid}</color>  ·  {typeStr}  ·  <b>{status}</b>";
                if (!string.IsNullOrEmpty(price))
                    body += $"\n<size=90%>{price}</size>";

                var textGo = CreateLabel("StoreRowText", body, Vector2.zero, 18f, row.transform, raycastTarget: false);
                var textLe = textGo.AddComponent<LayoutElement>();
                textLe.flexibleWidth = 1f;
                var tmp = textGo.GetComponent<TextMeshProUGUI>();
                tmp.alignment = TextAlignmentOptions.MidlineLeft;
                tmp.enableWordWrapping = true;

                bool canBuy = iap.IsStoreReady && iap.CanInitiatePurchase(pid);
                var buyBtn = CreateMenuButton("Buy_" + pid, "Buy", Vector2.zero, new Vector2(120f, 44f), row.GetComponent<RectTransform>(), isPrimary: true);
                string capturedId = pid;
                buyBtn.interactable = canBuy;
                buyBtn.onClick.AddListener(() => OnStorePurchaseProductClicked(capturedId));
                if (iap.IsPurchasedOrHasReceipt(pid) && entry.productType == ProductType.NonConsumable)
                {
                    buyBtn.gameObject.SetActive(false);
                    var owned = CreateLabel("Owned_" + pid, "Owned", Vector2.zero, 18f, row.transform, raycastTarget: false);
                    owned.AddComponent<LayoutElement>().preferredWidth = 96f;
                }
            }
        }

        /// <summary>
        /// Places title (if any), player name, input, Play, and Store in a vertical stack, centered in the main panel with generous spacing. Auth is positioned separately (top-right).
        /// </summary>
        private void LayoutMainMenuActionStack()
        {
            if (mainMenuPanel == null)
                return;

            var mainRect = mainMenuPanel.GetComponent<RectTransform>();
            float panelW = mainRect != null && mainRect.rect.width > 1f ? mainRect.rect.width : 1920f;
            float panelH = mainRect != null && mainRect.rect.height > 1f ? mainRect.rect.height : 1080f;
            float contentW = Mathf.Max(280f, panelW - 80f);

            const float gapAfterTitle = 28f;
            const float gapLabelToInput = 22f;
            const float gapInputToPlay = 28f;
            const float gapPlayToStore = 22f;
            const float minEdgeMargin = 32f;

            Transform titleTf = mainMenuPanel.transform.Find("Title");
            var titleRt = titleTf != null ? titleTf.GetComponent<RectTransform>() : null;
            Transform labelTf = mainMenuPanel.transform.Find("PlayerNameLabel");
            var labelRt = labelTf != null ? labelTf.GetComponent<RectTransform>() : null;
            var inputRt = playerNameInputField != null ? playerNameInputField.GetComponent<RectTransform>() : null;
            var playRt = playButton != null ? playButton.GetComponent<RectTransform>() : null;
            var storeRt = mainMenuStoreButton != null ? mainMenuStoreButton.GetComponent<RectTransform>() : null;

            float titleH = 0f, titleW = 0f, labelH = 0f, labelW = 0f, inputH = 0f, inputW = 0f, playH = 0f, playW = 0f, storeH = 0f, storeW = 0f;

            if (titleRt != null)
            {
                titleH = titleRt.sizeDelta.y > 1f ? titleRt.sizeDelta.y : 88f;
                titleW = Mathf.Min(800f, contentW);
            }
            if (labelRt != null)
            {
                labelH = labelRt.sizeDelta.y > 1f ? labelRt.sizeDelta.y : 28f;
                labelW = Mathf.Min(400f, contentW);
            }
            if (inputRt != null)
            {
                inputH = inputRt.sizeDelta.y > 1f ? inputRt.sizeDelta.y : 72f;
                inputW = inputRt.sizeDelta.x > 1f ? inputRt.sizeDelta.x : Mathf.Min(440f, contentW);
            }
            if (playRt != null)
            {
                playH = playRt.sizeDelta.y > 1f ? playRt.sizeDelta.y : 52f;
                playW = playRt.sizeDelta.x > 1f ? playRt.sizeDelta.x : Mathf.Min(360f, contentW);
            }
            if (storeRt != null)
            {
                storeH = storeRt.sizeDelta.y > 1f ? storeRt.sizeDelta.y : 48f;
                storeW = storeRt.sizeDelta.x > 1f ? storeRt.sizeDelta.x : Mathf.Min(360f, contentW);
            }

            float gaps = 0f;
            if (titleRt != null) gaps += gapAfterTitle;
            if (labelRt != null) gaps += gapLabelToInput;
            if (inputRt != null) gaps += gapInputToPlay;
            if (playRt != null && storeRt != null) gaps += gapPlayToStore;

            float totalH = titleH + labelH + inputH + playH + storeH + gaps;
            float startY = (panelH - totalH) * 0.5f;
            if (startY < minEdgeMargin)
                startY = minEdgeMargin;
            float y = startY;

            void PlaceTopDown(RectTransform rt, float height, float width)
            {
                if (rt == null)
                    return;
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
                rt.pivot = new Vector2(0.5f, 1f);
                rt.sizeDelta = new Vector2(width, height);
                rt.anchoredPosition = new Vector2(0f, -y);
                y += height;
            }

            if (titleRt != null)
            {
                PlaceTopDown(titleRt, titleH, titleW);
                y += gapAfterTitle;
            }
            if (labelRt != null)
            {
                PlaceTopDown(labelRt, labelH, labelW);
                y += gapLabelToInput;
            }
            if (inputRt != null)
            {
                PlaceTopDown(inputRt, inputH, inputW);
                y += gapInputToPlay;
            }
            if (playRt != null)
            {
                PlaceTopDown(playRt, playH, playW);
                if (storeRt != null)
                    y += gapPlayToStore;
            }
            if (storeRt != null)
                PlaceTopDown(storeRt, storeH, storeW);

            PositionMainMenuAuthTopRight();
        }

        /// <summary>
        /// Ensures listeners are bound after runtime UI is built (and avoids missing clicks if scene refs were reassigned).
        /// </summary>
        private void WireLobbyBrowserListeners()
        {
            if (refreshLobbiesButton != null)
            {
                refreshLobbiesButton.onClick.RemoveListener(OnRefreshLobbiesClicked);
                refreshLobbiesButton.onClick.AddListener(OnRefreshLobbiesClicked);
            }
            if (joinSelectedLobbyButton != null)
            {
                joinSelectedLobbyButton.onClick.RemoveListener(OnJoinSelectedLobbyClicked);
                joinSelectedLobbyButton.onClick.AddListener(OnJoinSelectedLobbyClicked);
            }
        }

        private void BuildLobbyBrowserPanel(RectTransform parent)
        {
            lobbyBrowserRoot = new GameObject("LobbyBrowserRoot", typeof(RectTransform), typeof(Image));
            lobbyBrowserRoot.transform.SetParent(parent, false);
            var rootRect = lobbyBrowserRoot.GetComponent<RectTransform>();
            rootRect.anchorMin = new Vector2(0f, 1f);
            rootRect.anchorMax = new Vector2(1f, 1f);
            rootRect.pivot = new Vector2(0.5f, 1f);
            rootRect.sizeDelta = new Vector2(0f, 420f);
            rootRect.anchoredPosition = Vector2.zero;

            var rootImage = lobbyBrowserRoot.GetComponent<Image>();
            rootImage.color = new Color(0.035f, 0.065f, 0.11f, 0.98f);
            rootImage.raycastTarget = false;

            var rootVlg = lobbyBrowserRoot.AddComponent<VerticalLayoutGroup>();
            rootVlg.spacing = 12f;
            rootVlg.padding = new RectOffset(16, 16, 14, 16);
            rootVlg.childAlignment = TextAnchor.UpperCenter;
            rootVlg.childControlWidth = true;
            rootVlg.childControlHeight = true;
            rootVlg.childForceExpandWidth = true;
            rootVlg.childForceExpandHeight = false;

            var rootLe = lobbyBrowserRoot.AddComponent<LayoutElement>();
            rootLe.minHeight = 220f;
            rootLe.preferredHeight = 360f;
            rootLe.flexibleHeight = 1f;

            var titleObj = CreateLabel("LobbyBrowserTitle", "Open matches", Vector2.zero, 32f, lobbyBrowserRoot.transform, raycastTarget: false);
            var titleRect = titleObj.GetComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0f, 1f);
            titleRect.anchorMax = new Vector2(1f, 1f);
            titleRect.pivot = new Vector2(0.5f, 1f);
            titleRect.sizeDelta = Vector2.zero;
            titleRect.anchoredPosition = Vector2.zero;
            var titleTmp = titleObj.GetComponent<TextMeshProUGUI>();
            titleTmp.enableWordWrapping = false;
            titleTmp.fontStyle = FontStyles.Bold;
            titleTmp.color = new Color(0.95f, 0.97f, 1f, 1f);
            titleTmp.outlineWidth = 0.15f;
            titleTmp.outlineColor = new Color32(20, 40, 70, 200);
            var titleLe = titleObj.AddComponent<LayoutElement>();
            titleLe.minHeight = 36f;
            titleLe.preferredHeight = 40f;

            var statusObj = CreateLabel("LobbyBrowserStatusText", "Select a lobby to join.", Vector2.zero, 20f, lobbyBrowserRoot.transform, raycastTarget: false);
            var statusRect = statusObj.GetComponent<RectTransform>();
            statusRect.anchorMin = new Vector2(0f, 1f);
            statusRect.anchorMax = new Vector2(1f, 1f);
            statusRect.pivot = new Vector2(0.5f, 1f);
            statusRect.sizeDelta = Vector2.zero;
            statusRect.anchoredPosition = Vector2.zero;
            lobbyBrowserStatusText = statusObj.GetComponent<TextMeshProUGUI>();
            lobbyBrowserStatusText.enableWordWrapping = true;
            lobbyBrowserStatusText.color = new Color(0.75f, 0.86f, 0.98f, 0.95f);
            lobbyBrowserStatusText.overflowMode = TextOverflowModes.Ellipsis;
            var statusLe = statusObj.AddComponent<LayoutElement>();
            statusLe.minHeight = 32f;
            statusLe.preferredHeight = 40f;

            var scrollRootObj = new GameObject("LobbyListScrollRect", typeof(RectTransform), typeof(Image), typeof(ScrollRect));
            scrollRootObj.transform.SetParent(lobbyBrowserRoot.transform, false);
            var scrollRootRect = scrollRootObj.GetComponent<RectTransform>();
            scrollRootRect.anchorMin = Vector2.zero;
            scrollRootRect.anchorMax = Vector2.one;
            scrollRootRect.offsetMin = Vector2.zero;
            scrollRootRect.offsetMax = Vector2.zero;
            scrollRootRect.pivot = new Vector2(0.5f, 0.5f);
            var scrollLe = scrollRootObj.AddComponent<LayoutElement>();
            scrollLe.minHeight = 120f;
            scrollLe.preferredHeight = 200f;
            scrollLe.flexibleHeight = 1f;

            var scrollBg = scrollRootObj.GetComponent<Image>();
            scrollBg.color = new Color(0.055f, 0.09f, 0.145f, 0.98f);
            scrollBg.raycastTarget = true;

            var viewportObj = new GameObject("LobbyListViewport", typeof(RectTransform), typeof(Image), typeof(Mask));
            viewportObj.transform.SetParent(scrollRootObj.transform, false);
            var viewportRect = viewportObj.GetComponent<RectTransform>();
            viewportRect.anchorMin = Vector2.zero;
            viewportRect.anchorMax = Vector2.one;
            viewportRect.offsetMin = Vector2.zero;
            viewportRect.offsetMax = Vector2.zero;
            viewportRect.pivot = new Vector2(0.5f, 0.5f);
            var viewportImage = viewportObj.GetComponent<Image>();
            viewportImage.color = new Color(0.07f, 0.11f, 0.17f, 1f);
            viewportImage.raycastTarget = true;
            var mask = viewportObj.GetComponent<Mask>();
            mask.showMaskGraphic = true;

            var contentObj = new GameObject("LobbyListContainer", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            contentObj.transform.SetParent(viewportObj.transform, false);
            var contentRect = contentObj.GetComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0f, 1f);
            contentRect.anchorMax = new Vector2(1f, 1f);
            contentRect.pivot = new Vector2(0.5f, 1f);
            contentRect.anchoredPosition = Vector2.zero;
            contentRect.sizeDelta = new Vector2(0f, 0f);
            lobbyListContainer = contentObj.transform;

            var vlg = contentObj.GetComponent<VerticalLayoutGroup>();
            vlg.spacing = 14f;
            vlg.padding = new RectOffset(16, 16, 16, 16);
            vlg.childAlignment = TextAnchor.UpperCenter;
            vlg.childControlHeight = true;
            vlg.childControlWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.childForceExpandWidth = true;

            var fitter = contentObj.GetComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var scrollRect = scrollRootObj.GetComponent<ScrollRect>();
            scrollRect.viewport = viewportRect;
            scrollRect.content = contentRect;
            scrollRect.horizontal = false;
            scrollRect.vertical = true;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;
            scrollRect.scrollSensitivity = 28f;
            scrollRect.inertia = true;
            scrollRect.decelerationRate = 0.12f;

            var footerObj = new GameObject("LobbyBrowserFooter", typeof(RectTransform), typeof(HorizontalLayoutGroup));
            footerObj.transform.SetParent(lobbyBrowserRoot.transform, false);
            var footerRect = footerObj.GetComponent<RectTransform>();
            footerRect.anchorMin = new Vector2(0f, 1f);
            footerRect.anchorMax = new Vector2(1f, 1f);
            footerRect.pivot = new Vector2(0.5f, 1f);
            footerRect.sizeDelta = new Vector2(0f, 56f);
            footerRect.anchoredPosition = Vector2.zero;
            var footerLe = footerObj.AddComponent<LayoutElement>();
            footerLe.minHeight = 52f;
            footerLe.preferredHeight = 56f;

            var hlg = footerObj.GetComponent<HorizontalLayoutGroup>();
            hlg.spacing = 16f;
            hlg.padding = new RectOffset(8, 8, 6, 8);
            hlg.childAlignment = TextAnchor.MiddleCenter;
            hlg.childControlHeight = true;
            hlg.childControlWidth = true;
            hlg.childForceExpandHeight = false;
            hlg.childForceExpandWidth = true;

            refreshLobbiesButton = CreateMenuButton("RefreshLobbiesButton", "Refresh list", Vector2.zero, new Vector2(360f, 48f), footerRect, isPrimary: false);
            joinSelectedLobbyButton = CreateMenuButton("JoinSelectedLobbyButton", "Join selected", Vector2.zero, new Vector2(360f, 48f), footerRect, isPrimary: true);

            lobbyListRowPrefab = CreateLobbyRowPrefab();
            if (lobbyListRowPrefab != null)
                lobbyListRowPrefab.SetActive(false);
        }

        private Button CreateMenuButton(string name, string label, Vector2 anchoredPosition, Vector2 size, RectTransform parent, bool isPrimary = true)
        {
            if (parent == null)
                return null;

            var buttonObj = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            buttonObj.transform.SetParent(parent, false);
            var rect = buttonObj.GetComponent<RectTransform>();
            var inLayoutGroup = parent.GetComponent<HorizontalLayoutGroup>() != null || parent.GetComponent<VerticalLayoutGroup>() != null;
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = size;
            if (inLayoutGroup)
            {
                var le = buttonObj.AddComponent<LayoutElement>();
                le.preferredWidth = size.x;
                le.preferredHeight = size.y;
                le.minWidth = Mathf.Max(80f, size.x * 0.5f);
                le.minHeight = size.y;
            }

            var image = buttonObj.GetComponent<Image>();
            image.color = isPrimary
                ? new Color(0.22f, 0.52f, 0.88f, 0.95f)
                : new Color(0.16f, 0.22f, 0.32f, 0.92f);
            image.raycastTarget = true;

            var btn = buttonObj.GetComponent<Button>();
            btn.targetGraphic = image;
            var colors = btn.colors;
            colors.normalColor = image.color;
            colors.highlightedColor = isPrimary ? new Color(0.32f, 0.62f, 0.98f, 1f) : new Color(0.24f, 0.32f, 0.44f, 1f);
            colors.pressedColor = isPrimary ? new Color(0.14f, 0.38f, 0.72f, 1f) : new Color(0.12f, 0.2f, 0.32f, 1f);
            colors.selectedColor = colors.normalColor;
            colors.disabledColor = new Color(0.2f, 0.22f, 0.28f, 0.55f);
            colors.colorMultiplier = 1f;
            colors.fadeDuration = 0.1f;
            btn.colors = colors;

            var textObj = CreateLabel(name + "_Label", label, Vector2.zero, 26f, buttonObj.transform, raycastTarget: false);
            if (textObj != null)
            {
                var textRect = textObj.GetComponent<RectTransform>();
                textRect.anchorMin = Vector2.zero;
                textRect.anchorMax = Vector2.one;
                textRect.offsetMin = Vector2.zero;
                textRect.offsetMax = Vector2.zero;
                var tmp = textObj.GetComponent<TextMeshProUGUI>();
                tmp.fontStyle = FontStyles.Bold;
                tmp.color = new Color(0.96f, 0.98f, 1f, 1f);
            }

            return btn;
        }

        private GameObject CreateLabel(string name, string text, Vector2 anchoredPosition, float fontSize, Transform parent = null, bool raycastTarget = true)
        {
            Transform targetParent = parent != null ? parent : mainMenuPanel.transform;
            var labelObj = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            labelObj.transform.SetParent(targetParent, false);
            var rect = labelObj.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = new Vector2(760f, 42f);

            var tmp = labelObj.GetComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = fontSize;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = new Color(0.92f, 0.95f, 1f, 1f);
            tmp.raycastTarget = raycastTarget;
            return labelObj;
        }

        private GameObject CreateLobbyRowPrefab()
        {
            if (lobbyBrowserRoot == null)
                return null;

            var rowObj = new GameObject("LobbyListRowPrefab", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
            rowObj.transform.SetParent(lobbyBrowserRoot.transform, false);

            var rect = rowObj.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(860f, 72f);

            var image = rowObj.GetComponent<Image>();
            image.color = new Color(0.11f, 0.17f, 0.28f, 0.98f);
            image.raycastTarget = true;

            var btn = rowObj.GetComponent<Button>();
            var colors = btn.colors;
            colors.normalColor = image.color;
            colors.highlightedColor = new Color(0.16f, 0.26f, 0.42f, 1f);
            colors.pressedColor = new Color(0.09f, 0.14f, 0.24f, 1f);
            colors.selectedColor = new Color(0.18f, 0.38f, 0.62f, 0.98f);
            colors.disabledColor = new Color(0.12f, 0.2f, 0.32f, 0.95f);
            btn.colors = colors;
            btn.transition = Selectable.Transition.ColorTint;

            var layoutElement = rowObj.GetComponent<LayoutElement>();
            layoutElement.minHeight = 72f;
            layoutElement.preferredHeight = 72f;

            var textObj = CreateLabel("LobbyRowLabel", "Lobby", Vector2.zero, 30f, rowObj.transform, raycastTarget: false);
            if (textObj != null)
            {
                var textRect = textObj.GetComponent<RectTransform>();
                textRect.anchorMin = Vector2.zero;
                textRect.anchorMax = Vector2.one;
                textRect.offsetMin = new Vector2(20f, 10f);
                textRect.offsetMax = new Vector2(-20f, -10f);
                var tmp = textObj.GetComponent<TextMeshProUGUI>();
                if (tmp != null)
                {
                    tmp.alignment = TextAlignmentOptions.MidlineLeft;
                    tmp.enableWordWrapping = true;
                    tmp.richText = true;
                }
            }

            return rowObj;
        }

        private static void EnsureEventSystemExists()
        {
            if (FindFirstObjectByType<EventSystem>() != null)
                return;
            var eventSystemObj = new GameObject("EventSystem", typeof(EventSystem));
            eventSystemObj.AddComponent<InputSystemUIInputModule>();
            DontDestroyOnLoad(eventSystemObj);
        }

        private void ShowLobby()
        {
            if (mainMenuPanel != null)
                mainMenuPanel.SetActive(false);
            if (lobbyScreenRoot != null)
                lobbyScreenRoot.SetActive(false);
            if (storeScreenRoot != null)
                storeScreenRoot.SetActive(false);

            string playerName = (playerNameInputField != null ? playerNameInputField.text : null) ?? "";
            playerName = playerName.Trim();
            NetworkGameManager.LocalPlayerDisplayName = string.IsNullOrEmpty(playerName)
                ? TitanOrbit.Data.GameNames.GetRandomPlayerName()
                : playerName;

            if (loadingScreenController != null)
            {
                deferTeamPanelUntilNetworkReady = false;
                if (lobbyPanel != null)
                    lobbyPanel.SetActive(false);
                if (teamSelectionPanel != null)
                    teamSelectionPanel.SetActive(false);
                loadingScreenController.ShowLoading();
                return;
            }

            deferTeamPanelUntilNetworkReady = true;
            if (lobbyPanel != null)
                lobbyPanel.SetActive(true);

            if (teamSelectionPanel != null)
                teamSelectionPanel.SetActive(false);
        }

        /// <summary>Called by LoadingScreenController when loading is complete. Shows lobby and team selection (hides loading).</summary>
        public void ShowLobbyAndTeamSelection()
        {
            deferTeamPanelUntilNetworkReady = false;
            if (mainMenuPanel != null)
                mainMenuPanel.SetActive(false);
            if (lobbyScreenRoot != null)
                lobbyScreenRoot.SetActive(false);
            if (storeScreenRoot != null)
                storeScreenRoot.SetActive(false);

            string playerName = (playerNameInputField != null ? playerNameInputField.text : null) ?? "";
            playerName = playerName.Trim();
            NetworkGameManager.LocalPlayerDisplayName = string.IsNullOrEmpty(playerName)
                ? TitanOrbit.Data.GameNames.GetRandomPlayerName()
                : playerName;

            if (lobbyPanel != null)
            {
                lobbyPanel.SetActive(true);
            }

            if (teamSelectionPanel != null)
            {
                teamSelectionPanel.SetActive(true);
            }
        }

        private void UpdateLobbyInfo()
        {
            if (roomNameText != null && NetworkGameManager.Instance != null)
                roomNameText.text = "Room: " + (string.IsNullOrEmpty(NetworkGameManager.Instance.CurrentLobbyName) ? "—" : NetworkGameManager.Instance.CurrentLobbyName);

            if (playerCountText != null)
            {
                int playerCount = 0;
                if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
                    playerCount = NetworkManager.Singleton.ConnectedClients.Count;
                playerCountText.text = $"Players: {playerCount}/60";
            }

            if (teamStatusText != null && Core.TeamManager.Instance != null)
            {
                int active = Core.TeamManager.Instance.GetEffectiveTeamCountForUI();
                var parts = new System.Collections.Generic.List<string>();
                for (int i = 0; i < active; i++)
                {
                    var t = (Core.TeamManager.Team)(i + 1);
                    int n = Core.TeamManager.Instance.GetTeamPlayerCount(t);
                    parts.Add($"Team {(char)('A' + i)}: {n}/20");
                }
                string countsLine = string.Join(" | ", parts);
                teamStatusText.text = string.IsNullOrEmpty(pendingTeamJoinError)
                    ? countsLine
                    : pendingTeamJoinError + "\n" + countsLine;
            }
        }
    }
}
