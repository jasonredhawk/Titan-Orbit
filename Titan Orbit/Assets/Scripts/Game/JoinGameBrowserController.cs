using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using TitanOrbit.Core;
using TitanOrbit.Diagnostics;
using TitanOrbit.NetCode;
using TitanOrbit.Services;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.Game
{
    /// <summary>
    /// [HYBRID] Join Game screen — programmatic UGUI overlay listing Unity Gaming Services (UGS) dedicated
    /// lobbies and connecting the client to the dedicated host IP:port. Opened from <see cref="NceGameFlowController"/> main menu.
    /// Client only; dedicated server builds have no canvas.
    /// Layout matches the Main Menu look: transparent backdrop over SpaceBackground, Titan Orbit logo,
    /// then Refresh / Quick join latest, then the lobby list.
    /// Each lobby row shows team cards, map extras, capacity, and its own Join button (no footer Join).
    /// The dedicated process publishes one IsLatest match; players join it — they do not create games.
    /// </summary>
    public class JoinGameBrowserController : MonoBehaviour
    {
        const float ContentWidth = 720f;
        /// <summary>Default row height for title + team cards + footer with per-row Join (2–3 teams).</summary>
        const float RowHeight = 188f;
        const float AutoRefreshIntervalSeconds = 45f;
        const float CacheGraceSeconds = 180f;
        /// <summary>How often age + empty-idle countdown labels tick without re-querying UGS.</summary>
        const float RowDurationRefreshSeconds = 1f;
        const int RequestMatchPollAttempts = 18;
        const int RequestMatchPollIntervalMs = 5000;
        /// <summary>[TITAN-ORBIT] Max team slots (TeamA–TeamE); matches map generation and TeamId range.</summary>
        const int MaxTeamSlots = 5;

        static readonly Color RowNormalColor = new Color(0.11f, 0.17f, 0.28f, 0.98f);
        static readonly Color RowSelectedColor = new Color(0.18f, 0.38f, 0.62f, 0.98f);
        static readonly Color MutedLabelColor = new Color(0.68f, 0.78f, 0.9f, 0.92f);
        static readonly Color DurationLabelColor = new Color(0.62f, 0.74f, 0.86f, 0.88f);
        static readonly Color TeamCardBgColor = new Color(0.07f, 0.11f, 0.18f, 0.96f);

        [SerializeField] GameObject mainMenuPanel;

        GameObject _screenRoot;
        GameObject _lobbyBrowserRoot;
        GameObject _lobbyRowPrefab;
        Transform _listContainer;
        TextMeshProUGUI _statusText;
        TextMeshProUGUI _buildIdText;
        Button _refreshButton;
        Button _quickJoinButton;

        readonly List<TitanOrbitLobbyService.LobbySummary> _cached = new List<TitanOrbitLobbyService.LobbySummary>();
        readonly List<GameObject> _rowObjects = new List<GameObject>();
        readonly List<Image> _rowBackgrounds = new List<Image>();
        readonly List<TextMeshProUGUI> _rowDurationLabels = new List<TextMeshProUGUI>();
        /// <summary>Footer extras labels (map meta + idle countdown); parallel to <see cref="_cached"/>.</summary>
        readonly List<TextMeshProUGUI> _rowExtrasLabels = new List<TextMeshProUGUI>();
        string _selectedLobbyId;
        int _selectedRowIndex = -1;
        bool _refreshInProgress;
        bool _joinInProgress;
        bool _requestInProgress;
        bool _autoRequestMatchSent;
        float _lastSuccessfulFetch = -1f;
        float _autoRefreshTimer;
        float _durationRefreshTimer;

        ScrollRect _lobbyScroll;

        public bool IsVisible => _screenRoot != null && _screenRoot.activeSelf;

        /// <summary>
        /// True after <see cref="DismissForLoading"/> until the browser is shown or hidden again,
        /// or <see cref="ClearLoadingHandoff"/> runs when the map overlay is done.
        /// Keeps MainMenuPanel off during the handoff so Play cannot flash under the loading overlay.
        /// </summary>
        public bool IsHandedOffToLoading { get; private set; }

        /// <summary>Stops the loading-handoff latch so Join Team / menu can show again.</summary>
        public void ClearLoadingHandoff() => IsHandedOffToLoading = false;

        public void Configure(GameObject menuPanel) => mainMenuPanel = menuPanel;

        public void Show()
        {
            IsHandedOffToLoading = false;
            // --- Show ---
            // Rebuild when status is still inside the list panel (last over-tight pass) or layout is outdated.
            bool statusInsideList = _screenRoot != null &&
                _screenRoot.transform.Find("JoinGameBody/JoinGameContentColumn/LobbyBrowserRoot/JoinGameStatus") != null;
            bool statusMissingOutside = _screenRoot != null &&
                _screenRoot.transform.Find("JoinGameBody/JoinGameContentColumn/JoinGameStatus") == null;
            var existingLogo = _screenRoot != null
                ? _screenRoot.transform.Find("JoinGameBody/JoinGameContentColumn/JoinGameLogo")
                : null;
            var existingListLe = _screenRoot != null
                ? _screenRoot.transform.Find("JoinGameBody/JoinGameContentColumn/LobbyBrowserRoot")
                    ?.GetComponent<LayoutElement>()
                : null;
            bool listNotFlexed = existingListLe != null &&
                                 (existingListLe.flexibleHeight < 0.5f || existingListLe.preferredHeight > 1f);
            bool needsRebuild = _screenRoot != null &&
                                (_lobbyScroll == null ||
                                 _lobbyBrowserRoot == null ||
                                 _lobbyRowPrefab == null ||
                                 _lobbyRowPrefab.transform.Find("LobbyRowMain/LobbyRowTeams") == null ||
                                 _lobbyRowPrefab.transform.Find("LobbyRowMain/LobbyRowFooter/LobbyRowJoin") == null ||
                                 existingLogo == null ||
                                 statusInsideList ||
                                 statusMissingOutside ||
                                 listNotFlexed ||
                                 _screenRoot.transform.Find("JoinGameBody/JoinGameContentColumn/JoinGameTitle") != null ||
                                 _screenRoot.transform.Find("JoinGameBody/JoinGameContentColumn/JoinGameActions/RequestDedicatedMatch") != null);
            if (needsRebuild)
            {
                Destroy(_screenRoot);
                _screenRoot = null;
                _lobbyBrowserRoot = null;
                _lobbyRowPrefab = null;
                _listContainer = null;
                _refreshButton = null;
                _quickJoinButton = null;
                _statusText = null;
                _buildIdText = null;
                _lobbyScroll = null;
            }

            EnsureUi();
            if (mainMenuPanel != null)
                mainMenuPanel.SetActive(false);
            _screenRoot.SetActive(true);
            _screenRoot.transform.SetAsLastSibling();
            _autoRefreshTimer = 0f;
            _autoRequestMatchSent = false;
            Debug.Log("[JoinGameBrowser] Opening join browser — refreshing lobby list.");
            _ = ShowAndRefreshAsync();
        }

        async Task ShowAndRefreshAsync()
        {
            // --- ShowAndRefreshAsync ---
            SetStatus("Connecting to multiplayer services...");
            bool ugsReady = await UnityGameServicesBootstrap.EnsureGuestSessionForOnlineAsync();
            if (!ugsReady)
            {
                SetStatus("Multiplayer services unavailable. Check your connection and tap Refresh.");
                Debug.LogWarning("[JoinGameBrowser] UGS guest session not ready. project=" +
                                 (Application.cloudProjectId ?? "(none)"));
            }

            await RefreshAsync(silent: false);
        }

        public void Hide()
        {
            // --- Hide (Back / leave join flow) ---
            // [UNITY] Deactivates this overlay and returns the player to the main menu panel.
            IsHandedOffToLoading = false;
            if (_screenRoot != null)
                _screenRoot.SetActive(false);
            if (mainMenuPanel != null)
                mainMenuPanel.SetActive(true);
        }

        void OnDestroy()
        {
            DestroyJoinGameOverlay();
        }

        void OnDisable()
        {
            if (!Application.isPlaying)
                DestroyJoinGameOverlay();
        }

        /// <summary>Destroys the runtime overlay so exiting Play does not leave JoinGameScreen in Hierarchy.</summary>
        void DestroyJoinGameOverlay()
        {
            if (_screenRoot != null)
            {
                Destroy(_screenRoot);
                _screenRoot = null;
            }

            Transform host = mainMenuPanel != null
                ? mainMenuPanel.GetComponentInParent<Canvas>()?.transform
                : null;
            if (host == null)
                return;

            for (int i = host.childCount - 1; i >= 0; i--)
            {
                Transform child = host.GetChild(i);
                if (child != null && child.name == "JoinGameScreen")
                    Destroy(child.gameObject);
            }
        }

        /// <summary>Sets the Join Game status line (used after a failed dedicated map sync).</summary>
        public void SetPublicStatus(string message)
        {
            SetStatus(message);
        }

        /// <summary>
        /// Hides the Join Game overlay when the Loading Map screen takes over — without
        /// re-enabling the main menu. <see cref="NceGameFlowController.RefreshUi"/> already
        /// keeps the main menu off while dedicated connect / map load is in progress.
        /// </summary>
        public void DismissForLoading()
        {
            // --- Dismiss for loading ---
            // [TITAN-ORBIT] Do NOT call Hide() here: Hide() turns mainMenuPanel back on, which
            // would flash the Play menu under the loading screen for a frame.
            IsHandedOffToLoading = true;
            if (_screenRoot != null)
                _screenRoot.SetActive(false);
            if (mainMenuPanel != null)
                mainMenuPanel.SetActive(false);

            // Cover this frame immediately — RefreshUi may have already run.
            LoadingScreenControllerNce.ShowExisting();
        }

        void Update()
        {
            // --- Per-frame refresh ---
            // [UNITY] Runs every frame while the Join Game overlay is visible.
            if (!IsVisible || _refreshInProgress || _joinInProgress)
                return;

            // --- Auto-refresh lobby list ---
            // [TITAN-ORBIT] Quiet background poll so new dedicated matches appear without tapping Refresh.
            _autoRefreshTimer += Time.unscaledDeltaTime;
            if (_autoRefreshTimer >= AutoRefreshIntervalSeconds)
            {
                _autoRefreshTimer = 0f;
                _ = RefreshAsync(silent: true);
            }

            // --- Live-update age + empty-idle countdown without re-querying UGS ---
            if (_cached.Count > 0)
            {
                _durationRefreshTimer += Time.unscaledDeltaTime;
                if (_durationRefreshTimer >= RowDurationRefreshSeconds)
                {
                    _durationRefreshTimer = 0f;
                    RefreshRowLiveLabels();
                }
            }
        }

        void EnsureUi()
        {
            // --- Ensure setup ---
            // Builds the full Join Game overlay once; Show() destroys and rebuilds when the layout
            // version is outdated (logo / per-row Join missing).
            if (_screenRoot != null)
                return;

            Transform host = ResolveUiHost();

            // --- Full-screen soft backdrop (same idea as Main Menu — SpaceBackground shows through) ---
            _screenRoot = new GameObject("JoinGameScreen", typeof(RectTransform), typeof(Image));
            _screenRoot.transform.SetParent(host, false);
            var screenRt = _screenRoot.GetComponent<RectTransform>();
            screenRt.anchorMin = Vector2.zero;
            screenRt.anchorMax = Vector2.one;
            screenRt.offsetMin = Vector2.zero;
            screenRt.offsetMax = Vector2.zero;
            MainMenuPresenter.ApplyTransparentMenuBackdrop(_screenRoot.GetComponent<Image>());

            // --- Top-left Back (floats over the body; logo uses the full top edge behind it) ---
            var topBar = CreateChild("TopBar", _screenRoot.transform, typeof(RectTransform), typeof(HorizontalLayoutGroup));
            var topRt = topBar.GetComponent<RectTransform>();
            topRt.anchorMin = new Vector2(0f, 1f);
            topRt.anchorMax = new Vector2(1f, 1f);
            topRt.pivot = new Vector2(0.5f, 1f);
            topRt.sizeDelta = new Vector2(0f, 52f);
            topRt.anchoredPosition = Vector2.zero;
            var topH = topBar.GetComponent<HorizontalLayoutGroup>();
            topH.padding = new RectOffset(12, 12, 6, 6);
            topH.spacing = 16f;
            topH.childAlignment = TextAnchor.MiddleLeft;
            topH.childControlHeight = true;
            topH.childControlWidth = false;
            topH.childForceExpandHeight = false;
            topH.childForceExpandWidth = false;

            var back = CreateMenuButton("Back", "Back", topBar.transform, new Vector2(120f, 40f), false);
            back.onClick.AddListener(Hide);

            // --- Body: logo (flush top) → tight actions → status → list stretched to bottom ---
            var body = CreateChild("JoinGameBody", _screenRoot.transform, typeof(RectTransform), typeof(VerticalLayoutGroup));
            var bodyRt = body.GetComponent<RectTransform>();
            bodyRt.anchorMin = Vector2.zero;
            bodyRt.anchorMax = Vector2.one;
            // Tight top inset only — logo close to the screen edge (Back stays top-left over it).
            bodyRt.offsetMin = new Vector2(20f, 12f);
            bodyRt.offsetMax = new Vector2(-20f, 0f);
            var bodyLayout = body.GetComponent<VerticalLayoutGroup>();
            bodyLayout.spacing = 0f;
            bodyLayout.padding = new RectOffset(0, 0, 0, 0);
            bodyLayout.childAlignment = TextAnchor.UpperCenter;
            bodyLayout.childControlWidth = true;
            bodyLayout.childControlHeight = true;
            bodyLayout.childForceExpandWidth = false;
            // Expand the single content column so the lobby list can flex to the bottom.
            bodyLayout.childForceExpandHeight = true;

            var contentColumn = CreateChild("JoinGameContentColumn", body.transform,
                typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(LayoutElement));
            ApplyContentColumnLayout(contentColumn.GetComponent<LayoutElement>());
            var columnLe = contentColumn.GetComponent<LayoutElement>();
            columnLe.flexibleHeight = 1f;
            columnLe.minHeight = 280f;
            columnLe.preferredHeight = -1f;
            var columnV = contentColumn.GetComponent<VerticalLayoutGroup>();
            // Normal stack spacing for buttons / status / list (not over-compressed).
            columnV.spacing = 4f;
            columnV.childAlignment = TextAnchor.UpperCenter;
            columnV.childControlWidth = true;
            columnV.childControlHeight = true;
            columnV.childForceExpandWidth = false;
            columnV.childForceExpandHeight = false;

            // [TITAN-ORBIT] Logo first — PlaceCompactTopLogo trims PNG padding so the gap under the art is smaller.
            MainMenuPresenter.PlaceCompactTopLogo(contentColumn.transform, "JoinGameLogo");

            // --- Action row: Refresh | Quick join latest ---
            var actionRow = CreateChild("JoinGameActions", contentColumn.transform,
                typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
            var actionH = actionRow.GetComponent<HorizontalLayoutGroup>();
            actionH.spacing = 12f;
            actionH.childAlignment = TextAnchor.MiddleCenter;
            actionH.childControlWidth = true;
            actionH.childControlHeight = false;
            actionH.childForceExpandWidth = true;
            actionH.childForceExpandHeight = false;
            var actionLe = actionRow.GetComponent<LayoutElement>();
            actionLe.minHeight = 44f;
            actionLe.preferredHeight = 48f;
            actionLe.flexibleHeight = 0f;
            ApplyContentColumnLayout(actionLe);

            _refreshButton = CreateMenuButton("RefreshLobbies", "Refresh", actionRow.transform,
                new Vector2(200f, 48f), false);
            _refreshButton.onClick.AddListener(() => _ = RefreshAsync(silent: false));

            _quickJoinButton = CreateMenuButton("QuickJoinButton", "Quick join latest", actionRow.transform,
                new Vector2(320f, 48f), true);
            _quickJoinButton.onClick.AddListener(() => _ = QuickJoinAsync());

            _statusText = CreateStyledLabel("JoinGameStatus", "Loading lobbies...", contentColumn.transform,
                16f, FontStyles.Normal, TextAlignmentOptions.Center);
            _statusText.color = MutedLabelColor;
            _statusText.enableWordWrapping = true;
            _statusText.overflowMode = TextOverflowModes.Ellipsis;
            var statusLe = _statusText.gameObject.AddComponent<LayoutElement>();
            statusLe.minHeight = 20f;
            statusLe.preferredHeight = 22f;
            statusLe.flexibleHeight = 0f;
            ApplyContentColumnLayout(statusLe);

            _buildIdText = CreateStyledLabel("JoinGameBuildId", "", contentColumn.transform,
                14f, FontStyles.Normal, TextAlignmentOptions.Center);
            _buildIdText.color = new Color(0.72f, 0.82f, 0.94f, 0.95f);
            _buildIdText.enableWordWrapping = true;
            _buildIdText.richText = true;
            var buildLe = _buildIdText.gameObject.AddComponent<LayoutElement>();
            buildLe.minHeight = 52f;
            buildLe.preferredHeight = 56f;
            buildLe.flexibleHeight = 0f;
            ApplyContentColumnLayout(buildLe);
            RefreshBuildIdLabel();

            BuildLobbyBrowserPanel(contentColumn.transform);

            // Keep Back above the full-screen body so it stays clickable over the logo.
            topBar.transform.SetAsLastSibling();

            _screenRoot.SetActive(false);
        }

        /// <summary>
        /// Soft lobby list panel — scrollable match cards. Flexible height fills remaining screen
        /// under the logo / action row so the panel stretches to the bottom edge.
        /// </summary>
        void BuildLobbyBrowserPanel(Transform parent)
        {
            _lobbyBrowserRoot = CreateChild("LobbyBrowserRoot", parent,
                typeof(RectTransform), typeof(Image), typeof(Outline), typeof(VerticalLayoutGroup), typeof(LayoutElement));
            var rootRt = _lobbyBrowserRoot.GetComponent<RectTransform>();
            // Stretch horizontally within the content column; height comes from LayoutElement flex.
            rootRt.anchorMin = new Vector2(0f, 0f);
            rootRt.anchorMax = new Vector2(1f, 1f);
            rootRt.pivot = new Vector2(0.5f, 0.5f);
            rootRt.sizeDelta = Vector2.zero;

            // Semi-transparent so the space backdrop still reads through the list panel.
            var rootImage = _lobbyBrowserRoot.GetComponent<Image>();
            rootImage.color = new Color(0.04f, 0.07f, 0.12f, 0.55f);
            rootImage.raycastTarget = false;
            var rootOutline = _lobbyBrowserRoot.GetComponent<Outline>();
            rootOutline.effectColor = new Color(0.28f, 0.48f, 0.72f, 0.35f);
            rootOutline.effectDistance = new Vector2(1.5f, -1.5f);

            var rootVlg = _lobbyBrowserRoot.GetComponent<VerticalLayoutGroup>();
            rootVlg.spacing = 0f;
            rootVlg.padding = new RectOffset(8, 8, 8, 8);
            rootVlg.childAlignment = TextAnchor.UpperLeft;
            rootVlg.childControlWidth = true;
            rootVlg.childControlHeight = true;
            rootVlg.childForceExpandWidth = true;
            // Scroll child expands to fill this panel.
            rootVlg.childForceExpandHeight = true;

            var rootLe = _lobbyBrowserRoot.GetComponent<LayoutElement>();
            ApplyContentColumnLayout(rootLe);
            // flexibleHeight=1 → eats all leftover space under logo/buttons/status down to screen bottom.
            rootLe.minHeight = 160f;
            rootLe.preferredHeight = 0f;
            rootLe.flexibleHeight = 1f;

            var scrollRoot = CreateChild("LobbyScroll", _lobbyBrowserRoot.transform,
                typeof(RectTransform), typeof(Image), typeof(ScrollRect), typeof(LayoutElement));
            scrollRoot.GetComponent<Image>().color = new Color(0.03f, 0.05f, 0.09f, 0.45f);
            var scrollLe = scrollRoot.GetComponent<LayoutElement>();
            scrollLe.minHeight = 120f;
            scrollLe.preferredHeight = 0f;
            scrollLe.flexibleHeight = 1f;
            ApplyContentColumnLayout(scrollLe);

            var viewport = CreateChild("Viewport", scrollRoot.transform, typeof(RectTransform), typeof(Image), typeof(Mask));
            viewport.GetComponent<Image>().color = new Color(0.07f, 0.1f, 0.14f, 0.35f);
            viewport.GetComponent<Mask>().showMaskGraphic = false;
            var vpRt = viewport.GetComponent<RectTransform>();
            vpRt.anchorMin = Vector2.zero;
            vpRt.anchorMax = Vector2.one;
            vpRt.offsetMin = Vector2.zero;
            vpRt.offsetMax = Vector2.zero;

            var content = CreateChild("LobbyList", viewport.transform, typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            var contentRt = content.GetComponent<RectTransform>();
            contentRt.anchorMin = new Vector2(0f, 1f);
            contentRt.anchorMax = new Vector2(1f, 1f);
            contentRt.pivot = new Vector2(0.5f, 1f);
            contentRt.anchoredPosition = Vector2.zero;
            contentRt.sizeDelta = new Vector2(0f, 0f);
            var vlg = content.GetComponent<VerticalLayoutGroup>();
            vlg.spacing = 10f;
            vlg.padding = new RectOffset(6, 6, 6, 6);
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            content.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            _listContainer = content.transform;

            _lobbyScroll = scrollRoot.GetComponent<ScrollRect>();
            _lobbyScroll.viewport = vpRt;
            _lobbyScroll.content = contentRt;
            _lobbyScroll.horizontal = false;
            _lobbyScroll.vertical = true;
            _lobbyScroll.movementType = ScrollRect.MovementType.Clamped;

            _lobbyRowPrefab = CreateLobbyRowPrefab();
            if (_lobbyRowPrefab != null)
                _lobbyRowPrefab.SetActive(false);
        }

        async Task RefreshAsync(bool silent)
        {
            // --- RefreshAsync ---
            if (_refreshInProgress)
                return;

            _refreshInProgress = true;
            if (!silent)
            {
                SetStatus("Loading lobbies...");
                if (_refreshButton != null)
                    _refreshButton.interactable = false;
            }

            try
            {
                if (!await UnityGameServicesBootstrap.EnsureGuestSessionForOnlineAsync())
                {
                    if (!silent)
                        SetStatus("Connecting to multiplayer services…");
                    RenderList();
                    return;
                }

                var fetched = await TitanOrbitLobbyService.QueryBrowsableDedicatedLobbiesAsync(40, skipEmptyStabilization: silent);
                var kind = TitanOrbitLobbyService.LastOpenLobbyQueryKind;

                if (fetched.Count > 0)
                {
                    _lastSuccessfulFetch = Time.realtimeSinceStartup;
                    ApplySummaries(fetched, silent);
                    return;
                }

                // --- Empty list: branch on last query kind ---
                if (kind == TitanOrbitLobbyService.OpenLobbyQueryResultKind.UnityServicesNotReady)
                {
                    if (!silent)
                        SetStatus("Connecting to multiplayer services…");
                    RenderList();
                    return;
                }

                if (kind == TitanOrbitLobbyService.OpenLobbyQueryResultKind.Error && !silent)
                {
                    string detail = TitanOrbitLobbyService.LastOpenLobbyQueryErrorDetail;
                    SetStatus(string.IsNullOrEmpty(detail)
                        ? "Could not load lobbies. Tap Refresh."
                        : "Could not load lobbies: " + detail);
                }

                if (_cached.Count > 0 && _lastSuccessfulFetch >= 0f &&
                    Time.realtimeSinceStartup - _lastSuccessfulFetch <= CacheGraceSeconds)
                {
                    RenderList();
                    if (!silent)
                        SetStatus("Searching for matches… showing previous list.");
                    return;
                }

                ApplySummaries(fetched, silent);
            }
            finally
            {
                _refreshInProgress = false;
                if (_refreshButton != null)
                    _refreshButton.interactable = true;
            }
        }

        void ApplySummaries(List<TitanOrbitLobbyService.LobbySummary> fetched, bool silent)
        {
            // --- Apply changes ---
            _cached.Clear();
            _cached.AddRange(fetched);
            _selectedLobbyId = null;
            _selectedRowIndex = -1;
            RenderList();
            if (!silent)
            {
                string project = Application.cloudProjectId ?? "(none)";
                SetStatus(_cached.Count == 0
                    ? "No dedicated match listed (project " + project + "). The server publishes games automatically — tap Refresh."
                    : "Tap Join on a match, or use Quick join latest.");
            }
        }

        Transform ResolveUiHost()
        {
            // --- Resolve value ---
            if (mainMenuPanel != null)
            {
                var canvas = mainMenuPanel.GetComponentInParent<Canvas>();
                if (canvas != null)
                    return canvas.transform;
                if (mainMenuPanel.transform.parent != null)
                    return mainMenuPanel.transform.parent;
            }

            var anyCanvas = UnityEngine.Object.FindAnyObjectByType<Canvas>();
            return anyCanvas != null ? anyCanvas.transform : transform;
        }

        void RebuildLobbyListLayout()
        {
            // --- Rebuild cache ---
            if (_listContainer == null)
                return;

            var contentRt = _listContainer as RectTransform;
            if (contentRt != null)
                LayoutRebuilder.ForceRebuildLayoutImmediate(contentRt);

            if (_lobbyScroll != null)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(_lobbyScroll.viewport);
                _lobbyScroll.verticalNormalizedPosition = 1f;
            }

            Canvas.ForceUpdateCanvases();
        }

        void RenderList()
        {
            // --- RenderList ---
            if (_listContainer == null)
            {
                Debug.LogWarning("[JoinGameBrowser] RenderList skipped — list container missing.");
                return;
            }

            for (int i = _rowObjects.Count - 1; i >= 0; i--)
            {
                GameObject row = _rowObjects[i];
                if (row != null)
                    Destroy(row);
            }

            _rowObjects.Clear();
            _rowBackgrounds.Clear();
            _rowDurationLabels.Clear();
            _rowExtrasLabels.Clear();

            if (_cached.Count == 0)
            {
                RebuildLobbyListLayout();
                return;
            }

            for (int i = 0; i < _cached.Count; i++)
            {
                var summary = _cached[i];
                var row = InstantiateLobbyRow(i, summary);
                if (row == null)
                    continue;

                // Per-row Join — each match joins itself; no footer "select then Join".
                string lobbyId = summary.LobbyId;
                var joinButton = row.transform.Find("LobbyRowMain/LobbyRowFooter/LobbyRowJoin")
                                     ?.GetComponent<Button>();
                if (joinButton != null)
                {
                    joinButton.onClick.RemoveAllListeners();
                    joinButton.onClick.AddListener(() => _ = JoinLobbyByIdAsync(lobbyId));
                    joinButton.interactable = !_joinInProgress && !string.IsNullOrWhiteSpace(lobbyId);
                }

                _rowObjects.Add(row);
                _rowBackgrounds.Add(row.GetComponent<Image>());
                var durationLabel = row.transform.Find("LobbyRowMain/LobbyRowHeader/LobbyRowDuration")
                                        ?.GetComponent<TextMeshProUGUI>()
                                    ?? row.transform.Find("LobbyRowDuration")?.GetComponent<TextMeshProUGUI>();
                // Keep lists index-aligned with _cached / _rowObjects (null-safe refresh).
                _rowDurationLabels.Add(durationLabel);
                var extrasLabel = row.transform.Find("LobbyRowMain/LobbyRowFooter/LobbyRowExtras")
                                      ?.GetComponent<TextMeshProUGUI>();
                _rowExtrasLabels.Add(extrasLabel);
            }

            Debug.Log("[JoinGameBrowser] RenderList rows=" + _cached.Count +
                      (_cached.Count > 0 ? " first=\"" + _cached[0].Name + "\"" : ""));
            RefreshBuildIdLabel();
            RebuildLobbyListLayout();
        }

        GameObject InstantiateLobbyRow(int index, TitanOrbitLobbyService.LobbySummary summary)
        {
            if (_lobbyRowPrefab == null || _listContainer == null)
                return null;

            var row = Instantiate(_lobbyRowPrefab, _listContainer);
            row.name = "LobbyRow" + index;
            row.SetActive(true);

            // --- Resolve row widgets ---
            // Prefab paths are fixed in CreateLobbyRowPrefab; Find keeps Instantiation resilient.
            var nameLabel = row.transform.Find("LobbyRowMain/LobbyRowHeader/LobbyRowName")
                                ?.GetComponent<TextMeshProUGUI>();
            var durationLabel = row.transform.Find("LobbyRowMain/LobbyRowHeader/LobbyRowDuration")
                                    ?.GetComponent<TextMeshProUGUI>();
            var teamsRoot = row.transform.Find("LobbyRowMain/LobbyRowTeams");
            var pendingLabel = row.transform.Find("LobbyRowMain/LobbyRowTeamsPending")
                                   ?.GetComponent<TextMeshProUGUI>();
            var extrasLabel = row.transform.Find("LobbyRowMain/LobbyRowFooter/LobbyRowExtras")
                                  ?.GetComponent<TextMeshProUGUI>();
            var playersLabel = row.transform.Find("LobbyRowMain/LobbyRowFooter/LobbyRowPlayers")
                                   ?.GetComponent<TextMeshProUGUI>();

            // --- Title + freshness ---
            // [TITAN-ORBIT] "Latest" is the dedicated lobby the server is currently advertising as joinable.
            string latestTag = summary.IsLatest
                ? "  <size=15><color=#7ec8ff>● Latest</color></size>"
                : "  <size=15><color=#8a9bb0>● Older</color></size>";
            if (nameLabel != null)
                nameLabel.text = $"<b>{summary.Name}</b>{latestTag}";

            if (durationLabel != null)
                durationLabel.text = FormatLobbyActiveDuration(summary.CreatedAtEpochSeconds);

            // --- Per-team cards (worlds + roster) ---
            int teamSlots = ResolveActiveTeamSlotCount(summary);
            bool filledTeams = ApplyTeamCards(teamsRoot, summary, teamSlots);
            if (pendingLabel != null)
            {
                pendingLabel.gameObject.SetActive(!filledTeams);
                if (!filledTeams)
                    pendingLabel.text = "<color=#6f8499>Map stats pending…</color>";
            }

            if (teamsRoot != null)
                teamsRoot.gameObject.SetActive(filledTeams);

            // --- Footer: map size / neutrals / asteroids + match capacity from map meta ---
            if (extrasLabel != null)
                extrasLabel.text = FormatLobbyExtrasLine(summary);

            if (playersLabel != null)
            {
                // Footer total uses map-driven capacity (teams × max-per-team) when meta is published.
                int capacity = ResolveMatchPlayerCapacity(summary);
                playersLabel.text = $"{summary.CurrentPlayers}/{capacity}";
            }

            // --- Row height grows with team count so 4–5 cards stay readable ---
            var rowLe = row.GetComponent<LayoutElement>();
            if (rowLe != null)
            {
                float height = teamSlots >= 4 ? RowHeight + 28f : RowHeight;
                rowLe.minHeight = height - 16f;
                rowLe.preferredHeight = height;
            }

            return row;
        }

        /// <summary>
        /// Fills the pre-built TeamCard0…TeamCard4 slots for this lobby.
        /// Shows only active teams; each card lists display name, owned worlds, and players/cap.
        /// </summary>
        /// <returns>True when at least one team card was shown.</returns>
        static bool ApplyTeamCards(
            Transform teamsRoot,
            TitanOrbitLobbyService.LobbySummary summary,
            int teamSlots)
        {
            // --- Guard ---
            if (teamsRoot == null || summary == null || teamSlots <= 0)
                return false;

            bool showedAny = false;
            int maxPerTeam = summary.MapMaxPlayersPerTeam > 0 ? summary.MapMaxPlayersPerTeam : -1;

            for (int i = 0; i < MaxTeamSlots; i++)
            {
                Transform card = teamsRoot.Find("TeamCard" + i);
                if (card == null)
                    continue;

                bool active = i < teamSlots;
                card.gameObject.SetActive(active);
                if (!active)
                    continue;

                showedAny = true;
                TeamId team = (TeamId)(i + 1);
                Color teamColor = team.ToColor();
                string hex = ColorUtility.ToHtmlStringRGB(teamColor);

                // --- Accent bar matches in-game team color (minimap / ships) ---
                var accent = card.Find("TeamAccent")?.GetComponent<Image>();
                if (accent != null)
                    accent.color = teamColor;

                // --- Title: colored bullet + "Team A" ---
                var title = card.Find("TeamCardBody/TeamTitle")?.GetComponent<TextMeshProUGUI>();
                if (title != null)
                {
                    title.text = "<color=#" + hex + ">●</color> <b>" + team.ToDisplayName() + "</b>";
                }

                // --- Worlds occupied by this team ---
                int worlds = 0;
                bool hasWorlds = summary.MapTeamPlanetCounts != null && i < summary.MapTeamPlanetCounts.Length;
                if (hasWorlds)
                    worlds = summary.MapTeamPlanetCounts[i];

                var worldsLabel = card.Find("TeamCardBody/TeamWorlds")?.GetComponent<TextMeshProUGUI>();
                if (worldsLabel != null)
                {
                    worldsLabel.text = hasWorlds
                        ? (worlds == 1 ? "1 world" : worlds + " worlds")
                        : "— worlds";
                }

                // --- Players on this team vs per-team cap ---
                int players = 0;
                bool hasPlayers = summary.MapTeamPlayerCounts != null && i < summary.MapTeamPlayerCounts.Length;
                if (hasPlayers)
                    players = summary.MapTeamPlayerCounts[i];

                var playersOnTeam = card.Find("TeamCardBody/TeamPlayers")?.GetComponent<TextMeshProUGUI>();
                if (playersOnTeam != null)
                {
                    if (hasPlayers && maxPerTeam > 0)
                        playersOnTeam.text = players + " / " + maxPerTeam + " players";
                    else if (hasPlayers)
                        playersOnTeam.text = players + " players";
                    else if (maxPerTeam > 0)
                        playersOnTeam.text = "0 / " + maxPerTeam + " players";
                    else
                        playersOnTeam.text = "— players";
                }
            }

            return showedAny;
        }

        /// <summary>
        /// How many team slots this match rolled. Prefers planet/player CSV length, then MapTeams.
        /// </summary>
        static int ResolveActiveTeamSlotCount(TitanOrbitLobbyService.LobbySummary summary)
        {
            if (summary == null)
                return 0;

            // --- Prefer published per-team arrays (authoritative once map heartbeat runs) ---
            if (summary.MapTeamPlanetCounts != null && summary.MapTeamPlanetCounts.Length > 0)
                return Mathf.Clamp(summary.MapTeamPlanetCounts.Length, 0, MaxTeamSlots);
            if (summary.MapTeamPlayerCounts != null && summary.MapTeamPlayerCounts.Length > 0)
                return Mathf.Clamp(summary.MapTeamPlayerCounts.Length, 0, MaxTeamSlots);
            if (summary.MapTeamCount > 0)
                return Mathf.Clamp(summary.MapTeamCount, 0, MaxTeamSlots);
            return 0;
        }

        /// <summary>
        /// Match-wide player capacity for the footer.
        /// Uses map meta (teams × max-per-team) when published; otherwise UGS lobby MaxPlayers.
        /// </summary>
        static int ResolveMatchPlayerCapacity(TitanOrbitLobbyService.LobbySummary summary)
        {
            // --- Map-driven capacity ---
            // [TITAN-ORBIT] Bootstrap sets MaxPlayersPerTeam (e.g. 20); map roll sets team count (2–5).
            // Product is the real joinable roster size — not the hard server ceiling often set to 60.
            if (summary != null && summary.MapTeamCount > 0 && summary.MapMaxPlayersPerTeam > 0)
                return summary.MapTeamCount * summary.MapMaxPlayersPerTeam;

            int slots = ResolveActiveTeamSlotCount(summary);
            if (summary != null && slots > 0 && summary.MapMaxPlayersPerTeam > 0)
                return slots * summary.MapMaxPlayersPerTeam;

            return summary != null ? Mathf.Max(1, summary.MaxPlayers) : 1;
        }

        /// <summary>
        /// Footer line for map size, neutrals, asteroids, and (only when empty) idle-kill countdown.
        /// </summary>
        /// <param name="summary">Lobby browse row data from UGS public Data.</param>
        /// <returns>Rich-text TMP string for the lobby footer extras label.</returns>
        static string FormatLobbyExtrasLine(TitanOrbitLobbyService.LobbySummary summary)
        {
            // --- FormatLobbyExtrasLine ---
            if (summary == null)
                return string.Empty;

            var sb = new StringBuilder(128);
            sb.Append("<size=14>");

            // --- Plain labels only ---
            // [UNITY] TMP default fonts often lack decorative Unicode (◇ / ✦) and draw □ tofu boxes.
            // Prefer the ASCII "x" multiply so every platform font can render the size.
            bool wrote = false;

            // --- Map footprint (toroidal width × height in world units) ---
            // [TITAN-ORBIT] Published by dedicated-server lobby heartbeat from MapSessionMetaRpc sizes.
            if (summary.MapWidth >= 100 && summary.MapHeight >= 100)
            {
                sb.Append("<color=#a8c4e0><b>")
                    .Append(summary.MapWidth)
                    .Append(" x ")
                    .Append(summary.MapHeight)
                    .Append("</b> map</color>");
                wrote = true;
            }

            if (summary.MapNeutralPlanetCount >= 0)
            {
                if (wrote)
                    sb.Append("   <color=#5f738a>|</color>   ");
                sb.Append("<color=#9eb6cc><b>")
                    .Append(summary.MapNeutralPlanetCount)
                    .Append("</b> free worlds</color>");
                wrote = true;
            }

            if (summary.MapAsteroidCount >= 0)
            {
                if (wrote)
                    sb.Append("   <color=#5f738a>|</color>   ");
                sb.Append("<color=#d4b06a><b>")
                    .Append(summary.MapAsteroidCount)
                    .Append("</b> asteroids</color>");
                wrote = true;
            }

            // --- Empty-idle kill countdown (server IdleKillAt; hidden while anyone is in the match) ---
            if (TryFormatEmptyIdleCountdown(summary, out string idleCountdown))
            {
                if (wrote)
                    sb.Append("   <color=#5f738a>|</color>   ");
                sb.Append(idleCountdown);
                wrote = true;
            }

            if (wrote)
                sb.Append("   <color=#5f738a>|</color>   ");
            sb.Append(FormatLobbyHostFragment(summary));
            sb.Append("   <color=#5f738a>|</color>   ");
            if (string.IsNullOrWhiteSpace(summary.ServerBuildId))
                sb.Append("<color=#e08a6a><b>build (old server — no id)</b></color>");
            else
                sb.Append("<color=#8fd4a8><b>build ").Append(summary.ServerBuildId).Append("</b></color>");

            if (!wrote)
                sb.Append("   <color=#6f8499>Waiting for map…</color>");

            sb.Append("</size>");
            return sb.ToString();
        }

        /// <summary>
        /// Builds the "closes in Xm" fragment only when the lobby reports zero players and a kill deadline.
        /// </summary>
        /// <param name="summary">Lobby row from the last UGS query (cached locally).</param>
        /// <param name="richText">TMP fragment including color tags; empty when not shown.</param>
        /// <returns>True when the countdown should appear in the footer.</returns>
        static bool TryFormatEmptyIdleCountdown(
            TitanOrbitLobbyService.LobbySummary summary,
            out string richText)
        {
            // --- TryFormatEmptyIdleCountdown ---
            richText = string.Empty;
            if (summary == null)
                return false;

            // [TITAN-ORBIT] Only empty matches idle-kill; occupied conquest maps never show this.
            if (summary.CurrentPlayers > 0)
                return false;
            if (summary.IdleKillAtEpochSeconds <= 0)
                return false;

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long remaining = Math.Max(0, summary.IdleKillAtEpochSeconds - now);
            richText = "<color=#e8a87c><b>closes in " + FormatCountdownDuration(remaining) + "</b></color>";
            return true;
        }

        /// <summary>Formats a remaining-seconds budget as short UI text (e.g. 29m, 1h 5m, 45s).</summary>
        /// <param name="remainingSeconds">Seconds until idle kill; clamped at zero by caller.</param>
        static string FormatCountdownDuration(long remainingSeconds)
        {
            // --- FormatCountdownDuration ---
            if (remainingSeconds < 60)
                return remainingSeconds + "s";
            if (remainingSeconds < 3600)
            {
                long minutes = remainingSeconds / 60;
                long seconds = remainingSeconds % 60;
                // Under 10 minutes show seconds so the last stretch feels live.
                return minutes < 10 && seconds > 0
                    ? minutes + "m " + seconds + "s"
                    : minutes + "m";
            }

            long hours = remainingSeconds / 3600;
            long hourMinutes = (remainingSeconds % 3600) / 60;
            return hourMinutes > 0 ? hours + "h " + hourMinutes + "m" : hours + "h";
        }

        void ApplyRowSelectionVisuals()
        {
            for (int i = 0; i < _rowBackgrounds.Count; i++)
            {
                if (_rowBackgrounds[i] == null)
                    continue;
                _rowBackgrounds[i].color = i == _selectedRowIndex ? RowSelectedColor : RowNormalColor;
            }
        }

        /// <summary>
        /// Ticks lobby age labels and empty-idle countdown from cached <see cref="LobbySummary"/> data
        /// without another UGS query (countdown uses IdleKillAt epoch from the last fetch/heartbeat).
        /// </summary>
        void RefreshRowLiveLabels()
        {
            // --- RefreshRowLiveLabels ---
            int count = Mathf.Min(_cached.Count, Mathf.Min(_rowDurationLabels.Count, _rowExtrasLabels.Count));
            for (int i = 0; i < count; i++)
            {
                TitanOrbitLobbyService.LobbySummary summary = _cached[i];
                if (_rowDurationLabels[i] != null)
                    _rowDurationLabels[i].text = FormatLobbyActiveDuration(summary.CreatedAtEpochSeconds);
                if (_rowExtrasLabels[i] != null)
                    _rowExtrasLabels[i].text = FormatLobbyExtrasLine(summary);
            }
        }

        static string FormatLobbyActiveDuration(long createdAtEpochSeconds)
        {
            if (createdAtEpochSeconds <= 0)
                return "—";

            long elapsed = Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - createdAtEpochSeconds);
            if (elapsed < 60)
                return elapsed <= 1 ? "Just started" : $"{elapsed}s";
            if (elapsed < 3600)
                return $"{elapsed / 60}m";
            if (elapsed < 86400)
            {
                long hours = elapsed / 3600;
                long minutes = (elapsed % 3600) / 60;
                return minutes > 0 ? $"{hours}h {minutes}m" : $"{hours}h";
            }

            long days = elapsed / 86400;
            long dayHours = (elapsed % 86400) / 3600;
            return dayHours > 0 ? $"{days}d {dayHours}h" : $"{days}d";
        }

        void SelectRow(int index)
        {
            // --- SelectRow (legacy highlight only; Join is per-row) ---
            if (index < 0 || index >= _cached.Count)
                return;
            _selectedRowIndex = index;
            _selectedLobbyId = _cached[index].LobbyId;
            ApplyRowSelectionVisuals();
        }

        async Task QuickJoinAsync()
        {
            // --- QuickJoinAsync ---
            if (_joinInProgress || TitanOrbitSessionManager.Instance == null)
                return;
            _joinInProgress = true;
            SetStatus("Quick joining latest match...");
            SetRowJoinButtonsInteractable(false);
            if (_quickJoinButton != null)
                _quickJoinButton.interactable = false;
            try
            {
                bool ok = await TitanOrbitSessionManager.Instance.QuickJoinDedicatedAsync();
                if (!ok)
                {
                    SetStatus(TitanOrbitSessionManager.Instance.LastStatusMessage ?? "Quick join failed.");
                    return;
                }

                // --- Hand off to Loading Map ---
                // [TITAN-ORBIT] Leave Join Game before dedicated connect finishes so the lobby list
                // is not visible under the loading overlay (RefreshUi also dismisses as backup).
                DismissForLoading();
                SetStatus("Connecting to match...");
                if (!await WaitForDedicatedConnectionAsync(65f))
                {
                    // Connection failed — bring the browser back so the player can retry.
                    SetStatus(TitanOrbitSessionManager.Instance.LastStatusMessage ??
                              "Connection timed out. Tap Refresh — the server may be offline.");
                    if (!IsVisible)
                        Show();
                    return;
                }
            }
            finally
            {
                _joinInProgress = false;
                SetRowJoinButtonsInteractable(true);
                if (_quickJoinButton != null)
                    _quickJoinButton.interactable = true;
            }
        }

        /// <summary>
        /// Joins a specific dedicated lobby by id — used by each row's Join button.
        /// </summary>
        /// <param name="lobbyId">UGS lobby id from the row's <see cref="TitanOrbitLobbyService.LobbySummary"/>.</param>
        async Task JoinLobbyByIdAsync(string lobbyId)
        {
            // --- JoinLobbyByIdAsync ---
            if (_joinInProgress || string.IsNullOrWhiteSpace(lobbyId) ||
                TitanOrbitSessionManager.Instance == null)
                return;

            _joinInProgress = true;
            _selectedLobbyId = lobbyId;
            SetStatus("Joining...");
            SetRowJoinButtonsInteractable(false);
            if (_quickJoinButton != null)
                _quickJoinButton.interactable = false;
            try
            {
                bool ok = await TitanOrbitSessionManager.Instance.JoinDedicatedLobbyAsync(lobbyId);
                if (!ok)
                {
                    string detail = TitanOrbitSessionManager.Instance.LastStatusMessage;
                    SetStatus(string.IsNullOrEmpty(detail) ? "Join failed. Try Refresh or another lobby." : detail);
                    return;
                }

                // --- Hand off to Loading Map ---
                // [TITAN-ORBIT] Dismiss Join Game immediately so the lobby list is gone before
                // LoadingScreenControllerNce covers the canvas (not after WaitForDedicatedConnection).
                DismissForLoading();
                SetStatus("Connecting to match...");
                if (!await WaitForDedicatedConnectionAsync(65f))
                {
                    // Connection failed — restore the browser with the error status for retry.
                    SetStatus(TitanOrbitSessionManager.Instance.LastStatusMessage ??
                              "Connection timed out. Tap Refresh — the server may be offline.");
                    if (!IsVisible)
                        Show();
                    return;
                }
            }
            finally
            {
                _joinInProgress = false;
                SetRowJoinButtonsInteractable(true);
                if (_quickJoinButton != null)
                    _quickJoinButton.interactable = true;
            }
        }

        /// <summary>Enables or disables every visible row Join button (blocks double-join).</summary>
        void SetRowJoinButtonsInteractable(bool interactable)
        {
            for (int i = 0; i < _rowObjects.Count; i++)
            {
                GameObject row = _rowObjects[i];
                if (row == null)
                    continue;
                var joinButton = row.transform.Find("LobbyRowMain/LobbyRowFooter/LobbyRowJoin")
                                     ?.GetComponent<Button>();
                if (joinButton != null)
                    joinButton.interactable = interactable;
            }
        }

        static async Task<bool> WaitForDedicatedConnectionAsync(float timeoutSeconds)
        {
            // --- WaitForDedicatedConnectionAsync ---
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (TitanOrbitSessionManager.Instance != null &&
                    TitanOrbitSessionManager.Instance.IsInGame &&
                    EcsGameBridge.IsNetworkInGame())
                {
                    return true;
                }

                if (!TitanOrbitSessionManager.IsDedicatedJoinConnecting)
                    return false;

                await Task.Yield();
            }

            return TitanOrbitSessionManager.Instance != null &&
                   TitanOrbitSessionManager.Instance.IsInGame &&
                   EcsGameBridge.IsNetworkInGame();
        }

        /// <summary>
        /// When the lobby list is empty, quietly asks the dedicated fleet to publish a match.
        /// There is no Request Match button — this runs once per Show() when UGS returns zero lobbies.
        /// </summary>
        async Task AutoRequestMatchOnceAsync()
        {
            // --- AutoRequestMatchOnceAsync ---
            if (_requestInProgress)
                return;

            _requestInProgress = true;
            SetStatus("No matches listed — asking the dedicated server to publish one…");
            try
            {
                bool ok = await TitanOrbitLobbyService.RequestDedicatedMatchCreationAsync();
                if (!ok)
                {
                    SetStatus("Could not start a match automatically. Tap Refresh to try again.");
                    return;
                }

                for (int attempt = 0; attempt < RequestMatchPollAttempts; attempt++)
                {
                    await Task.Delay(RequestMatchPollIntervalMs);
                    if (!IsVisible)
                        return;

                    await RefreshAsync(silent: true);
                    if (_cached.Count > 0)
                    {
                        SetStatus("A dedicated match is ready. Tap Join on a row, or Quick join latest.");
                        return;
                    }
                }

                SetStatus("Still waiting for a dedicated match. Tap Refresh — the server may be offline.");
            }
            finally
            {
                _requestInProgress = false;
            }
        }

        void SetStatus(string message)
        {
            if (_statusText != null)
                _statusText.text = message;
            RefreshBuildIdLabel();
        }

        /// <summary>Host line for the lobby footer — shows where Join actually UDP-connects.</summary>
        static string FormatLobbyHostFragment(TitanOrbitLobbyService.LobbySummary summary)
        {
            if (summary == null || string.IsNullOrWhiteSpace(summary.HostAddress))
                return "<color=#e08a6a><b>host (missing)</b></color>";

            string endpoint = summary.HostAddress.Trim() + ":" + summary.HostPort;
            bool loopback = summary.HostAddress.StartsWith("127.") ||
                            string.Equals(summary.HostAddress, "localhost", StringComparison.OrdinalIgnoreCase);
            bool dummyEdgegap = summary.HostAddress.StartsWith("162.254.");
            string color = loopback ? "#8fd4a8" : dummyEdgegap ? "#e08a6a" : "#e0c06a";
            string note = loopback ? " local Docker" : dummyEdgegap ? " DUMMY — rebuild Edgegap server" : " public (GCE/cloud)";
            return "<color=" + color + "><b>host " + endpoint + "</b>" + note + "</color>";
        }

        /// <summary>
        /// Always-visible bake id. A missing server id means the live lobby process predates this field.
        /// </summary>
        void RefreshBuildIdLabel()
        {
            if (_buildIdText == null)
                return;

            string serverId = null;
            int serverHz = -1;
            for (int i = 0; i < _cached.Count; i++)
            {
                if (_cached[i] == null)
                    continue;
                if (_cached[i].IsLatest && !string.IsNullOrWhiteSpace(_cached[i].ServerBuildId))
                {
                    serverId = _cached[i].ServerBuildId;
                    serverHz = _cached[i].ServerSimHz;
                    break;
                }

                if (serverId == null && !string.IsNullOrWhiteSpace(_cached[i].ServerBuildId))
                {
                    serverId = _cached[i].ServerBuildId;
                    serverHz = _cached[i].ServerSimHz;
                }
            }

            string simLine = FormatServerSimHzLine(serverHz);

            if (serverId == null && _cached.Count > 0)
            {
                _buildIdText.text =
                    "This client: " + TitanOrbitBuildStamp.LocalLabel() +
                    "\n<color=#e08a6a>Server: (not published — old binary, rebuild + deploy)</color>" +
                    simLine;
                return;
            }

            if (serverId == null)
            {
                _buildIdText.text =
                    "This client: " + TitanOrbitBuildStamp.LocalLabel() +
                    "\nServer: (no lobby listed yet)" +
                    simLine;
                return;
            }

            bool sameBake = TitanOrbitBuildStamp.SameBake(TitanOrbitBuildStamp.Id, serverId);
            string gceColor = sameBake ? "#8fd4a8" : "#e08a6a";
            string gceNote = sameBake
                ? ""
                : " — different bake than this Editor (deploy did not land)";
            _buildIdText.text =
                "This client: " + TitanOrbitBuildStamp.LocalLabel() +
                "\n<color=" + gceColor + ">Server: " + TitanOrbitBuildStamp.FormatFriendly(serverId) +
                gceNote + "</color>" +
                simLine;
        }

        /// <summary>Sim Hz packed into lobby <c>ServerBuild</c> as <c>stamp@Hz</c>. Empty until the first sample.</summary>
        static string FormatServerSimHzLine(int serverHz)
        {
            if (serverHz < 0)
                return string.Empty;
            if (serverHz == 0)
                return "\nServer sim: (measuring…)";
            if (serverHz < 12)
                return "\n<color=#e08a6a>Server sim: " + serverHz +
                       " Hz wall — live from this server (stamps can match and still snap)</color>";
            if (serverHz < 50)
                return "\n<color=#e0c06a>Server sim: " + serverHz + " Hz — below 60</color>";
            return "\n<color=#8fd4a8>Server sim: " + serverHz + " Hz</color>";
        }

        static void ApplyContentColumnLayout(LayoutElement layoutElement)
        {
            if (layoutElement == null)
                return;
            layoutElement.preferredWidth = ContentWidth;
            layoutElement.minWidth = Mathf.Min(300f, ContentWidth);
            layoutElement.flexibleWidth = 0f;
        }

        /// <summary>
        /// Builds the inactive lobby-row template: header, up to five team cards, and a footer
        /// with extras, player count, and a per-match Join button.
        /// Instantiated once per listed lobby in <see cref="InstantiateLobbyRow"/>.
        /// </summary>
        GameObject CreateLobbyRowPrefab()
        {
            // --- Row shell (visual card; Join is a child button in the footer) ---
            var rowObj = new GameObject("LobbyListRowPrefab",
                typeof(RectTransform), typeof(Image), typeof(LayoutElement), typeof(VerticalLayoutGroup));
            var rect = rowObj.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(ContentWidth - 32f, RowHeight);

            var image = rowObj.GetComponent<Image>();
            image.color = RowNormalColor;
            image.raycastTarget = true;

            var rowVlg = rowObj.GetComponent<VerticalLayoutGroup>();
            rowVlg.padding = new RectOffset(14, 14, 10, 10);
            rowVlg.spacing = 8f;
            rowVlg.childAlignment = TextAnchor.UpperLeft;
            rowVlg.childControlWidth = true;
            rowVlg.childControlHeight = true;
            rowVlg.childForceExpandWidth = true;
            rowVlg.childForceExpandHeight = false;

            var layoutElement = rowObj.GetComponent<LayoutElement>();
            layoutElement.minHeight = 150f;
            layoutElement.preferredHeight = RowHeight;
            layoutElement.preferredWidth = ContentWidth - 32f;
            layoutElement.flexibleWidth = 0f;

            // --- Main column: header → team cards → footer ---
            var mainCol = CreateChild("LobbyRowMain", rowObj.transform,
                typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(LayoutElement));
            var mainVlg = mainCol.GetComponent<VerticalLayoutGroup>();
            mainVlg.padding = new RectOffset(0, 0, 0, 0);
            mainVlg.spacing = 8f;
            mainVlg.childAlignment = TextAnchor.UpperLeft;
            mainVlg.childControlWidth = true;
            mainVlg.childControlHeight = true;
            mainVlg.childForceExpandWidth = true;
            mainVlg.childForceExpandHeight = false;
            var mainLe = mainCol.GetComponent<LayoutElement>();
            mainLe.flexibleWidth = 1f;
            mainLe.minWidth = 220f;

            // --- Header: lobby name (left) + age (right) ---
            var header = CreateChild("LobbyRowHeader", mainCol.transform,
                typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
            var headerH = header.GetComponent<HorizontalLayoutGroup>();
            headerH.spacing = 10f;
            headerH.childAlignment = TextAnchor.MiddleLeft;
            headerH.childControlWidth = true;
            headerH.childControlHeight = true;
            headerH.childForceExpandWidth = false;
            headerH.childForceExpandHeight = false;
            var headerLe = header.GetComponent<LayoutElement>();
            headerLe.minHeight = 26f;
            headerLe.preferredHeight = 28f;
            headerLe.flexibleWidth = 1f;

            var nameLabel = CreateStyledLabel("LobbyRowName", "Lobby", header.transform, 20f,
                FontStyles.Normal, TextAlignmentOptions.MidlineLeft);
            nameLabel.enableWordWrapping = false;
            nameLabel.richText = true;
            nameLabel.overflowMode = TextOverflowModes.Ellipsis;
            var nameLe = nameLabel.gameObject.AddComponent<LayoutElement>();
            nameLe.flexibleWidth = 1f;
            nameLe.minHeight = 24f;
            nameLe.preferredHeight = 28f;

            var durationLabel = CreateStyledLabel("LobbyRowDuration", "—", header.transform, 15f,
                FontStyles.Normal, TextAlignmentOptions.MidlineRight);
            durationLabel.color = DurationLabelColor;
            var durationLe = durationLabel.gameObject.AddComponent<LayoutElement>();
            durationLe.preferredWidth = 72f;
            durationLe.minWidth = 56f;
            durationLe.flexibleWidth = 0f;

            // --- Team cards row (one small panel per active team) ---
            // [TITAN-ORBIT] Colors match TeamIdExtensions.ToColor — same palette as minimap / ships.
            var teamsRow = CreateChild("LobbyRowTeams", mainCol.transform,
                typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
            var teamsH = teamsRow.GetComponent<HorizontalLayoutGroup>();
            teamsH.spacing = 8f;
            teamsH.childAlignment = TextAnchor.MiddleLeft;
            teamsH.childControlWidth = true;
            teamsH.childControlHeight = true;
            teamsH.childForceExpandWidth = true;
            teamsH.childForceExpandHeight = false;
            var teamsLe = teamsRow.GetComponent<LayoutElement>();
            teamsLe.minHeight = 78f;
            teamsLe.preferredHeight = 86f;
            teamsLe.flexibleWidth = 1f;

            for (int i = 0; i < MaxTeamSlots; i++)
                CreateTeamCardPrefab("TeamCard" + i, teamsRow.transform);

            // Shown when map heartbeat has not published team meta yet.
            var pendingLabel = CreateStyledLabel("LobbyRowTeamsPending", "Map stats pending…", mainCol.transform,
                15f, FontStyles.Normal, TextAlignmentOptions.MidlineLeft);
            pendingLabel.richText = true;
            pendingLabel.color = MutedLabelColor;
            var pendingLe = pendingLabel.gameObject.AddComponent<LayoutElement>();
            pendingLe.minHeight = 24f;
            pendingLe.preferredHeight = 28f;
            pendingLe.flexibleWidth = 1f;

            // --- Footer: map extras + player total + Join for this match ---
            // [TITAN-ORBIT] Extras text can grow to "333 x 444 map | N free worlds | M asteroids".
            var footer = CreateChild("LobbyRowFooter", mainCol.transform,
                typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
            var footerH = footer.GetComponent<HorizontalLayoutGroup>();
            footerH.spacing = 10f;
            footerH.childAlignment = TextAnchor.MiddleLeft;
            footerH.childControlWidth = true;
            footerH.childControlHeight = true;
            footerH.childForceExpandWidth = false;
            footerH.childForceExpandHeight = false;
            var footerLe = footer.GetComponent<LayoutElement>();
            footerLe.minHeight = 40f;
            footerLe.preferredHeight = 44f;
            footerLe.flexibleWidth = 1f;

            var extrasLabel = CreateStyledLabel("LobbyRowExtras", "— map", footer.transform, 14f,
                FontStyles.Normal, TextAlignmentOptions.MidlineLeft);
            extrasLabel.richText = true;
            extrasLabel.enableWordWrapping = false;
            extrasLabel.overflowMode = TextOverflowModes.Ellipsis;
            extrasLabel.color = new Color(0.78f, 0.86f, 0.94f, 1f);
            var extrasLe = extrasLabel.gameObject.AddComponent<LayoutElement>();
            extrasLe.flexibleWidth = 1f;
            extrasLe.minWidth = 180f;

            var playersLabel = CreateStyledLabel("LobbyRowPlayers", "0/0", footer.transform, 16f,
                FontStyles.Bold, TextAlignmentOptions.MidlineRight);
            playersLabel.color = new Color(0.82f, 0.9f, 0.98f, 1f);
            var playersLe = playersLabel.gameObject.AddComponent<LayoutElement>();
            playersLe.preferredWidth = 64f;
            playersLe.minWidth = 48f;
            playersLe.flexibleWidth = 0f;

            // [TITAN-ORBIT] Join lives on the row so players do not select-then-join from a footer.
            CreateMenuButton("LobbyRowJoin", "Join", footer.transform, new Vector2(110f, 40f), true);

            return rowObj;
        }

        /// <summary>
        /// Creates one inactive team info card (accent bar + title + worlds + players).
        /// Filled later by <see cref="ApplyTeamCards"/>.
        /// </summary>
        static void CreateTeamCardPrefab(string name, Transform parent)
        {
            // --- Card shell ---
            var card = CreateChild(name, parent,
                typeof(RectTransform), typeof(Image), typeof(HorizontalLayoutGroup), typeof(LayoutElement), typeof(Outline));
            var cardImage = card.GetComponent<Image>();
            cardImage.color = TeamCardBgColor;
            cardImage.raycastTarget = false;

            var outline = card.GetComponent<Outline>();
            outline.effectColor = new Color(0.35f, 0.48f, 0.65f, 0.35f);
            outline.effectDistance = new Vector2(1f, -1f);

            var cardH = card.GetComponent<HorizontalLayoutGroup>();
            cardH.padding = new RectOffset(0, 8, 6, 6);
            cardH.spacing = 8f;
            cardH.childAlignment = TextAnchor.MiddleLeft;
            cardH.childControlWidth = true;
            cardH.childControlHeight = true;
            cardH.childForceExpandWidth = false;
            cardH.childForceExpandHeight = true;

            var cardLe = card.GetComponent<LayoutElement>();
            cardLe.flexibleWidth = 1f;
            cardLe.minWidth = 96f;
            cardLe.preferredWidth = 118f;
            cardLe.minHeight = 72f;
            cardLe.preferredHeight = 82f;

            // --- Colored accent strip (team identity at a glance) ---
            var accent = CreateChild("TeamAccent", card.transform,
                typeof(RectTransform), typeof(Image), typeof(LayoutElement));
            accent.GetComponent<Image>().color = Color.white;
            accent.GetComponent<Image>().raycastTarget = false;
            var accentLe = accent.GetComponent<LayoutElement>();
            accentLe.preferredWidth = 5f;
            accentLe.minWidth = 5f;
            accentLe.flexibleWidth = 0f;
            accentLe.flexibleHeight = 1f;

            // --- Text column ---
            var body = CreateChild("TeamCardBody", card.transform,
                typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(LayoutElement));
            var bodyV = body.GetComponent<VerticalLayoutGroup>();
            bodyV.spacing = 2f;
            bodyV.childAlignment = TextAnchor.MiddleLeft;
            bodyV.childControlWidth = true;
            bodyV.childControlHeight = true;
            bodyV.childForceExpandWidth = true;
            bodyV.childForceExpandHeight = false;
            var bodyLe = body.GetComponent<LayoutElement>();
            bodyLe.flexibleWidth = 1f;
            bodyLe.minWidth = 72f;

            var title = CreateStyledLabel("TeamTitle", "Team A", body.transform, 14f,
                FontStyles.Normal, TextAlignmentOptions.MidlineLeft);
            title.richText = true;
            title.enableWordWrapping = false;
            title.overflowMode = TextOverflowModes.Ellipsis;
            var titleLe = title.gameObject.AddComponent<LayoutElement>();
            titleLe.minHeight = 18f;
            titleLe.preferredHeight = 20f;

            var worlds = CreateStyledLabel("TeamWorlds", "— worlds", body.transform, 13f,
                FontStyles.Normal, TextAlignmentOptions.MidlineLeft);
            worlds.color = new Color(0.78f, 0.86f, 0.94f, 0.95f);
            var worldsLe = worlds.gameObject.AddComponent<LayoutElement>();
            worldsLe.minHeight = 16f;
            worldsLe.preferredHeight = 18f;

            var players = CreateStyledLabel("TeamPlayers", "— players", body.transform, 13f,
                FontStyles.Normal, TextAlignmentOptions.MidlineLeft);
            players.color = new Color(0.72f, 0.82f, 0.92f, 0.95f);
            var playersLe = players.gameObject.AddComponent<LayoutElement>();
            playersLe.minHeight = 16f;
            playersLe.preferredHeight = 18f;

            card.SetActive(false);
        }

        static GameObject CreateChild(string name, Transform parent, params Type[] components)
        {
            // --- Create instance ---
            var go = new GameObject(name, components);
            go.transform.SetParent(parent, false);
            return go;
        }

        static TextMeshProUGUI CreateStyledLabel(string name, string text, Transform parent, float fontSize,
            FontStyles style, TextAlignmentOptions alignment)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);
            var label = go.GetComponent<TextMeshProUGUI>();
            if (TMP_Settings.defaultFontAsset != null)
                label.font = TMP_Settings.defaultFontAsset;
            label.text = text;
            label.fontSize = fontSize;
            label.fontStyle = style;
            label.alignment = alignment;
            label.color = Color.white;
            label.raycastTarget = false;
            return label;
        }

        /// <summary>
        /// Creates a Cut Frame–styled menu button matching the Main Menu Play look.
        /// </summary>
        /// <param name="primary">Reserved for call-site clarity; all buttons share the Play Cut Frame style.</param>
        static Button CreateMenuButton(string name, string label, Transform parent, Vector2 size, bool primary)
        {
            // --- Create Cut Frame button ---
            // [TITAN-ORBIT] Style comes from the scene PlayButton via MainMenuPresenter (same as Main Menu).
            _ = primary;
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.sizeDelta = size;

            MainMenuPresenter.StyleGameObjectAsMenuButton(go, label, size.y, size.x);

            bool inHorizontalLayout = parent.GetComponent<HorizontalLayoutGroup>() != null;
            bool inVerticalLayout = parent.GetComponent<VerticalLayoutGroup>() != null;
            if (inHorizontalLayout || inVerticalLayout)
            {
                var le = go.GetComponent<LayoutElement>();
                if (le == null)
                    le = go.AddComponent<LayoutElement>();
                le.preferredWidth = size.x;
                le.preferredHeight = size.y;
                le.minWidth = Mathf.Max(80f, size.x * 0.5f);
                le.minHeight = size.y;
                // Action row: share width evenly. Row Join: fixed width so extras text keeps room.
                bool isRowJoin = name == "LobbyRowJoin";
                le.flexibleWidth = inHorizontalLayout && !isRowJoin ? 1f : 0f;
                le.flexibleHeight = 0f;
            }

            return go.GetComponent<Button>();
        }
    }
}
