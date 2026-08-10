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
        string _statusMessage = "Join a match or start local play.";
        bool _mainMenuButtonsBuilt;
        bool _initialized;

        /// <summary>
        /// [TITAN-ORBIT] Once the local ship has been seen this session, keep gameplay HUD up even if
        /// <see cref="EcsGameBridge.HasLocalPlayerShip"/> briefly returns false during GhostSpawnBacklog
        /// (gem Instantiates after asteroid destroy). Otherwise lobby overlay + HUD hide = black blink.
        /// </summary>
        bool _latchedHasShipThisSession;

        /// <summary>
        /// [TITAN-ORBIT] Realtime when Join Team latched <see cref="ClientTeamFlowState.HasRequestedTeamPick"/>.
        /// Used to detect lost RequestTeam / missing TeamChoiceResult (Editor.log hang 2026-07-30).
        /// </summary>
        float _teamPickRequestedAt = -1f;

        /// <summary>How many automatic RequestTeam resends we have attempted this pick.</summary>
        int _teamPickRetryCount;

        /// <summary>Seconds to wait for TeamChoiceResult before clearing spawn-wait and retrying.</summary>
        const float TeamPickResultTimeoutSeconds = 3f;

        /// <summary>Max automatic retries after a TeamChoiceResult timeout (then re-enable manual pick).</summary>
        const int TeamPickMaxAutoRetries = 2;

        /// <summary>
        /// Shown on the team panel after spawn-wait timeouts exhaust auto-retries.
        /// Cleared when the player picks again or Confirm arrives.
        /// </summary>
        string _teamPickTimeoutHint;

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

            // --- Visual refresh (logo, account bar, stacked buttons, clear placeholder BG) ---
            // [TITAN-ORBIT] MainMenuPresenter owns layout so bootstrap + flow stay in sync.
            MainMenuPresenter.Apply(
                mainMenuPanel,
                playButton,
                OnBrowseGamesClicked,
                OnLocalClientClicked,
                out var presentedStatus);
            if (presentedStatus != null)
                statusText = presentedStatus;

            if (statusText != null && string.IsNullOrEmpty(statusText.text))
                statusText.text = _statusMessage;

            _mainMenuButtonsBuilt = true;
            Debug.Log("[NceGameFlow] Main menu presented (logo, account, Join game" +
                      (TitanOrbitMultiplayerConfig.ShowLocalPlayOptions ? ", Local client" : "") + ").");
        }

        /// <summary>Shows connection errors on the main menu status line (not only the console).</summary>
        public void SetMainMenuStatus(string message)
        {
            _statusMessage = message ?? string.Empty;
            PushStatusToUi();
        }

        void WireExistingMainMenuButtons()
        {
            if (mainMenuPanel == null)
                return;

            // Re-apply presenter so retries after first frame still fix orphan layout / missing account bar.
            MainMenuPresenter.Apply(
                mainMenuPanel,
                playButton,
                OnBrowseGamesClicked,
                OnLocalClientClicked,
                out var presentedStatus);
            if (presentedStatus != null)
                statusText = presentedStatus;
        }

        void OnBrowseGamesClicked()
        {
            if (_joinBrowser != null)
                _joinBrowser.Show();
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
                PushStatusToUi();
                return;
            }

            await PrefetchDedicatedLobbyCountAsync();
            PushStatusToUi();
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

                // --- Empty list: show query failure or "no matches yet" ---
                var kind = TitanOrbitLobbyService.LastOpenLobbyQueryKind;
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
            // [TITAN-ORBIT] Do NOT AddListener(OnPlayClicked) — MainMenuPlayButton owns onClick alone.
            // Previously both IPointerClick + this listener double-fired Local play / Quick join.
            playButton.onClick.RemoveListener(OnPlayClicked);
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

        /// <summary>
        /// [TITAN-ORBIT] If Join Team was clicked but <see cref="TeamChoiceResultRpc"/> never arrives,
        /// clear the in-flight latch and retry (then re-enable buttons). Prevents soft-lock on
        /// "Spawning your ship..." when RequestTeam is lost under Local Host IPC load.
        /// Does <b>not</b> fire while Confirm is deferred through the post–TeamChoice Instantiates
        /// hold — that is a successful result waiting for Crash!!!-safe unlock, not a lost RPC.
        /// </summary>
        /// <param name="teamPickInFlight">True while pick requested and not yet confirmed.</param>
        /// <param name="teamConfirmed">True after deferred Confirm flushed.</param>
        void TickTeamPickTimeoutWatchdog(bool teamPickInFlight, bool teamConfirmed)
        {
            // --- Success / idle / Instantiates-hold defer: reset or wait without retry ---
            // [TITAN-ORBIT] Deferred Confirm keeps TeamChoiceConfirmed false for ~PostTeamChoiceHold
            // frames after a successful TeamChoiceResult. Treating that as a lost RPC would
            // ClearTeamPickRequest + re-send RequestTeam while the ship is Instantiating.
            if (teamConfirmed ||
                !teamPickInFlight ||
                ClientTeamFlowState.HasDeferredTeamChoiceConfirmPending)
            {
                _teamPickRequestedAt = -1f;
                if (teamConfirmed || !IsInGameFlow())
                {
                    _teamPickRetryCount = 0;
                    if (teamConfirmed)
                        _teamPickTimeoutHint = null;
                }
                return;
            }

            // --- Rising edge of in-flight pick ---
            if (_teamPickRequestedAt < 0f)
            {
                _teamPickRequestedAt = Time.realtimeSinceStartup;
                _teamPickTimeoutHint = null;
                return;
            }

            if (Time.realtimeSinceStartup - _teamPickRequestedAt < TeamPickResultTimeoutSeconds)
                return;

            // --- Timeout: no TeamChoiceResult within window ---
            // Clear the optimistic latch so UI can leave "Spawning..." (caller recomputes flags).
            var team = ClientTeamFlowState.LastRequestedTeam;
            ClientTeamFlowState.ClearTeamPickRequest();
            _teamPickRequestedAt = -1f;

            if (_teamPickRetryCount < TeamPickMaxAutoRetries &&
                team != TeamId.None &&
                TitanOrbitSessionManager.Instance != null)
            {
                _teamPickRetryCount++;
                Debug.LogWarning(
                    $"[NceGameFlow] TeamChoiceResult timed out after {TeamPickResultTimeoutSeconds:0.#}s — " +
                    $"auto-retry RequestTeam {team} (attempt {_teamPickRetryCount}/{TeamPickMaxAutoRetries}).");
                // RequestTeam re-latches HasRequestedTeamPick — spawn-wait continues.
                TitanOrbitSessionManager.Instance.RequestTeam(team);
                return;
            }

            // --- Exhausted auto-retries: return player to team buttons ---
            _teamPickRetryCount = 0;
            _teamPickTimeoutHint = "Team join timed out — click a team again.";
            _statusMessage = _teamPickTimeoutHint;
            Debug.LogError(
                "[NceGameFlow] TeamChoiceResult timed out after retries. " +
                "RequestTeam may have been lost; choose a team again.");
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

            // [TITAN-ORBIT] Main Editor: ServerWorld is created in StartLocalPlay (not at Play enter).
            // MPPM additional instances are client-only and join the host.

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
            // [TITAN-ORBIT] Team count from MapSessionMeta / homes — required before team picker UI.
            bool knowsTeamCount = connected &&
                                  EcsGameBridge.TryGetActiveTeamCount(out activeTeamsForUi) &&
                                  activeTeamsForUi > 0;

            // --- Map ready vs team ready (do not conflate) ---
            // [TITAN-ORBIT] Old dedicated path: mapReady required knowsTeamCount, but knowsTeamCount
            // also required mapLoaded — and showTeamCountWait required mapReady && !knowsTeamCount
            // (impossible). Windows clients sat on "Queuing map visuals... N/N" forever even at 100%.
            // Loading dismisses when the map heuristic completes; "Preparing teams..." covers meta lag.
            bool mapReady = connected && mapLoaded;
            bool hasShipLive = connected && EcsGameBridge.HasLocalPlayerShip();
            if (!connected)
                _latchedHasShipThisSession = false;
            else if (hasShipLive)
                _latchedHasShipThisSession = true;

            // Prefer live detect; latch covers GhostSpawnBacklog false-negatives mid-combat.
            bool hasShip = hasShipLive || _latchedHasShipThisSession;
            bool teamConfirmed = ClientTeamFlowState.TeamChoiceConfirmed;
            bool teamPickInFlight = ClientTeamFlowState.HasRequestedTeamPick && !teamConfirmed;

            // --- Spawn-wait watchdog (lost RequestTeam / missing TeamChoiceResult) ---
            // [TITAN-ORBIT] Failed sessions: RequestTeam logged, no TeamManagement spawn, UI stuck on
            // "Spawning your ship..." forever because HasRequestedTeamPick never cleared. Retry then
            // re-enable team buttons so the player is not soft-locked.
            // Run BEFORE showTeam / showSpawnWait so ClearTeamPickRequest / auto-retry re-latch
            // are reflected in the same frame (otherwise buttons stay disabled one frame late,
            // and final timeout still painted "Spawning your ship...").
            TickTeamPickTimeoutWatchdog(teamPickInFlight, teamConfirmed);
            teamPickInFlight = ClientTeamFlowState.HasRequestedTeamPick && !teamConfirmed;

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
            // Do not re-enter spawn-wait lobby after we already had a ship (would flash dark overlay).
            bool showSpawnWait = connected && (teamConfirmed || teamPickInFlight) && !hasShip &&
                                 !showRejoinChoice && !_latchedHasShipThisSession;

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
                                ? (!string.IsNullOrEmpty(_teamPickTimeoutHint)
                                    ? _teamPickTimeoutHint
                                    : "Choose a team to spawn your ship.")
                            : showSpawnWait
                                ? "Spawning your ship..."
                                : "Choose a team.";
            }

            // Loading screen alone while map builds; LobbyPanel dim covers team pick / spawn wait
            // (TeamSelectionPanel itself draws no container fill — only the team cards with space art).
            if (lobbyPanel != null)
                lobbyPanel.SetActive((showTeam || showTeamCountWait || showSpawnWait) && !showRejoinChoice);
            if (teamSelectionPanel != null)
                teamSelectionPanel.SetActive(showTeam);
            // Ensure nebula backgrounds are on the cards when Join Team becomes visible.
            if (showTeam)
                CleanupJoinTeamScreenUi();

            // --- Loading Map owns the screen ---
            // [TITAN-ORBIT] Show loading while Relay connect is in flight OR while the map is
            // still streaming. Join Game must be dismissed first — otherwise the lobby list stays
            // active under a semi-transparent loading panel.
            bool showLoadingOverlay = showLoading || (connectingDedicated && !connected);
            if (showLoadingOverlay && _joinBrowser != null && _joinBrowser.IsVisible)
                _joinBrowser.DismissForLoading();

            if (loadingRoot != null)
                loadingRoot.SetActive(showLoadingOverlay);
            if (_loadingScreen != null)
            {
                if (showLoadingOverlay)
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
                // --- Live panel stats (roster / home gems / planets) ---
                // [TITAN-ORBIT] Scene placeholders stay at 0 until this binder runs each frame.
                JoinTeamPanelStatsBinder.Refresh(_teamPanels, activeTeamsForUi);
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

        /// <summary>
        /// Resources path for the Join Team card nebula (same art as the world SpaceBackground).
        /// Copied under Resources so player builds can <see cref="Resources.Load{T}"/> it.
        /// </summary>
        const string TeamPanelSpaceSpriteResourcesPath = "UI/Backgrounds/SpaceBackground";

        /// <summary>Cached nebula sprite for TeamA…TeamE panel Images (loaded once).</summary>
        static Sprite s_TeamPanelSpaceSprite;

        void CleanupJoinTeamScreenUi()
        {
            // --- One-time chrome (legacy labels + LobbyPanel / container fills) ---
            if (!_joinTeamUiCleaned)
            {
                _joinTeamUiCleaned = true;

                if (lobbyPanel != null)
                {
                    HideChildIfPresent(lobbyPanel.transform, "PlayerCount");
                    HideChildIfPresent(lobbyPanel.transform, "RoomName");
                    HideChildIfPresent(lobbyPanel.transform, "TeamStatus");
                }

                // [TITAN-ORBIT] LobbyPanel: soft transparent dim (Main Menu style).
                // TeamSelectionPanel: no fill (layout only).
                ApplyJoinTeamSpaceBackdrop(lobbyPanel);
                HideJoinTeamContainerBackground(teamSelectionPanel);
            }

            // --- Team cards: always refresh nebula fill + TitleBar accent ---
            // Cheap, and lets palette tweaks apply when Join Team becomes visible again.
            if (_teamPanels != null)
            {
                for (int i = 0; i < _teamPanels.Length; i++)
                    ApplyTeamPanelVisuals(_teamPanels[i]);
            }
        }

        /// <summary>Hides a direct child by name when present (legacy lobby labels).</summary>
        static void HideChildIfPresent(Transform parent, string childName)
        {
            var child = parent.Find(childName);
            if (child != null)
                child.gameObject.SetActive(false);
        }

        /// <summary>
        /// Full-screen Join Team dim — same soft navy tint as Main Menu / Join Game so the
        /// scrolling world <c>SpaceBackground</c> still reads behind the UI.
        /// </summary>
        /// <param name="root">Usually <c>LobbyPanel</c>; null-safe no-op.</param>
        static void ApplyJoinTeamSpaceBackdrop(GameObject root)
        {
            if (root == null)
                return;
            if (!root.TryGetComponent<Image>(out var image))
                return;

            // [TITAN-ORBIT] Shared helper — identical color/alpha to Main Menu & Join Game overlays.
            MainMenuPresenter.ApplyTransparentMenuBackdrop(image);
            image.type = Image.Type.Simple;
            image.preserveAspect = false;
        }

        /// <summary>
        /// TitleBar fill alpha when painted with the canonical team color.
        /// Opaque enough to read as a solid accent strip; text still sits on top.
        /// </summary>
        const float TeamPanelAccentBarAlpha = 0.92f;

        /// <summary>
        /// Soft darken on the nebula sprite itself (multiply grey) so labels stay readable.
        /// Team hue is NOT applied here — see <see cref="TeamPanelTintOverlayAlpha"/>.
        /// </summary>
        const float TeamPanelNebulaScrim = 0.12f;

        /// <summary>
        /// Alpha of the solid team-color overlay drawn over the nebula.
        /// [UGUI] Multiply-tinting Image.color on a blue-heavy SpaceBackground barely shows
        /// red/green/orange — a semi-transparent solid overlay actually reads as team tint.
        /// </summary>
        const float TeamPanelTintOverlayAlpha = 0.05f;

        /// <summary>Runtime child name for the team-color wash over each card nebula.</summary>
        const string TeamPanelTintOverlayName = "TeamTintOverlay";

        /// <summary>
        /// Applies Join Team card visuals: nebula fill + solid team wash overlay, TitleBar accent,
        /// and matching Outline. Overlay (not Image multiply) is what makes the faction tint visible.
        /// </summary>
        /// <param name="root">TeamAPanel…TeamEPanel root; null-safe no-op.</param>
        static void ApplyTeamPanelVisuals(GameObject root)
        {
            if (root == null)
                return;

            // --- Resolve faction RGB once (panel name → TeamId palette) ---
            if (!TryGetTeamColorForPanel(root, out Color teamRgb))
            {
                // Fallback: keep scene-authored Image RGB if the GO was renamed.
                if (root.TryGetComponent<Image>(out var fallbackImage))
                    teamRgb = new Color(fallbackImage.color.r, fallbackImage.color.g, fallbackImage.color.b, 1f);
                else
                    teamRgb = Color.white;
            }

            ApplyTeamPanelSpaceBackgroundImage(root);
            ApplyTeamPanelTintOverlay(root, teamRgb);
            ApplyTeamPanelAccentBar(root, teamRgb);
            ApplyTeamPanelOutline(root, teamRgb);
        }

        /// <summary>
        /// Puts the shared space nebula sprite on the team card root Image with a near-neutral
        /// multiply so art stays bright. Team color comes from <see cref="ApplyTeamPanelTintOverlay"/>.
        /// </summary>
        /// <param name="root">TeamAPanel…TeamEPanel root; null-safe no-op.</param>
        static void ApplyTeamPanelSpaceBackgroundImage(GameObject root)
        {
            if (root == null)
                return;
            if (!root.TryGetComponent<Image>(out var image))
                return;

            Sprite space = ResolveTeamPanelSpaceSprite();
            if (space == null)
            {
                Debug.LogWarning(
                    "[NceGameFlow] Team panel space sprite missing at Resources/" +
                    TeamPanelSpaceSpriteResourcesPath +
                    ". Team cards will keep solid color fills.");
                return;
            }

            // [UNITY] Keep sprite multiply near-white — do not bake team hue into Image.color.
            image.sprite = space;
            image.type = Image.Type.Simple;
            image.preserveAspect = false;
            image.raycastTarget = true;

            float grey = 1f - TeamPanelNebulaScrim;
            image.color = new Color(grey, grey, grey, 1f);
        }

        /// <summary>
        /// Ensures a full-rect child Image behind Content that paints a semi-transparent solid
        /// team color over the nebula. This is the visible "panel tint" players expect.
        /// </summary>
        /// <param name="root">TeamAPanel…TeamEPanel root.</param>
        /// <param name="teamRgb">Canonical team RGB (alpha ignored; we set overlay alpha).</param>
        static void ApplyTeamPanelTintOverlay(GameObject root, Color teamRgb)
        {
            if (root == null)
                return;

            // --- Find or create the wash layer ---
            Transform existing = root.transform.Find(TeamPanelTintOverlayName);
            GameObject overlayGo;
            if (existing != null)
            {
                overlayGo = existing.gameObject;
            }
            else
            {
                // [UNITY] New UI child under the panel; drawn above root Image, below Content.
                overlayGo = new GameObject(TeamPanelTintOverlayName, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                overlayGo.layer = root.layer;
                overlayGo.transform.SetParent(root.transform, false);
            }

            // Sibling 0 = first drawn after the panel's own Image (Content stays on top for text/buttons).
            overlayGo.transform.SetSiblingIndex(0);

            var rect = overlayGo.GetComponent<RectTransform>();
            // Stretch to the full card so the wash matches the nebula rect.
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.localScale = Vector3.one;

            // If a parent LayoutGroup ever appears, keep this decorative layer out of layout math.
            if (!overlayGo.TryGetComponent<LayoutElement>(out var layoutElement))
                layoutElement = overlayGo.AddComponent<LayoutElement>();
            layoutElement.ignoreLayout = true;

            var overlayImage = overlayGo.GetComponent<Image>();
            // Solid fill (no sprite) so alpha blends the true team RGB over the nebula.
            overlayImage.sprite = null;
            overlayImage.type = Image.Type.Simple;
            overlayImage.preserveAspect = false;
            overlayImage.raycastTarget = false;

            Color wash = teamRgb;
            wash.a = TeamPanelTintOverlayAlpha;
            overlayImage.color = wash;
        }

        /// <summary>
        /// Paints Content/TitleBar with the solid team color — the strong "team colour bar"
        /// on top of the softer tinted nebula fill.
        /// </summary>
        /// <param name="root">TeamAPanel…TeamEPanel root.</param>
        /// <param name="teamRgb">Canonical team RGB (alpha ignored; we set accent alpha).</param>
        static void ApplyTeamPanelAccentBar(GameObject root, Color teamRgb)
        {
            if (root == null)
                return;

            // SampleScene path: Team*Panel/Content/TitleBar (Image + LayoutElement height ~32).
            Transform titleBar = root.transform.Find("Content/TitleBar");
            if (titleBar == null)
                titleBar = FindChildRecursiveByName(root.transform, "TitleBar");
            if (titleBar == null || !titleBar.TryGetComponent<Image>(out var barImage))
                return;

            // Solid fill — drop any Placeholder HUD sprite so hue is pure team color.
            barImage.sprite = null;
            barImage.type = Image.Type.Simple;
            barImage.preserveAspect = false;
            Color accent = teamRgb;
            accent.a = TeamPanelAccentBarAlpha;
            barImage.color = accent;
        }

        /// <summary>
        /// Tints the card Outline with the team color so the panel edge matches the TitleBar accent.
        /// </summary>
        /// <param name="root">TeamAPanel…TeamEPanel root.</param>
        /// <param name="teamRgb">Canonical team RGB.</param>
        static void ApplyTeamPanelOutline(GameObject root, Color teamRgb)
        {
            if (root == null)
                return;
            if (!root.TryGetComponent<Outline>(out var outline))
                return;

            // Slightly brighter edge so the outline pops against the nebula fill.
            Color edge = Color.Lerp(teamRgb, Color.white, 0.35f);
            edge.a = 1f;
            outline.effectColor = edge;
        }

        /// <summary>
        /// Depth-first search for a child Transform by exact name (TitleBar fallback if path differs).
        /// </summary>
        static Transform FindChildRecursiveByName(Transform parent, string childName)
        {
            if (parent == null || string.IsNullOrEmpty(childName))
                return null;

            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (child.name == childName)
                    return child;

                Transform nested = FindChildRecursiveByName(child, childName);
                if (nested != null)
                    return nested;
            }

            return null;
        }

        /// <summary>
        /// Maps TeamAPanel…TeamEPanel to the shared <see cref="TeamIdExtensions.ToColor"/> palette.
        /// </summary>
        static bool TryGetTeamColorForPanel(GameObject root, out Color color)
        {
            color = Color.white;
            if (root == null)
                return false;

            // [TITAN-ORBIT] Panel names are authored in SampleScene as TeamAPanel … TeamEPanel.
            switch (root.name)
            {
                case "TeamAPanel":
                    color = TeamId.TeamA.ToColor();
                    return true;
                case "TeamBPanel":
                    color = TeamId.TeamB.ToColor();
                    return true;
                case "TeamCPanel":
                    color = TeamId.TeamC.ToColor();
                    return true;
                case "TeamDPanel":
                    color = TeamId.TeamD.ToColor();
                    return true;
                case "TeamEPanel":
                    color = TeamId.TeamE.ToColor();
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Loads the Join Team nebula sprite once from Resources (player-safe path).
        /// </summary>
        static Sprite ResolveTeamPanelSpaceSprite()
        {
            if (s_TeamPanelSpaceSprite != null)
                return s_TeamPanelSpaceSprite;

            // Resources.Load<Sprite> works when the texture importer type is Sprite (2D).
            s_TeamPanelSpaceSprite = Resources.Load<Sprite>(TeamPanelSpaceSpriteResourcesPath);
            if (s_TeamPanelSpaceSprite != null)
                return s_TeamPanelSpaceSprite;

            // Fallback: some importers expose the asset as Texture2D with a sub-sprite.
            var sprites = Resources.LoadAll<Sprite>(TeamPanelSpaceSpriteResourcesPath);
            if (sprites != null && sprites.Length > 0)
            {
                s_TeamPanelSpaceSprite = sprites[0];
                return s_TeamPanelSpaceSprite;
            }

            return null;
        }

        /// <summary>
        /// Makes the TeamSelectionPanel container Image fully transparent so it no longer draws
        /// a box behind the team cards. Layout (HorizontalLayoutGroup) still runs on the same GO.
        /// </summary>
        /// <param name="root">Usually <c>TeamSelectionPanel</c>; null-safe no-op.</param>
        static void HideJoinTeamContainerBackground(GameObject root)
        {
            if (root == null)
                return;

            if (!root.TryGetComponent<Image>(out var image))
                return;

            // [UNITY] Keep the Image component (layout / raycasts) but draw nothing.
            // [TITAN-ORBIT] LobbyPanel + team cards carry the look; this second fill was redundant.
            image.sprite = null;
            image.type = Image.Type.Simple;
            image.preserveAspect = false;
            var c = image.color;
            c.a = 0f;
            image.color = c;
            image.raycastTarget = false;
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
