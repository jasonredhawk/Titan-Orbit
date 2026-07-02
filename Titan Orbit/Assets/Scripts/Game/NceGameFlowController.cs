using System.Collections;
using TitanOrbit.Core;
using TitanOrbit.ECS;
using TitanOrbit.NetCode;
using TMPro;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Drives the NCE vertical-slice UI flow: local connect → team pick → gameplay.
    /// </summary>
    public class NceGameFlowController : MonoBehaviour
    {
        [Header("UI Panels")]
        [SerializeField] GameObject mainMenuPanel;
        [SerializeField] GameObject lobbyPanel;
        [SerializeField] GameObject teamSelectionPanel;
        [SerializeField] GameObject loadingRoot;
        [SerializeField] GameObject gameplayRoot;

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
        bool _loggedWaitingForMap;
        bool _loggedTeamUiReady;
        float _connectedAt = -1f;
        float _mppmConnectedSince = -1f;
        string _statusMessage = "Press Play to start a local match.";

        void Awake()
        {
            ClientTeamFlowState.Reset();
            _teamButtons = new[] { teamAButton, teamBButton, teamCButton, teamDButton, teamEButton };
            _teamPanels = new[] { teamAPanel, teamBPanel, teamCPanel, teamDPanel, teamEPanel };
            ResolveMissingReferences();
            if (GetComponent<EcsWorldVisualizer>() == null)
                gameObject.AddComponent<EcsWorldVisualizer>();
            WireTeamButtons();
            EnsureMainMenuPlayButton();
        }

        void Start()
        {
            WirePlayButton();
            if (_teamButtons[0] == null || _teamButtons[1] == null || _teamButtons[2] == null)
                Debug.LogWarning("[NceGameFlow] One or more team Join buttons were not found. Expected TeamAPanel/Content/JoinButton etc.");
            else
                Debug.Log("[NceGameFlow] Team Join buttons wired.");

            LogNetCodeWorldState();

            // Always begin at the main menu until the player connects.
            if (mainMenuPanel != null) mainMenuPanel.SetActive(true);
            if (lobbyPanel != null) lobbyPanel.SetActive(false);
            if (teamSelectionPanel != null) teamSelectionPanel.SetActive(false);
            if (loadingRoot != null) loadingRoot.SetActive(false);
            if (gameplayRoot != null) gameplayRoot.SetActive(false);

            if (TitanOrbitPlayModeUtility.IsMppmAdditionalEditorInstance())
            {
                _autoStartSent = true;
                _statusMessage = "Connecting to host... choose a team when the lobby appears.";
                if (playButton != null)
                    playButton.interactable = false;
            }

            WireTeamButtons();
            RefreshUi();

            if (autoStartLocalPlayInEditor && Application.isEditor &&
                !TitanOrbitPlayModeUtility.IsMppmAdditionalEditorInstance())
                StartCoroutine(WaitAndStartLocalPlay());
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
            message += ". Use the main Editor Game tab; run Titan Orbit > Configure Multiplayer For Local Play if Play fails.";
            Debug.Log(message);
        }

        void Update()
        {
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
                {
                    var panel = loading.transform.Find("LoadingPanel");
                    loadingRoot = panel != null ? panel.gameObject : loading;
                }
            }
            if (gameplayRoot == null)
                gameplayRoot = GameObject.Find("HUD");

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
            var transforms = Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None);
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
                Debug.LogError("[NceGameFlow] TitanOrbitSessionManager not found on NceGameRoot.");
                return;
            }

            if (!HasPlayableClientWorld())
            {
                _statusMessage = "No ClientWorld. Run Titan Orbit > Configure Multiplayer For Local Play, then use the main Editor Game tab.";
                Debug.LogError("[NceGameFlow] ClientWorld missing. client=" + DescribeWorld(ClientServerBootstrap.ClientWorld) +
                               " server=" + DescribeWorld(ClientServerBootstrap.ServerWorld) +
                               ". Run menu: Titan Orbit > Configure Multiplayer For Local Play. " +
                               "Use the main Editor Game tab (not a Server-only player window). " +
                               "Check the ▾ dropdown next to the Play button — pick Default or Client+Server for Main Editor.");
                return;
            }

            _statusMessage = "Connecting to game server...";
            if (playButton != null)
                playButton.interactable = false;

            TitanOrbitSessionManager.Instance.StartLocalPlay();
            _autoStartSent = true;
        }

        static bool HasPlayableClientWorld()
        {
            var client = ClientServerBootstrap.ClientWorld;
            if (client == null || !client.IsCreated)
                return false;
            return client.EntityManager.CreateEntityQuery(typeof(NetworkStreamDriver)).CalculateEntityCount() > 0;
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

            if (IsInGameFlow() && IsMapReadyForTeamSelection() && !EcsGameBridge.HasLocalPlayerShip())
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
            if (EcsGameBridge.IsMapLoadingComplete())
                return true;

            // Remote MPPM client: once connected, allow team pick even if map ghosts are slow.
            if (TitanOrbitPlayModeUtility.IsMppmAdditionalEditorInstance() &&
                IsInGameFlow() &&
                _mppmConnectedSince >= 0f &&
                Time.time - _mppmConnectedSince >= 1f)
                return true;

            return false;
        }

        bool IsInGameFlow() => EcsGameBridge.IsNetworkInGame();

        void RefreshUi()
        {
            if (TitanOrbitPlayModeUtility.IsMppmAdditionalEditorInstance() && IsInGameFlow() && _mppmConnectedSince < 0f)
                _mppmConnectedSince = Time.time;

            bool connected = IsInGameFlow();
            bool mapReady = connected && IsMapReadyForTeamSelection();
            bool hasShip = connected && EcsGameBridge.HasLocalPlayerShip();
            bool teamConfirmed = ClientTeamFlowState.TeamChoiceConfirmed;
            int activeTeamsForUi = 0;
            bool knowsTeamCount = connected && mapReady &&
                                  EcsGameBridge.TryGetActiveTeamCount(out activeTeamsForUi) &&
                                  activeTeamsForUi > 0;
            bool showLoading = connected && !mapReady;
            bool showTeamCountWait = connected && mapReady && !hasShip && !teamConfirmed && !knowsTeamCount;
            bool showTeam = connected && mapReady && !hasShip && !teamConfirmed && knowsTeamCount;
            bool showSpawnWait = connected && teamConfirmed && !hasShip;

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
                mainMenuPanel.SetActive(!connected);

            if (playButton != null && mainMenuPanel != null && mainMenuPanel.activeSelf)
                playButton.gameObject.SetActive(true);

            if (statusText != null)
            {
                if (!connected || showLoading || showTeam || showTeamCountWait || showSpawnWait)
                    statusText.text = !connected
                        ? _statusMessage
                        : showLoading
                            ? "Loading map... (start the match on the Main Editor if this persists)"
                            : showTeamCountWait
                                ? "Preparing teams..."
                            : showSpawnWait
                                ? "Spawning your ship..."
                                : "Choose a team.";
            }

            // Lobby backdrop covers loading + team pick (loadingRoot is an empty legacy object).
            if (lobbyPanel != null)
                lobbyPanel.SetActive(showLoading || showTeam || showTeamCountWait || showSpawnWait);
            if (teamSelectionPanel != null)
                teamSelectionPanel.SetActive(showTeam);

            if (showTeam)
            {
                if (EnsureTeamUiReferences())
                    WireTeamButtons();
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

            if (loadingRoot != null)
                loadingRoot.SetActive(false);

            if (gameplayRoot != null)
                gameplayRoot.SetActive(connected && hasShip);
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
