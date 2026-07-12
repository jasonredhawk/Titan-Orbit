using System;
using System.Collections;
using System.Threading.Tasks;
using TitanOrbit.Data;
using TitanOrbit.Core;
using TitanOrbit.ECS;
using TitanOrbit.NetCode;
using TitanOrbit.Services;
using TMPro;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.Game
{
    /// <summary>
    /// [HYBRID] Drives the NCE (NetCode Entities) vertical-slice UI flow: main menu → local connect →
    /// team pick → loading → gameplay HUD. Wires buttons to <see cref="TitanOrbitSessionManager"/> and
    /// listens for team-choice / rejoin RPC results. Client only — dedicated server has no canvas.
    /// </summary>
    public class NceGameFlowController : MonoBehaviour
    {
        [Header("UI Panels")]
        /// <summary>Root panel with Play button and connection status.</summary>
        [SerializeField] GameObject mainMenuPanel;
        /// <summary>Optional intermediate lobby (browser / relay) before team select.</summary>
        [SerializeField] GameObject lobbyPanel;
        /// <summary>Five-team picker shown after successful connect.</summary>
        [SerializeField] GameObject teamSelectionPanel;
        /// <summary>Full-screen blocker while worlds and ghosts stream in.</summary>
        [SerializeField] GameObject loadingRoot;
        /// <summary>HUD root enabled after local ship spawns.</summary>
        [SerializeField] GameObject gameplayRoot;
        /// <summary>Optional ship stat readout panel during gameplay.</summary>
        [SerializeField] GameObject shipStatsPanel;

        [Header("Main Menu")]
        [SerializeField] Button playButton;
        [SerializeField] TextMeshProUGUI statusText;

        [Header("Team Buttons")]
        [SerializeField] Button teamAButton;
        [SerializeField] Button teamBButton;
        [SerializeField] Button teamCButton;
        [SerializeField] Button teamDButton;
        [SerializeField] Button teamEButton;

        [Header("Team Panels (optional; auto-found by name)")]
        [SerializeField] GameObject teamAPanel;
        [SerializeField] GameObject teamBPanel;
        [SerializeField] GameObject teamCPanel;
        [SerializeField] GameObject teamDPanel;
        [SerializeField] GameObject teamEPanel;

        /// <summary>Fixed team order for wiring parallel button/panel arrays.</summary>
        static readonly TeamId[] TeamOrder =
        {
            TeamId.TeamA, TeamId.TeamB, TeamId.TeamC, TeamId.TeamD, TeamId.TeamE,
        };

        Button[] _teamButtons;
        GameObject[] _teamPanels;

        [Header("Dev")]
        [SerializeField] bool autoStartLocalPlayInEditor = false;
        [Tooltip("Editor-only: auto-join Team A after a delay. Leave off to pick a team manually.")]
        [SerializeField] bool autoPickTeamAInEditor = false;
        [SerializeField] float autoPickDelaySeconds = 2f;

        bool _autoPickSent;
        bool _autoStartSent;
        bool _joinTeamUiCleaned;
        bool _teamPanelWidthsConfigured;
        bool _loggedWaitingForMap;
        bool _loggedTeamUiReady;
        float _connectedAt = -1f;
        float _dedicatedConnectedAt = -1f;
        float _mppmConnectedSince = -1f;
        LoadingScreenControllerNce _loadingScreen;
        JoinGameBrowserController _joinBrowser;
        RejoinShipChoiceController _rejoinChoice;
        string _statusMessage = "For Docker/dedicated server: click Join game. For local dev: Local host or Local play.";
        bool _mainMenuButtonsBuilt;
        bool _initialized;

        void Awake()
        {
            Debug.Log("[NceGameFlow] Awake on " + gameObject.name + " enabled=" + enabled);
        }

        void OnEnable()
        {
            Debug.Log("[NceGameFlow] OnEnable");
        }

        void Start()
        {
            Debug.Log("[NceGameFlow] Start");
#if !UNITY_EDITOR
#if UNITY_SERVER
            enabled = false;
            return;
#endif
            if (TitanOrbitDedicatedServerAutoBoot.IsDedicatedServerProcess())
            {
                enabled = false;
                return;
            }
#endif

            InitializeMenuFlow();
            MainMenuUiBootstrap.EnsureButtonsCreated();
        }

        /// <summary>Runs once after NetCode worlds exist — builds menu buttons and wires panels.</summary>
        void InitializeMenuFlow()
        {
            if (_initialized)
                return;
            _initialized = true;

            Debug.Log("[NceGameFlow] Initializing main menu flow.");

            ClientTeamFlowState.Reset();
            _teamButtons = new[] { teamAButton, teamBButton, teamCButton, teamDButton, teamEButton };
            _teamPanels = new[] { teamAPanel, teamBPanel, teamCPanel, teamDPanel, teamEPanel };
            ResolveMissingReferences();
            EnsureLoadingScreen();
            EnsureEcsWorldVisualizer();
            EnsureMatchFlowControllers();
            WireTeamButtons();
            EnsureMainMenuPlayButton();
            EnsureJoinGameBrowser();
            EnsureRejoinShipChoice();
            BuildMainMenuButtons();
            WireExistingMainMenuButtons();

            WirePlayButton();
            if (_teamButtons[0] == null || _teamButtons[1] == null || _teamButtons[2] == null)
                Debug.LogWarning("[NceGameFlow] One or more team Join buttons were not found. Expected TeamAPanel/Content/JoinButton etc.");
            else
                Debug.Log("[NceGameFlow] Team Join buttons wired.");

            LogNetCodeWorldState();

            if (mainMenuPanel != null) mainMenuPanel.SetActive(true);
            if (lobbyPanel != null) lobbyPanel.SetActive(false);
            if (teamSelectionPanel != null) teamSelectionPanel.SetActive(false);
            if (loadingRoot != null) loadingRoot.SetActive(false);
            if (gameplayRoot != null) gameplayRoot.SetActive(false);
            if (shipStatsPanel != null) shipStatsPanel.SetActive(false);

            if (TitanOrbitPlayModeUtility.IsMppmAdditionalEditorInstance())
            {
                _statusMessage = TitanOrbitMultiplayerConfig.ShowLocalPlayOptions
                    ? "Player " + TitanOrbitPlayModeUtility.GetMppmPlayerNumber() +
                      " — click Local client after the host starts Local play."
                    : "Player " + TitanOrbitPlayModeUtility.GetMppmPlayerNumber() +
                      " — use Join game for a dedicated match.";
            }

            WireTeamButtons();
            CleanupJoinTeamScreenUi();
            RefreshUi();
            PushStatusToUi();
            _ = PrimeGuestSessionAndPrefetchLobbiesAsync();

            if (autoStartLocalPlayInEditor && TitanOrbitMultiplayerConfig.ShowLocalPlayOptions &&
                Application.isEditor &&
                !TitanOrbitPlayModeUtility.IsMppmAdditionalEditorInstance())
                StartCoroutine(WaitAndStartLocalPlay());

            StartCoroutine(RetryBuildMainMenuButtons());
        }

        IEnumerator RetryBuildMainMenuButtons()
        {
            for (int i = 0; i < 5 && !_mainMenuButtonsBuilt; i++)
            {
                yield return null;
                ResolveMissingReferences();
                BuildMainMenuButtons();
                WireExistingMainMenuButtons();
            }

            if (!_mainMenuButtonsBuilt)
                Debug.LogError("[NceGameFlow] Failed to create Join game / host buttons after retries.");
        }

        void EnsureRejoinShipChoice()
        {
            _rejoinChoice = GetComponent<RejoinShipChoiceController>();
            if (_rejoinChoice == null)
                _rejoinChoice = gameObject.AddComponent<RejoinShipChoiceController>();
            _rejoinChoice.Configure(mainMenuPanel);
        }

        void EnsureJoinGameBrowser()
        {
            _joinBrowser = GetComponent<JoinGameBrowserController>();
            if (_joinBrowser == null)
                _joinBrowser = gameObject.AddComponent<JoinGameBrowserController>();
            _joinBrowser.Configure(mainMenuPanel);
        }

        void BuildMainMenuButtons()
        {
            if (mainMenuPanel == null)
            {
                ResolveMissingReferences();
                if (mainMenuPanel == null)
                {
                    Debug.LogError("[NceGameFlow] MainMenuPanel not found — Join game / host buttons cannot be created.");
                    return;
                }
            }

            if (_mainMenuButtonsBuilt)
                return;

            var panelRt = mainMenuPanel.GetComponent<RectTransform>();
            if (panelRt == null)
            {
                Debug.LogError("[NceGameFlow] MainMenuPanel has no RectTransform — menu buttons skipped.");
                return;
            }

            EnsureMainMenuStatusText();

            if (playButton != null)
            {
                var playLabel = playButton.GetComponentInChildren<TextMeshProUGUI>();
                if (playLabel != null)
                {
                    if (TitanOrbitPlayModeUtility.IsMppmAdditionalEditorInstance())
                        playLabel.text = "Local client";
                    else
                        playLabel.text = TitanOrbitMultiplayerConfig.ShowLocalPlayOptions
                            ? "Local play"
                            : "Quick join";
                }
            }

            float y = playButton != null
                ? playButton.GetComponent<RectTransform>().anchoredPosition.y - 56f
                : -170f;

            CreateMainMenuButton("BrowseGamesButton", "Join game", y, OnBrowseGamesClicked);
            y -= 56f;

            if (TitanOrbitMultiplayerConfig.ShowLocalPlayOptions)
            {
                if (!TitanOrbitPlayModeUtility.IsMppmAdditionalEditorInstance())
                    CreateMainMenuButton("LocalHostButton", "Local host", y, OnLocalHostClicked);
                if (!TitanOrbitPlayModeUtility.IsMppmAdditionalEditorInstance())
                    y -= 48f;
                CreateMainMenuButton("LocalClientButton", "Local client", y, OnLocalClientClicked);
            }

            _mainMenuButtonsBuilt = true;
            Debug.Log("[NceGameFlow] Main menu buttons built (Join game" +
                      (TitanOrbitMultiplayerConfig.ShowLocalPlayOptions ? ", Local host, Local client" : "") + ").");
        }

        void EnsureMainMenuStatusText()
        {
            if (statusText != null || mainMenuPanel == null)
                return;

            var statusGo = mainMenuPanel.transform.Find("MainMenuStatus");
            if (statusGo != null)
            {
                statusText = statusGo.GetComponent<TextMeshProUGUI>();
                if (statusText != null)
                    return;
            }

            var go = new GameObject("MainMenuStatus", typeof(RectTransform), typeof(TextMeshProUGUI));
            go.transform.SetParent(mainMenuPanel.transform, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.2f);
            rt.anchorMax = new Vector2(0.5f, 0.2f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(680f, 80f);
            rt.anchoredPosition = new Vector2(0f, 0f);

            statusText = go.GetComponent<TextMeshProUGUI>();
            statusText.fontSize = 16f;
            statusText.alignment = TextAlignmentOptions.Center;
            statusText.color = new Color(0.75f, 0.85f, 0.95f, 0.95f);
            statusText.raycastTarget = false;
            statusText.text = _statusMessage;
        }

        /// <summary>Shows connection errors on the main menu status line (not only the console).</summary>
        public void SetMainMenuStatus(string message)
        {
            _statusMessage = message ?? string.Empty;
            PushStatusToUi();
        }

        void CreateMainMenuButton(string name, string label, float y, UnityEngine.Events.UnityAction onClick)
        {
            Transform existing = mainMenuPanel.transform.Find(name);
            if (existing != null)
            {
                WireMainMenuButton(existing.gameObject, label, y, onClick);
                return;
            }

            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            WireMainMenuButton(go, label, y, onClick);
            go.transform.SetParent(mainMenuPanel.transform, false);
        }

        void WireMainMenuButton(GameObject go, string label, float y, UnityEngine.Events.UnityAction onClick)
        {
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = playButton != null
                ? playButton.GetComponent<RectTransform>().anchorMin
                : new Vector2(0.5f, 0.5f);
            rt.anchorMax = playButton != null
                ? playButton.GetComponent<RectTransform>().anchorMax
                : new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(320f, 44f);
            rt.anchoredPosition = new Vector2(0f, y);

            var image = go.GetComponent<Image>();
            if (image != null)
                image.color = new Color(0.11f, 0.17f, 0.28f, 0.98f);

            var textGo = go.transform.Find("Text")?.gameObject;
            if (textGo == null)
            {
                textGo = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
                textGo.transform.SetParent(go.transform, false);
            }

            var textRt = textGo.GetComponent<RectTransform>();
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.offsetMin = Vector2.zero;
            textRt.offsetMax = Vector2.zero;
            var tmp = textGo.GetComponent<TextMeshProUGUI>();
            tmp.text = label;
            tmp.fontSize = 20f;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;
            tmp.raycastTarget = false;

            var button = go.GetComponent<Button>();
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(onClick);
            button.interactable = true;
        }

        void WireExistingMainMenuButtons()
        {
            if (mainMenuPanel == null)
                return;

            float y = playButton != null
                ? playButton.GetComponent<RectTransform>().anchoredPosition.y - 56f
                : -170f;
            WireExistingMainMenuButton("BrowseGamesButton", "Join game", y, OnBrowseGamesClicked);
            y -= 56f;

            if (TitanOrbitMultiplayerConfig.ShowLocalPlayOptions)
            {
                if (!TitanOrbitPlayModeUtility.IsMppmAdditionalEditorInstance())
                    WireExistingMainMenuButton("LocalHostButton", "Local host", y, OnLocalHostClicked);
                if (!TitanOrbitPlayModeUtility.IsMppmAdditionalEditorInstance())
                    y -= 48f;
                WireExistingMainMenuButton("LocalClientButton", "Local client", y, OnLocalClientClicked);
            }
        }

        void WireExistingMainMenuButton(string name, string label, float y, UnityEngine.Events.UnityAction onClick)
        {
            var existing = mainMenuPanel.transform.Find(name);
            if (existing == null)
                return;
            WireMainMenuButton(existing.gameObject, label, y, onClick);
        }

        void OnBrowseGamesClicked()
        {
            if (_joinBrowser != null)
                _joinBrowser.Show();
        }

        void OnLocalHostClicked()
        {
            if (TitanOrbitSessionManager.Instance == null)
            {
                _statusMessage = "Session manager missing on NceGameRoot.";
                PushStatusToUi();
                Debug.LogError("[NceGameFlow] Local host: TitanOrbitSessionManager missing.");
                return;
            }

            if (!HasPlayableServerWorld())
            {
                _statusMessage = "No ServerWorld. Run Titan Orbit > Configure Multiplayer For Local Play (Client+Server).";
                PushStatusToUi();
                Debug.LogError("[NceGameFlow] Local host requires ServerWorld. server=" +
                               DescribeWorld(ClientServerBootstrap.ServerWorld));
                return;
            }

            if (!HasPlayableClientWorld())
            {
                _statusMessage = "No ClientWorld. Run Titan Orbit > Configure Multiplayer For Local Play.";
                PushStatusToUi();
                Debug.LogError("[NceGameFlow] Local host requires ClientWorld. client=" +
                               DescribeWorld(ClientServerBootstrap.ClientWorld));
                return;
            }

            Debug.Log("[NceGameFlow] Local host clicked — starting host + local client.");
            _statusMessage = "Starting local host...";
            PushStatusToUi();
            _autoStartSent = true;
            if (playButton != null)
                playButton.interactable = false;

            // Host and connect this window so team selection appears (not listen-only).
            TitanOrbitSessionManager.Instance.StartLocalPlay();
        }

        void PushStatusToUi()
        {
            if (statusText != null)
                statusText.text = _statusMessage;
        }

        void OnLocalClientClicked()
        {
            if (TitanOrbitSessionManager.Instance == null)
                return;
            TitanOrbitSessionManager.Instance.StartLocalClientForLanTest();
        }

        void EnsureEcsWorldVisualizer()
        {
            if (!TitanOrbitDedicatedServerAutoBoot.ShouldRunClientPresentation())
                return;

            if (GetComponent<EcsWorldVisualizer>() == null)
            {
                gameObject.AddComponent<EcsWorldVisualizer>();
                Debug.Log("[NceGameFlow] EcsWorldVisualizer added — GameObject proxies for ships, planets, gems.");
            }
        }

        void EnsureMatchFlowControllers()
        {
            if (!TitanOrbitDedicatedServerAutoBoot.ShouldRunClientPresentation())
                return;

            if (GetComponent<WorldFloatingCountManager>() == null)
                gameObject.AddComponent<WorldFloatingCountManager>();
            if (GetComponent<EcsFloatingCountPresenter>() == null)
                gameObject.AddComponent<EcsFloatingCountPresenter>();
            if (GetComponent<MatchEndScreenController>() == null)
                gameObject.AddComponent<MatchEndScreenController>();
            if (GetComponent<DeathScreenController>() == null)
                gameObject.AddComponent<DeathScreenController>();
        }

        async System.Threading.Tasks.Task PrimeGuestSessionAndPrefetchLobbiesAsync()
        {
            bool ok = await UnityGameServicesBootstrap.EnsureGuestSessionForOnlineAsync();
            Debug.Log("[NceGameFlow] UGS guest session ready=" + ok + " project=" + (Application.cloudProjectId ?? "(none)"));
            if (!ok)
            {
                _statusMessage = "Multiplayer services unavailable. Check internet and Unity project link.";
                return;
            }

            await PrefetchDedicatedLobbyCountAsync();
        }

        async System.Threading.Tasks.Task PrefetchDedicatedLobbyCountAsync()
        {
            try
            {
                var lobbies = await TitanOrbitLobbyService.QueryBrowsableDedicatedLobbiesAsync(40, skipEmptyStabilization: true);
                if (lobbies.Count > 0)
                {
                    _statusMessage = lobbies.Count + " dedicated match" + (lobbies.Count == 1 ? "" : "es") +
                                     " available — tap Join game.";
                    Debug.Log("[NceGameFlow] Menu lobby prefetch found " + lobbies.Count +
                              " browsable match(es); first=\"" + lobbies[0].Name + "\".");
                    return;
                }

                var kind = TitanOrbitLobbyService.LastOpenLobbyQueryKind;
                if (kind == TitanOrbitLobbyService.OpenLobbyQueryResultKind.RateLimitBackoff)
                {
                    _statusMessage = "Lobby list rate-limited — wait a moment, then tap Join game → Refresh.";
                    return;
                }

                if (kind != TitanOrbitLobbyService.OpenLobbyQueryResultKind.Ok)
                {
                    string detail = TitanOrbitLobbyService.LastOpenLobbyQueryErrorDetail;
                    _statusMessage = string.IsNullOrEmpty(detail)
                        ? "Could not query lobbies — tap Join game → Refresh."
                        : "Lobby query failed: " + detail;
                    return;
                }

                _statusMessage = "No dedicated matches listed yet — tap Join game → Request match or Refresh.";
                Debug.Log("[NceGameFlow] Menu lobby prefetch: zero browsable dedicated matches (see TitanOrbitLobbyService query log).");
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[NceGameFlow] Lobby prefetch failed: " + ex.Message);
            }
        }

        static void LogNetCodeWorldState()
        {
            var client = ClientServerBootstrap.ClientWorld;
            var server = ClientServerBootstrap.ServerWorld;
            var message = "[NceGameFlow] NetCode worlds at Start: client=" + DescribeWorld(client) +
                          " server=" + DescribeWorld(server);
#if UNITY_EDITOR
            message += ". PlayMode Type (NetCode prefs)=" + MultiplayerPlayModePreferences.RequestedPlayType;
#endif
            message += ". Use the main Editor Game tab; run Titan Orbit > Configure Multiplayer For Local Play (LAN) or Configure Multiplayer For Dedicated Server (UGS/Relay).";
            Debug.Log(message);
        }

        void Update()
        {
            // --- Per-frame refresh ---
            UpdateStatusFromSession();
            RefreshUi();
            TryAutoPickTeamInEditor();
        }

        void ResolveMissingReferences()
        {
            if (mainMenuPanel == null)
                mainMenuPanel = FindSceneObjectByName("MainMenuPanel");
            if (lobbyPanel == null)
                lobbyPanel = FindSceneObjectByName("LobbyPanel");
            if (teamSelectionPanel == null)
                teamSelectionPanel = FindSceneObjectByName("TeamSelectionPanel");
            if (loadingRoot == null)
            {
                var loading = GameObject.Find("LoadingScreenController");
                if (loading != null)
                    loadingRoot = loading;
            }

            EnsureLoadingScreen();
            if (gameplayRoot == null)
                gameplayRoot = GameObject.Find("HUD");
            if (shipStatsPanel == null)
                shipStatsPanel = FindSceneObjectByName("ShipStatsPanel");

            if (playButton == null)
            {
                var playGo = FindSceneObjectByName("PlayButton");
                if (playGo != null)
                    playButton = playGo.GetComponent<Button>();
            }

            if (statusText == null && mainMenuPanel != null)
            {
                var statusGo = mainMenuPanel.transform.Find("MainMenuStatus");
                if (statusGo != null)
                    statusText = statusGo.GetComponent<TextMeshProUGUI>();
            }

            if (teamAButton == null) teamAButton = FindJoinButton("TeamAPanel");
            if (teamBButton == null) teamBButton = FindJoinButton("TeamBPanel");
            if (teamCButton == null) teamCButton = FindJoinButton("TeamCPanel");
            if (teamDButton == null) teamDButton = FindJoinButton("TeamDPanel");
            if (teamEButton == null) teamEButton = FindJoinButton("TeamEPanel");

            _teamButtons = new[] { teamAButton, teamBButton, teamCButton, teamDButton, teamEButton };

            if (teamAPanel == null) teamAPanel = FindSceneObjectByName("TeamAPanel");
            if (teamBPanel == null) teamBPanel = FindSceneObjectByName("TeamBPanel");
            if (teamCPanel == null) teamCPanel = FindSceneObjectByName("TeamCPanel");
            if (teamDPanel == null) teamDPanel = FindSceneObjectByName("TeamDPanel");
            if (teamEPanel == null) teamEPanel = FindSceneObjectByName("TeamEPanel");

            _teamPanels = new[] { teamAPanel, teamBPanel, teamCPanel, teamDPanel, teamEPanel };
        }

        static GameObject FindSceneObjectByName(string objectName)
        {
            var transforms = UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < transforms.Length; i++)
            {
                var transform = transforms[i];
                if (transform.name != objectName)
                    continue;
                var scene = transform.gameObject.scene;
                if (!scene.IsValid() || !scene.isLoaded)
                    continue;
                return transform.gameObject;
            }

            return null;
        }

        bool EnsureTeamUiReferences()
        {
            bool hadMissing = false;
            for (int i = 0; i < TeamOrder.Length; i++)
            {
                if (_teamPanels[i] != null && _teamButtons[i] != null)
                    continue;

                hadMissing = true;
                ResolveMissingReferences();
                break;
            }

            for (int i = 0; i < TeamOrder.Length; i++)
            {
                if (_teamPanels[i] == null && _teamButtons[i] != null)
                    _teamPanels[i] = ResolveTeamPanelFromButton(_teamButtons[i]);
            }

            return hadMissing;
        }

        static GameObject ResolveTeamPanelFromButton(Button button)
        {
            if (button == null)
                return null;

            var transform = button.transform;
            while (transform != null)
            {
                if (transform.name.StartsWith("Team") && transform.name.EndsWith("Panel"))
                    return transform.gameObject;
                transform = transform.parent;
            }

            return null;
        }

        static Button FindJoinButton(string panelName)
        {
            var panel = FindSceneObjectByName(panelName);
            if (panel == null) return null;

            var join = panel.transform.Find("Content/JoinButton");
            if (join != null && join.TryGetComponent(out Button nested))
                return nested;

            foreach (var button in panel.GetComponentsInChildren<Button>(true))
            {
                if (button.gameObject.name == "JoinButton")
                    return button;
            }

            return null;
        }

        void WirePlayButton()
        {
            if (playButton == null) return;
            playButton.gameObject.SetActive(true);
            DisableChildRaycasts(playButton);
            EnsureMainMenuPlayButton();
            playButton.onClick.RemoveListener(OnPlayClicked);
            playButton.onClick.AddListener(OnPlayClicked);
        }

        void EnsureMainMenuPlayButton()
        {
            if (playButton == null) return;
            if (playButton.GetComponent<MainMenuPlayButton>() == null)
                playButton.gameObject.AddComponent<MainMenuPlayButton>();
        }

        void WireTeamButtons()
        {
            for (int i = 0; i < TeamOrder.Length; i++)
                WireTeamButton(_teamButtons[i], TeamOrder[i]);
        }

        static void WireTeamButton(Button button, TeamId team)
        {
            if (button == null) return;

            DisableChildRaycasts(button);

            var join = button.GetComponent<TeamJoinButton>();
            if (join == null)
                join = button.gameObject.AddComponent<TeamJoinButton>();
            join.Configure(team);

            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(join.JoinTeam);
        }

        static void DisableChildRaycasts(Button button)
        {
            foreach (var graphic in button.GetComponentsInChildren<Graphic>(true))
            {
                if (graphic.gameObject != button.gameObject)
                    graphic.raycastTarget = false;
            }
        }

        static void PickTeamStatic(TeamId team)
        {
            Debug.Log($"[NceGameFlow] PickTeam {team}.");
            if (TitanOrbitSessionManager.Instance == null)
            {
                Debug.LogError("[NceGameFlow] Cannot pick team: session manager missing.");
                return;
            }

            TitanOrbitSessionManager.Instance.RequestTeam(team);
        }

        IEnumerator WaitAndStartLocalPlay()
        {
            float deadline = Time.realtimeSinceStartup + 20f;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (HasPlayableClientWorld())
                {
                    TryStartLocalPlay();
                    yield break;
                }

                yield return null;
            }

            TryStartLocalPlay();
        }

        void OnPlayClicked()
        {
            TryStartLocalPlay();
        }

        void TryStartLocalPlay()
        {
            if (EcsGameBridge.IsNetworkInGame())
                return;

            if (TitanOrbitSessionManager.Instance == null)
            {
                _statusMessage = "Session manager missing. Run Titan Orbit → Setup NetCode Game (Full).";
                PushStatusToUi();
                Debug.LogError("[NceGameFlow] TitanOrbitSessionManager not found on NceGameRoot.");
                return;
            }

            if (!TitanOrbitMultiplayerConfig.ShowLocalPlayOptions)
            {
                _statusMessage = "Finding a dedicated match...";
                if (playButton != null)
                    playButton.interactable = false;
                QuickJoinDedicatedFromMenuAsync();
                return;
            }

            if (!HasPlayableClientWorld())
            {
                _statusMessage = "No ClientWorld. Run Titan Orbit > Configure Multiplayer For Local Play, then use the main Editor Game tab.";
                PushStatusToUi();
                Debug.LogError("[NceGameFlow] ClientWorld missing. client=" + DescribeWorld(ClientServerBootstrap.ClientWorld) +
                               " server=" + DescribeWorld(ClientServerBootstrap.ServerWorld) +
                               ". Run menu: Titan Orbit > Configure Multiplayer For Local Play. " +
                               "Use the main Editor Game tab (not a Server-only player window). " +
                               "Check the ▾ dropdown next to the Play button — pick Default or Client+Server for Main Editor.");
                return;
            }

            if (!TitanOrbitPlayModeUtility.IsMppmAdditionalEditorInstance() && !HasPlayableServerWorld())
            {
                _statusMessage = "No ServerWorld. Run Titan Orbit > Configure Multiplayer For Local Play (Client+Server).";
                Debug.LogError("[NceGameFlow] Local play requires ServerWorld. server=" +
                               DescribeWorld(ClientServerBootstrap.ServerWorld));
                return;
            }

            _statusMessage = TitanOrbitPlayModeUtility.IsMppmAdditionalEditorInstance()
                ? "Connecting to host..."
                : "Connecting to game server...";
            if (playButton != null)
                playButton.interactable = false;

            if (TitanOrbitPlayModeUtility.IsMppmAdditionalEditorInstance())
                TitanOrbitSessionManager.Instance.StartLocalClientForLanTest();
            else
                TitanOrbitSessionManager.Instance.StartLocalPlay();
            _autoStartSent = true;
        }

        async void QuickJoinDedicatedFromMenuAsync()
        {
            try
            {
                bool ok = await TitanOrbitSessionManager.Instance.QuickJoinDedicatedAsync();
                if (!ok)
                {
                    _statusMessage = TitanOrbitSessionManager.Instance.LastStatusMessage ?? "Quick join failed. Try Join game.";
                    return;
                }

                _statusMessage = "Connecting to dedicated server...";
                float deadline = Time.realtimeSinceStartup + 65f;
                while (Time.realtimeSinceStartup < deadline)
                {
                    if (TitanOrbitSessionManager.Instance.IsInGame && EcsGameBridge.IsNetworkInGame())
                    {
                        _statusMessage = TitanOrbitSessionManager.Instance.LastStatusMessage;
                        return;
                    }

                    if (!TitanOrbitSessionManager.IsDedicatedJoinConnecting)
                    {
                        _statusMessage = TitanOrbitSessionManager.Instance.LastStatusMessage ?? "Connection failed.";
                        return;
                    }

                    await Task.Yield();
                }

                _statusMessage = TitanOrbitSessionManager.Instance.LastStatusMessage ?? "Connection timed out.";
            }
            finally
            {
                if (playButton != null)
                    playButton.interactable = true;
            }
        }

        static bool HasPlayableClientWorld()
        {
            var client = ClientServerBootstrap.ClientWorld;
            if (client == null || !client.IsCreated)
                return false;
            return client.EntityManager.CreateEntityQuery(typeof(NetworkStreamDriver)).CalculateEntityCount() > 0;
        }

        static bool HasPlayableServerWorld()
        {
            var server = ClientServerBootstrap.ServerWorld;
            if (server == null || !server.IsCreated)
                return false;
            return server.EntityManager.CreateEntityQuery(typeof(NetworkStreamDriver)).CalculateEntityCount() > 0;
        }

        static string DescribeWorld(World world)
        {
            if (world == null) return "null";
            if (!world.IsCreated) return world.Name + "(disposed)";
            return world.Name;
        }

        void UpdateStatusFromSession()
        {
            var session = TitanOrbitSessionManager.Instance;

            if (IsInGameFlow() && IsMapReadyForTeamSelection() && !EcsGameBridge.HasLocalPlayerShip() &&
                !ClientTeamFlowState.IsRejoinChoicePending)
            {
                _statusMessage = "Choose a team.";
                if (playButton != null)
                    playButton.interactable = true;
                return;
            }

            if (session != null && !string.IsNullOrEmpty(session.LastStatusMessage))
                _statusMessage = session.LastStatusMessage;

            if (IsInGameFlow())
            {
                if (playButton != null)
                    playButton.interactable = true;
                return;
            }

            if (_autoStartSent && session != null && !session.IsInGame)
                _statusMessage = "Still connecting... check Console for errors. Click Play to retry.";
        }

        void PickTeam(TeamId team) => PickTeamStatic(team);

        void TryAutoPickTeamInEditor()
        {
#if UNITY_SERVER
            return;
#endif
            // MPPM additional players choose their team manually.
            if (TitanOrbitPlayModeUtility.IsMppmAdditionalEditorInstance())
                return;

            if (!Application.isEditor || _autoPickSent || !autoPickTeamAInEditor)
                return;

            if (ClientTeamFlowState.IsRejoinChoicePending || ClientTeamFlowState.ChoseStartFreshShip)
                return;

            if (!IsInGameFlow() || !EcsGameBridge.IsMapLoadingComplete())
                return;
            if (!EcsGameBridge.TryGetActiveTeamCount(out _))
                return;
            if (EcsGameBridge.TryGetLocalShipPosition(out _))
                return;
            if (ClientTeamFlowState.TeamChoiceConfirmed)
                return;

            if (_connectedAt < 0f)
                _connectedAt = Time.time;

            if (Time.time - _connectedAt < autoPickDelaySeconds)
                return;

            _autoPickSent = true;
            PickTeam(TeamId.TeamA);
        }

        bool IsMapReadyForTeamSelection()
        {
            return EcsGameBridge.IsMapLoadingComplete();
        }

        bool IsInGameFlow() => EcsGameBridge.IsNetworkInGame();

        void RefreshUi()
        {
            if (TitanOrbitPlayModeUtility.IsMppmAdditionalEditorInstance() && IsInGameFlow() && _mppmConnectedSince < 0f)
                _mppmConnectedSince = Time.time;

            bool connectingDedicated = TitanOrbitSessionManager.IsDedicatedJoinConnecting;
            bool connected = IsInGameFlow();
            if (TitanOrbitSessionManager.IsDedicatedOnlineClient && connected && _dedicatedConnectedAt < 0f)
                _dedicatedConnectedAt = Time.time;
            if (!connected && !connectingDedicated)
                _dedicatedConnectedAt = -1f;

            bool mapLoaded = connected && IsMapReadyForTeamSelection();

            ShipState rejoinShipState = default;
            bool hasRejoinableShip = connected &&
                                     EcsGameBridge.TryGetRejoinableShipForLocalPlayer(out rejoinShipState);
            // Only evaluate rejoin once basic map readiness is met — stale ships during galaxy build must not block flow.
            if (mapLoaded)
                ClientTeamFlowState.TryNotifyRejoinableShip(hasRejoinableShip);

            int activeTeamsForUi = 0;
            // [TITAN-ORBIT] Team count from replicated home planets / TeamStateSingleton — required before team picker.
            bool knowsTeamCount = connected && mapLoaded &&
                                  EcsGameBridge.TryGetActiveTeamCount(out activeTeamsForUi) &&
                                  activeTeamsForUi > 0;

            // Full map sync only while the rejoin prompt is actually pending (returning player resume).
            bool requireFullMapLoad = ClientTeamFlowState.IsRejoinChoicePending;
            // [NETCODE] Dedicated Docker/cloud joins: keep loading until team count replicates — avoids empty galaxy with no picker.
            bool mapReady = connected && (requireFullMapLoad
                ? EcsGameBridge.IsMapLoadingComplete()
                : mapLoaded && (!TitanOrbitSessionManager.IsDedicatedOnlineClient || knowsTeamCount));
            bool hasShip = connected && EcsGameBridge.HasLocalPlayerShip();
            bool teamConfirmed = ClientTeamFlowState.TeamChoiceConfirmed;
            bool teamPickInFlight = ClientTeamFlowState.HasRequestedTeamPick && !teamConfirmed;
            bool showRejoinChoice = connected && mapReady && hasRejoinableShip &&
                                    ClientTeamFlowState.IsRejoinChoicePending &&
                                    !teamConfirmed && !ClientTeamFlowState.HasRequestedTeamPick;
            bool allowTeamPick = ClientTeamFlowState.ChoseStartFreshShip ||
                                 (!ClientTeamFlowState.IsRejoinChoicePending &&
                                  !ClientTeamFlowState.HasRequestedTeamPick &&
                                  !hasRejoinableShip);
            bool showLoading = connected && !mapReady &&
                               !ClientTeamFlowState.TeamChoiceConfirmed &&
                               !ClientTeamFlowState.HasRequestedTeamPick;
            bool showTeamCountWait = connected && mapReady && allowTeamPick && !hasShip && !teamConfirmed &&
                                     !teamPickInFlight && !knowsTeamCount;
            bool showTeam = connected && mapReady && allowTeamPick && !hasShip && !teamConfirmed &&
                            !teamPickInFlight && knowsTeamCount && !showRejoinChoice;
            bool showSpawnWait = connected && (teamConfirmed || teamPickInFlight) && !hasShip && !showRejoinChoice;

            if (TitanOrbitPlayModeUtility.IsMppmAdditionalEditorInstance() && connected && !mapReady && !_loggedWaitingForMap)
            {
                _loggedWaitingForMap = true;
                Debug.Log("[NceGameFlow] MPPM Player " + TitanOrbitPlayModeUtility.GetMppmPlayerNumber() +
                          " connected — waiting for host. Start the match on the Main Editor (Play → pick a team).");
            }

            if (showTeam && TitanOrbitPlayModeUtility.IsMppmAdditionalEditorInstance() && !_loggedTeamUiReady)
            {
                _loggedTeamUiReady = true;
                Debug.Log("[NceGameFlow] MPPM Player " + TitanOrbitPlayModeUtility.GetMppmPlayerNumber() +
                          " — team selection ready. Click Join on any team.");
            }

            if (mainMenuPanel != null)
                mainMenuPanel.SetActive(!connected && !connectingDedicated &&
                                      (_joinBrowser == null || !_joinBrowser.IsVisible));

            if (showRejoinChoice && hasRejoinableShip && _rejoinChoice != null)
                _rejoinChoice.Show(rejoinShipState);
            else if (_rejoinChoice != null && _rejoinChoice.IsVisible)
            {
                _rejoinChoice.Hide();
            }

            if (playButton != null && mainMenuPanel != null && mainMenuPanel.activeSelf)
                playButton.gameObject.SetActive(true);

            if (statusText != null)
            {
                if (!connected || showLoading || showRejoinChoice || showTeam || showTeamCountWait || showSpawnWait || connectingDedicated)
                    statusText.text = connectingDedicated && !connected
                        ? "Connecting to dedicated server..."
                        : !connected
                        ? _statusMessage
                        : showLoading
                            ? TitanOrbitSessionManager.IsDedicatedOnlineClient
                                ? "Syncing map from dedicated server..."
                                : "Building galaxy..."
                            : showRejoinChoice
                                ? "You have a ship in this match."
                            : showTeamCountWait
                                ? "Preparing teams..."
                            : showTeam
                                ? "Choose a team to spawn your ship."
                            : showSpawnWait
                                ? "Spawning your ship..."
                                : "Choose a team.";
            }

            // Loading screen is shown alone; lobby backdrop covers team pick and spawn wait.
            if (lobbyPanel != null)
                lobbyPanel.SetActive((showTeam || showTeamCountWait || showSpawnWait) && !showRejoinChoice);
            if (teamSelectionPanel != null)
                teamSelectionPanel.SetActive(showTeam);

            if (loadingRoot != null)
                loadingRoot.SetActive(showLoading || (connectingDedicated && !connected));
            if (_loadingScreen != null)
            {
                if (showLoading || (connectingDedicated && !connected))
                    _loadingScreen.Show();
                else
                    _loadingScreen.Hide();
            }

            if (showTeam)
            {
                if (EnsureTeamUiReferences())
                    WireTeamButtons();
                EnsureUniformTeamPanelWidths();
                ApplyActiveTeamVisibility(activeTeamsForUi);
                SetTeamButtonsInteractable(true, activeTeamsForUi);
            }
            else if (!connected)
            {
                RestoreTeamPanelsForNextSelection();
                SetTeamButtonsInteractable(false, MapGenerationLogic.MaxSupportedTeams);
            }
            else
            {
                SetTeamButtonsInteractable(false, MapGenerationLogic.MaxSupportedTeams);
            }

            bool matchWon = EcsGameBridge.TryGetMatchState(out var match) && match.WinningTeam != TeamId.None;
            bool showGameplayHud = connected && mapReady && hasShip && !showRejoinChoice &&
                                   !ClientTeamFlowState.IsRejoinChoicePending && !matchWon;

            if (gameplayRoot != null)
                gameplayRoot.SetActive(showGameplayHud);

            if (shipStatsPanel != null)
                shipStatsPanel.SetActive(showGameplayHud);
        }

        void EnsureLoadingScreen()
        {
            if (_loadingScreen != null)
                return;

            if (loadingRoot != null)
                _loadingScreen = loadingRoot.GetComponent<LoadingScreenControllerNce>();

            if (_loadingScreen == null)
            {
                var loadingGo = GameObject.Find("LoadingScreenController");
                if (loadingGo != null)
                {
                    loadingRoot = loadingGo;
                    _loadingScreen = loadingGo.GetComponent<LoadingScreenControllerNce>();
                    if (_loadingScreen == null)
                        _loadingScreen = loadingGo.AddComponent<LoadingScreenControllerNce>();
                }
            }
        }

        void CleanupJoinTeamScreenUi()
        {
            if (_joinTeamUiCleaned)
                return;

            _joinTeamUiCleaned = true;

            if (lobbyPanel == null)
                return;

            HideChildIfPresent(lobbyPanel.transform, "PlayerCount");
            HideChildIfPresent(lobbyPanel.transform, "RoomName");
            HideChildIfPresent(lobbyPanel.transform, "TeamStatus");
        }

        static void HideChildIfPresent(Transform parent, string childName)
        {
            var child = parent.Find(childName);
            if (child != null)
                child.gameObject.SetActive(false);
        }

        void EnsureUniformTeamPanelWidths()
        {
            if (_teamPanelWidthsConfigured || teamSelectionPanel == null)
                return;

            var layout = teamSelectionPanel.GetComponent<HorizontalLayoutGroup>();
            if (layout == null)
                return;

            var containerRect = teamSelectionPanel.GetComponent<RectTransform>();
            if (containerRect == null)
                return;

            Canvas.ForceUpdateCanvases();
            float containerWidth = containerRect.rect.width;
            if (containerWidth <= 0f)
                return;

            int maxTeams = MapGenerationLogic.MaxSupportedTeams;
            float padding = layout.padding.horizontal;
            float spacingTotal = layout.spacing * Mathf.Max(0, maxTeams - 1);
            float panelWidth = (containerWidth - padding - spacingTotal) / maxTeams;
            if (panelWidth <= 0f)
                return;

            layout.childForceExpandWidth = false;

            for (int i = 0; i < TeamOrder.Length; i++)
            {
                var panel = _teamPanels[i] ?? ResolveTeamPanelFromButton(_teamButtons[i]);
                if (panel == null)
                    continue;

                var layoutElement = panel.GetComponent<LayoutElement>();
                if (layoutElement == null)
                    layoutElement = panel.AddComponent<LayoutElement>();

                layoutElement.preferredWidth = panelWidth;
                layoutElement.flexibleWidth = 0f;
            }

            _teamPanelWidthsConfigured = true;
        }

        void ApplyActiveTeamVisibility(int activeTeamCount)
        {
            for (int i = 0; i < TeamOrder.Length; i++)
            {
                bool inMatch = activeTeamCount > 0 && (int)TeamOrder[i] <= activeTeamCount;
                var panel = _teamPanels[i] ?? ResolveTeamPanelFromButton(_teamButtons[i]);
                if (panel != null)
                    panel.SetActive(inMatch);
                else if (_teamButtons[i] != null)
                    _teamButtons[i].gameObject.SetActive(inMatch);
            }

            if (teamSelectionPanel != null)
                LayoutRebuilder.ForceRebuildLayoutImmediate(teamSelectionPanel.GetComponent<RectTransform>());
        }

        void RestoreTeamPanelsForNextSelection()
        {
            for (int i = 0; i < TeamOrder.Length; i++)
            {
                if (_teamPanels[i] != null)
                    _teamPanels[i].SetActive(true);
                else if (_teamButtons[i] != null)
                    _teamButtons[i].gameObject.SetActive(true);
            }
        }

        void SetTeamButtonsInteractable(bool interactable, int activeTeamCount)
        {
            for (int i = 0; i < TeamOrder.Length; i++)
            {
                if (_teamButtons[i] == null)
                    continue;

                bool teamActive = activeTeamCount > 0 && (int)TeamOrder[i] <= activeTeamCount;
                _teamButtons[i].interactable = interactable && teamActive;
            }
        }
    }
}
