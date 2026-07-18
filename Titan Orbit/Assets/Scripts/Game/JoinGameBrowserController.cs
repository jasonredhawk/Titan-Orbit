using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using TitanOrbit.Core;
using TitanOrbit.NetCode;
using TitanOrbit.Services;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.Game
{
    /// <summary>
    /// [HYBRID] Join Game screen — programmatic UGUI overlay listing Unity Gaming Services (UGS) dedicated
    /// lobbies and connecting the client via Relay. Opened from <see cref="NceGameFlowController"/> main menu.
    /// Client only; dedicated server builds have no canvas. Visual layout mirrors the pre-ECS lobby browser panel.
    /// </summary>
    public class JoinGameBrowserController : MonoBehaviour
    {
        const float ContentWidth = 680f;
        // Tall enough for title row + colored team/planet meta strip.
        const float RowHeight = 112f;
        const float AutoRefreshIntervalSeconds = 45f;
        const float CacheGraceSeconds = 180f;
        const float RowDurationRefreshSeconds = 1f;
        const int RequestMatchPollAttempts = 18;
        const int RequestMatchPollIntervalMs = 5000;

        static readonly Color RowNormalColor = new Color(0.11f, 0.17f, 0.28f, 0.98f);
        static readonly Color RowSelectedColor = new Color(0.18f, 0.38f, 0.62f, 0.98f);
        static readonly Color MutedLabelColor = new Color(0.68f, 0.78f, 0.9f, 0.92f);
        static readonly Color DurationLabelColor = new Color(0.62f, 0.74f, 0.86f, 0.88f);

        [SerializeField] GameObject mainMenuPanel;

        GameObject _screenRoot;
        GameObject _lobbyBrowserRoot;
        GameObject _lobbyRowPrefab;
        Transform _listContainer;
        TextMeshProUGUI _statusText;
        Button _joinButton;
        Button _refreshButton;
        Button _requestMatchButton;

        readonly List<TitanOrbitLobbyService.LobbySummary> _cached = new List<TitanOrbitLobbyService.LobbySummary>();
        readonly List<GameObject> _rowObjects = new List<GameObject>();
        readonly List<Image> _rowBackgrounds = new List<Image>();
        readonly List<TextMeshProUGUI> _rowDurationLabels = new List<TextMeshProUGUI>();
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

        public void Configure(GameObject menuPanel) => mainMenuPanel = menuPanel;

        public void Show()
        {
            // --- Show ---
            // Rebuild if an older layout is still cached (missing bordered panel or meta strip).
            bool needsRebuild = _screenRoot != null &&
                                (_lobbyScroll == null ||
                                 _lobbyBrowserRoot == null ||
                                 _lobbyRowPrefab == null ||
                                 _lobbyRowPrefab.transform.Find("LobbyRowMeta") == null);
            if (needsRebuild)
            {
                Destroy(_screenRoot);
                _screenRoot = null;
                _lobbyBrowserRoot = null;
                _lobbyRowPrefab = null;
                _listContainer = null;
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
            // --- Hide ---
            if (_screenRoot != null)
                _screenRoot.SetActive(false);
            if (mainMenuPanel != null)
                mainMenuPanel.SetActive(true);
        }

        void Update()
        {
            // --- Per-frame refresh ---
            if (!IsVisible || _refreshInProgress || _joinInProgress)
                return;

            _autoRefreshTimer += Time.unscaledDeltaTime;
            if (TitanOrbitLobbyService.LobbyRateLimitRemainingSeconds > 0f)
                return;

            if (_autoRefreshTimer >= AutoRefreshIntervalSeconds)
            {
                _autoRefreshTimer = 0f;
                _ = RefreshAsync(silent: true);
            }

            // Live-update "5m" / "2h" labels without re-querying UGS.
            if (_cached.Count > 0)
            {
                _durationRefreshTimer += Time.unscaledDeltaTime;
                if (_durationRefreshTimer >= RowDurationRefreshSeconds)
                {
                    _durationRefreshTimer = 0f;
                    RefreshRowDurations();
                }
            }
        }

        void EnsureUi()
        {
            // --- Ensure setup ---
            if (_screenRoot != null)
                return;

            Transform host = ResolveUiHost();

            // Full-screen dim backdrop.
            _screenRoot = new GameObject("JoinGameScreen", typeof(RectTransform), typeof(Image));
            _screenRoot.transform.SetParent(host, false);
            var screenRt = _screenRoot.GetComponent<RectTransform>();
            screenRt.anchorMin = Vector2.zero;
            screenRt.anchorMax = Vector2.one;
            screenRt.offsetMin = Vector2.zero;
            screenRt.offsetMax = Vector2.zero;
            _screenRoot.GetComponent<Image>().color = new Color(0.02f, 0.04f, 0.08f, 1f);

            // --- Top bar: Back + centered title ---
            var topBar = CreateChild("TopBar", _screenRoot.transform, typeof(RectTransform), typeof(HorizontalLayoutGroup));
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

            var back = CreateMenuButton("Back", "Back", topBar.transform, new Vector2(120f, 44f), false);
            back.onClick.AddListener(Hide);

            var title = CreateStyledLabel("JoinGameTitle", "Join Game", topBar.transform, 24f, FontStyles.Bold,
                TextAlignmentOptions.Center);
            var titleLe = title.gameObject.AddComponent<LayoutElement>();
            titleLe.flexibleWidth = 1f;
            titleLe.minHeight = 36f;

            // --- Body: centered content column ---
            var body = CreateChild("JoinGameBody", _screenRoot.transform, typeof(RectTransform), typeof(VerticalLayoutGroup));
            var bodyRt = body.GetComponent<RectTransform>();
            bodyRt.anchorMin = Vector2.zero;
            bodyRt.anchorMax = Vector2.one;
            bodyRt.offsetMin = new Vector2(24f, 24f);
            bodyRt.offsetMax = new Vector2(-24f, -64f);
            var bodyLayout = body.GetComponent<VerticalLayoutGroup>();
            bodyLayout.spacing = 14f;
            bodyLayout.padding = new RectOffset(0, 0, 8, 8);
            bodyLayout.childAlignment = TextAnchor.UpperCenter;
            bodyLayout.childControlWidth = true;
            bodyLayout.childControlHeight = true;
            bodyLayout.childForceExpandWidth = false;
            bodyLayout.childForceExpandHeight = false;

            var contentColumn = CreateChild("JoinGameContentColumn", body.transform,
                typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(LayoutElement));
            ApplyContentColumnLayout(contentColumn.GetComponent<LayoutElement>());
            var columnV = contentColumn.GetComponent<VerticalLayoutGroup>();
            columnV.spacing = 12f;
            columnV.childAlignment = TextAnchor.UpperCenter;
            columnV.childControlWidth = true;
            columnV.childControlHeight = true;
            columnV.childForceExpandWidth = false;
            columnV.childForceExpandHeight = false;

            var quickJoin = CreateMenuButton("QuickJoinButton", "Quick join latest", contentColumn.transform,
                new Vector2(ContentWidth, 48f), true);
            quickJoin.onClick.AddListener(() => _ = QuickJoinAsync());

            BuildLobbyBrowserPanel(contentColumn.transform);

            _screenRoot.SetActive(false);
        }

        /// <summary>Bordered lobby list panel — title, status, scroll list, and action buttons.</summary>
        void BuildLobbyBrowserPanel(Transform parent)
        {
            _lobbyBrowserRoot = CreateChild("LobbyBrowserRoot", parent,
                typeof(RectTransform), typeof(Image), typeof(Outline), typeof(VerticalLayoutGroup), typeof(LayoutElement));
            var rootRt = _lobbyBrowserRoot.GetComponent<RectTransform>();
            rootRt.anchorMin = new Vector2(0.5f, 0.5f);
            rootRt.anchorMax = new Vector2(0.5f, 0.5f);
            rootRt.pivot = new Vector2(0.5f, 0.5f);
            rootRt.sizeDelta = new Vector2(ContentWidth, 460f);

            var rootImage = _lobbyBrowserRoot.GetComponent<Image>();
            rootImage.color = new Color(0.05f, 0.08f, 0.13f, 0.98f);
            rootImage.raycastTarget = false;
            var rootOutline = _lobbyBrowserRoot.GetComponent<Outline>();
            rootOutline.effectColor = new Color(0.28f, 0.48f, 0.72f, 0.45f);
            rootOutline.effectDistance = new Vector2(1.5f, -1.5f);

            var rootVlg = _lobbyBrowserRoot.GetComponent<VerticalLayoutGroup>();
            rootVlg.spacing = 10f;
            rootVlg.padding = new RectOffset(14, 14, 12, 12);
            rootVlg.childAlignment = TextAnchor.UpperLeft;
            rootVlg.childControlWidth = true;
            rootVlg.childControlHeight = true;
            rootVlg.childForceExpandWidth = false;
            rootVlg.childForceExpandHeight = false;

            var rootLe = _lobbyBrowserRoot.GetComponent<LayoutElement>();
            ApplyContentColumnLayout(rootLe);
            rootLe.minHeight = 220f;
            rootLe.preferredHeight = 340f;
            rootLe.flexibleHeight = 1f;

            var browserTitle = CreateStyledLabel("LobbyBrowserTitle", "Open matches", _lobbyBrowserRoot.transform,
                22f, FontStyles.Bold, TextAlignmentOptions.Left);
            browserTitle.color = new Color(0.92f, 0.95f, 1f, 1f);
            var titleLe = browserTitle.gameObject.AddComponent<LayoutElement>();
            titleLe.minHeight = 28f;
            titleLe.preferredHeight = 30f;
            ApplyContentColumnLayout(titleLe);

            _statusText = CreateStyledLabel("JoinGameStatus", "Loading lobbies...", _lobbyBrowserRoot.transform,
                17f, FontStyles.Normal, TextAlignmentOptions.Left);
            _statusText.color = MutedLabelColor;
            _statusText.enableWordWrapping = true;
            _statusText.overflowMode = TextOverflowModes.Ellipsis;
            var statusLe = _statusText.gameObject.AddComponent<LayoutElement>();
            statusLe.minHeight = 24f;
            statusLe.preferredHeight = 30f;
            ApplyContentColumnLayout(statusLe);

            var scrollRoot = CreateChild("LobbyScroll", _lobbyBrowserRoot.transform,
                typeof(RectTransform), typeof(Image), typeof(ScrollRect), typeof(LayoutElement));
            scrollRoot.GetComponent<Image>().color = new Color(0.04f, 0.065f, 0.1f, 0.98f);
            var scrollLe = scrollRoot.GetComponent<LayoutElement>();
            scrollLe.minHeight = 160f;
            scrollLe.preferredHeight = 260f;
            scrollLe.flexibleHeight = 1f;
            ApplyContentColumnLayout(scrollLe);

            var viewport = CreateChild("Viewport", scrollRoot.transform, typeof(RectTransform), typeof(Image), typeof(Mask));
            viewport.GetComponent<Image>().color = new Color(0.07f, 0.1f, 0.14f, 1f);
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

            var buttonRow = CreateChild("JoinGameButtons", _lobbyBrowserRoot.transform,
                typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
            var rowLayout = buttonRow.GetComponent<HorizontalLayoutGroup>();
            rowLayout.spacing = 10f;
            rowLayout.childAlignment = TextAnchor.MiddleCenter;
            rowLayout.childControlWidth = true;
            rowLayout.childControlHeight = false;
            rowLayout.childForceExpandWidth = true;
            rowLayout.childForceExpandHeight = false;
            var footerLe = buttonRow.GetComponent<LayoutElement>();
            footerLe.minHeight = 36f;
            footerLe.preferredHeight = 40f;
            ApplyContentColumnLayout(footerLe);

            _refreshButton = CreateMenuButton("RefreshLobbies", "Refresh", buttonRow.transform, new Vector2(160f, 36f), false);
            _refreshButton.onClick.AddListener(() => _ = RefreshAsync(silent: false));

            _joinButton = CreateMenuButton("JoinSelectedLobby", "Join", buttonRow.transform, new Vector2(160f, 36f), true);
            _joinButton.interactable = false;
            _joinButton.onClick.AddListener(() => _ = JoinSelectedAsync());

            _requestMatchButton = CreateMenuButton("RequestDedicatedMatch", "Request match", buttonRow.transform,
                new Vector2(180f, 36f), false);
            _requestMatchButton.onClick.AddListener(() => _ = RequestMatchAsync());

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

                if (kind == TitanOrbitLobbyService.OpenLobbyQueryResultKind.RateLimitBackoff)
                {
                    int waitSec = Mathf.Max(1, Mathf.CeilToInt(TitanOrbitLobbyService.LobbyRateLimitRemainingSeconds));
                    if (!silent)
                        SetStatus(_cached.Count > 0
                            ? $"Rate-limited. Showing previous list. Retry in ~{waitSec}s."
                            : $"Rate-limited. Wait ~{waitSec}s and tap Refresh.");
                    RenderList();
                    return;
                }

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

                if (fetched.Count == 0 &&
                    kind == TitanOrbitLobbyService.OpenLobbyQueryResultKind.Ok &&
                    !_autoRequestMatchSent &&
                    !_requestInProgress)
                {
                    _autoRequestMatchSent = true;
                    Debug.Log("[JoinGameBrowser] No lobbies listed — auto-requesting dedicated match.");
                    _ = AutoRequestMatchOnceAsync();
                }
            }
            finally
            {
                _refreshInProgress = false;
                if (_refreshButton != null)
                    _refreshButton.interactable = true;
                UpdateRequestButton();
            }
        }

        void ApplySummaries(List<TitanOrbitLobbyService.LobbySummary> fetched, bool silent)
        {
            // --- Apply changes ---
            _cached.Clear();
            _cached.AddRange(fetched);
            _selectedLobbyId = null;
            _selectedRowIndex = -1;
            if (_joinButton != null)
                _joinButton.interactable = false;
            RenderList();
            if (!silent)
            {
                string project = Application.cloudProjectId ?? "(none)";
                SetStatus(_cached.Count == 0
                    ? "No dedicated matches listed (project " + project + "). Tap Request match or Refresh."
                    : "Select a lobby, then tap Join selected.");
            }

            if (_cached.Count > 0 && string.IsNullOrWhiteSpace(_selectedLobbyId))
                SelectRow(0);

            UpdateRequestButton();
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

            if (_cached.Count == 0)
            {
                RebuildLobbyListLayout();
                UpdateRequestButton();
                return;
            }

            for (int i = 0; i < _cached.Count; i++)
            {
                var summary = _cached[i];
                var row = InstantiateLobbyRow(i, summary);
                if (row == null)
                    continue;

                int captured = i;
                var button = row.GetComponent<Button>();
                if (button != null)
                {
                    button.onClick.RemoveAllListeners();
                    button.onClick.AddListener(() => SelectRow(captured));
                }

                _rowObjects.Add(row);
                _rowBackgrounds.Add(row.GetComponent<Image>());
                var durationLabel = row.transform.Find("LobbyRowDuration")?.GetComponent<TextMeshProUGUI>();
                if (durationLabel != null)
                    _rowDurationLabels.Add(durationLabel);
            }

            ApplyRowSelectionVisuals();
            Debug.Log("[JoinGameBrowser] RenderList rows=" + _cached.Count +
                      (_cached.Count > 0 ? " first=\"" + _cached[0].Name + "\"" : ""));
            RebuildLobbyListLayout();
            UpdateRequestButton();
        }

        GameObject InstantiateLobbyRow(int index, TitanOrbitLobbyService.LobbySummary summary)
        {
            if (_lobbyRowPrefab == null || _listContainer == null)
                return null;

            var row = Instantiate(_lobbyRowPrefab, _listContainer);
            row.name = "LobbyRow" + index;
            row.SetActive(true);

            var nameLabel = row.transform.Find("LobbyRowMain/LobbyRowName")?.GetComponent<TextMeshProUGUI>()
                            ?? row.transform.Find("LobbyRowName")?.GetComponent<TextMeshProUGUI>();
            var metaLabel = row.transform.Find("LobbyRowMain/LobbyRowMeta")?.GetComponent<TextMeshProUGUI>()
                            ?? row.transform.Find("LobbyRowMeta")?.GetComponent<TextMeshProUGUI>();
            var durationLabel = row.transform.Find("LobbyRowDuration")?.GetComponent<TextMeshProUGUI>();
            var playersLabel = row.transform.Find("LobbyRowPlayers")?.GetComponent<TextMeshProUGUI>();

            // --- Title + freshness ---
            // [TITAN-ORBIT] "Latest" is the dedicated lobby the server is currently advertising as joinable.
            string latestTag = summary.IsLatest
                ? "  <size=15><color=#7ec8ff>● Latest</color></size>"
                : "  <size=15><color=#8a9bb0>● Older</color></size>";
            if (nameLabel != null)
                nameLabel.text = $"<b>{summary.Name}</b>{latestTag}";

            // --- Map meta strip (colored team chips + neutrals / asteroids) ---
            if (metaLabel != null)
            {
                string mapMetaLine = FormatLobbyMapMeta(summary);
                metaLabel.text = string.IsNullOrEmpty(mapMetaLine)
                    ? "<color=#6f8499>Map stats pending…</color>"
                    : mapMetaLine;
                metaLabel.gameObject.SetActive(true);
            }

            if (durationLabel != null)
                durationLabel.text = FormatLobbyActiveDuration(summary.CreatedAtEpochSeconds);
            if (playersLabel != null)
                playersLabel.text = $"{summary.CurrentPlayers}/{summary.MaxPlayers}";

            return row;
        }

        /// <summary>
        /// Builds a rich-text map-stats strip for a lobby row from UGS lobby Data.
        /// Team planet counts use the same colors as <see cref="TeamIdExtensions.ToColor"/>.
        /// </summary>
        static string FormatLobbyMapMeta(TitanOrbitLobbyService.LobbySummary summary)
        {
            // --- Guard ---
            if (summary == null)
                return string.Empty;

            bool hasTeamPlanets = summary.MapTeamPlanetCounts != null && summary.MapTeamPlanetCounts.Length > 0;
            bool hasAny =
                summary.MapTeamCount >= 0 ||
                summary.MapNeutralPlanetCount >= 0 ||
                summary.MapAsteroidCount >= 0 ||
                hasTeamPlanets;
            if (!hasAny)
                return string.Empty;

            // --- Fancy strip ---
            // [TITAN-ORBIT] TMP rich text — colored dots match in-game team colors (minimap / ships).
            var sb = new StringBuilder(160);
            sb.Append("<size=15>");

            if (hasTeamPlanets)
            {
                sb.Append("<color=#b8c9dc>Worlds</color>  ");
                for (int i = 0; i < summary.MapTeamPlanetCounts.Length; i++)
                {
                    if (i > 0)
                        sb.Append("  ");
                    TeamId team = (TeamId)(i + 1);
                    string hex = ColorUtility.ToHtmlStringRGB(team.ToColor());
                    int count = summary.MapTeamPlanetCounts[i];
                    // Pill-like chip: colored bullet + count.
                    sb.Append("<color=#").Append(hex).Append(">●</color><b>").Append(count).Append("</b>");
                }
            }
            else if (summary.MapTeamCount >= 0)
            {
                sb.Append("<color=#b8c9dc>").Append(summary.MapTeamCount).Append(" teams</color>");
            }

            if (summary.MapNeutralPlanetCount >= 0)
            {
                if (sb.Length > "<size=15>".Length)
                    sb.Append("   <color=#5f738a>|</color>   ");
                sb.Append("<color=#9eb6cc>◇</color> <b>")
                    .Append(summary.MapNeutralPlanetCount)
                    .Append("</b><color=#9eb6cc> free</color>");
            }

            if (summary.MapAsteroidCount >= 0)
            {
                if (sb.Length > "<size=15>".Length)
                    sb.Append("   <color=#5f738a>|</color>   ");
                sb.Append("<color=#d4b06a>✦</color> <b>")
                    .Append(summary.MapAsteroidCount)
                    .Append("</b><color=#d4b06a> asteroids</color>");
            }

            sb.Append("</size>");
            return sb.ToString();
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

        void RefreshRowDurations()
        {
            int count = Mathf.Min(_rowDurationLabels.Count, _cached.Count);
            for (int i = 0; i < count; i++)
            {
                if (_rowDurationLabels[i] == null)
                    continue;
                _rowDurationLabels[i].text = FormatLobbyActiveDuration(_cached[i].CreatedAtEpochSeconds);
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
            // --- SelectRow ---
            if (index < 0 || index >= _cached.Count)
                return;
            _selectedRowIndex = index;
            _selectedLobbyId = _cached[index].LobbyId;
            if (_joinButton != null)
                _joinButton.interactable = true;
            ApplyRowSelectionVisuals();
            SetStatus("Selected: " + _cached[index].Name);
        }

        void UpdateRequestButton()
        {
            // --- Per-frame refresh ---
            if (_requestMatchButton == null)
                return;
            _requestMatchButton.gameObject.SetActive(true);
            _requestMatchButton.interactable = !_requestInProgress && !_refreshInProgress;
        }

        async Task QuickJoinAsync()
        {
            // --- QuickJoinAsync ---
            if (_joinInProgress || TitanOrbitSessionManager.Instance == null)
                return;
            _joinInProgress = true;
            SetStatus("Quick joining latest match...");
            try
            {
                bool ok = await TitanOrbitSessionManager.Instance.QuickJoinDedicatedAsync();
                if (!ok)
                {
                    SetStatus(TitanOrbitSessionManager.Instance.LastStatusMessage ?? "Quick join failed.");
                    return;
                }

                SetStatus("Connecting to match...");
                if (!await WaitForDedicatedConnectionAsync(65f))
                {
                    SetStatus(TitanOrbitSessionManager.Instance.LastStatusMessage ??
                              "Connection timed out. Tap Refresh — the server may be offline.");
                    return;
                }

                Hide();
            }
            finally
            {
                _joinInProgress = false;
            }
        }

        async Task JoinSelectedAsync()
        {
            // --- JoinSelectedAsync ---
            if (_joinInProgress || string.IsNullOrWhiteSpace(_selectedLobbyId) ||
                TitanOrbitSessionManager.Instance == null)
                return;

            _joinInProgress = true;
            SetStatus("Joining...");
            if (_joinButton != null)
                _joinButton.interactable = false;
            try
            {
                bool ok = await TitanOrbitSessionManager.Instance.JoinDedicatedLobbyAsync(_selectedLobbyId);
                if (!ok)
                {
                    string detail = TitanOrbitSessionManager.Instance.LastStatusMessage;
                    SetStatus(string.IsNullOrEmpty(detail) ? "Join failed. Try Refresh or another lobby." : detail);
                    return;
                }

                SetStatus("Connecting to match...");
                if (!await WaitForDedicatedConnectionAsync(65f))
                {
                    SetStatus(TitanOrbitSessionManager.Instance.LastStatusMessage ??
                              "Connection timed out. Tap Refresh — the server may be offline.");
                    return;
                }

                Hide();
            }
            finally
            {
                _joinInProgress = false;
                if (_joinButton != null)
                    _joinButton.interactable = !string.IsNullOrWhiteSpace(_selectedLobbyId);
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

        async Task AutoRequestMatchOnceAsync()
        {
            // --- AutoRequestMatchOnceAsync ---
            if (_requestInProgress)
                return;

            _requestInProgress = true;
            UpdateRequestButton();
            SetStatus("No matches listed — asking the dedicated server to publish one…");
            try
            {
                bool ok = await TitanOrbitLobbyService.RequestDedicatedMatchCreationAsync();
                if (!ok)
                {
                    SetStatus("Could not request a match. Tap Request match or Refresh.");
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
                        SetStatus("A dedicated match is ready. Select it and tap Join selected.");
                        return;
                    }
                }

                SetStatus("Still waiting for a dedicated match. Tap Request match or Refresh.");
            }
            finally
            {
                _requestInProgress = false;
                UpdateRequestButton();
            }
        }

        async Task RequestMatchAsync()
        {
            // --- RequestMatchAsync ---
            if (_requestInProgress)
                return;

            _requestInProgress = true;
            _autoRefreshTimer = 0f;
            UpdateRequestButton();
            SetStatus("Requesting a new dedicated match...");
            Debug.Log("[JoinGameBrowser] Request match clicked. project=" + (Application.cloudProjectId ?? "(none)"));

            try
            {
                bool ok = await TitanOrbitLobbyService.RequestDedicatedMatchCreationAsync();
                if (!ok)
                {
                    SetStatus("Could not request a match. Check your connection and try again.");
                    return;
                }

                SetStatus("Dedicated match requested. Waiting for the server to publish a lobby…");
                for (int attempt = 0; attempt < RequestMatchPollAttempts; attempt++)
                {
                    await Task.Delay(RequestMatchPollIntervalMs);
                    if (!IsVisible)
                        return;

                    await RefreshAsync(silent: true);
                    if (_cached.Count > 0)
                    {
                        SetStatus("A dedicated match is ready. Select it and tap Join selected.");
                        return;
                    }
                }

                SetStatus(
                    "Still waiting for a dedicated match. The headless server may be offline — keep this screen open or tap Refresh.");
            }
            finally
            {
                _requestInProgress = false;
                UpdateRequestButton();
            }
        }

        void SetStatus(string message)
        {
            if (_statusText != null)
                _statusText.text = message;
        }

        static void ApplyContentColumnLayout(LayoutElement layoutElement)
        {
            if (layoutElement == null)
                return;
            layoutElement.preferredWidth = ContentWidth;
            layoutElement.minWidth = Mathf.Min(300f, ContentWidth);
            layoutElement.flexibleWidth = 0f;
        }

        GameObject CreateLobbyRowPrefab()
        {
            var rowObj = new GameObject("LobbyListRowPrefab",
                typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement), typeof(HorizontalLayoutGroup));
            var rect = rowObj.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(ContentWidth - 32f, RowHeight);

            var image = rowObj.GetComponent<Image>();
            image.color = RowNormalColor;
            image.raycastTarget = true;

            var btn = rowObj.GetComponent<Button>();
            var colors = btn.colors;
            colors.normalColor = RowNormalColor;
            colors.highlightedColor = new Color(0.15f, 0.24f, 0.38f, 1f);
            colors.pressedColor = new Color(0.08f, 0.12f, 0.2f, 1f);
            colors.selectedColor = RowSelectedColor;
            colors.disabledColor = new Color(0.12f, 0.2f, 0.32f, 0.95f);
            btn.colors = colors;
            btn.transition = Selectable.Transition.ColorTint;

            var rowHlg = rowObj.GetComponent<HorizontalLayoutGroup>();
            rowHlg.padding = new RectOffset(16, 16, 12, 12);
            rowHlg.spacing = 14f;
            rowHlg.childAlignment = TextAnchor.MiddleLeft;
            rowHlg.childControlWidth = true;
            rowHlg.childControlHeight = true;
            rowHlg.childForceExpandWidth = false;
            rowHlg.childForceExpandHeight = true;

            var layoutElement = rowObj.GetComponent<LayoutElement>();
            layoutElement.minHeight = 96f;
            layoutElement.preferredHeight = RowHeight;
            layoutElement.preferredWidth = ContentWidth - 32f;
            layoutElement.flexibleWidth = 0f;

            // --- Left column: lobby title + colored map meta strip ---
            // [TITAN-ORBIT] Vertical stack keeps meta readable without crowding duration/players.
            var mainCol = CreateChild("LobbyRowMain", rowObj.transform,
                typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(LayoutElement));
            var mainVlg = mainCol.GetComponent<VerticalLayoutGroup>();
            mainVlg.padding = new RectOffset(0, 0, 0, 0);
            mainVlg.spacing = 6f;
            mainVlg.childAlignment = TextAnchor.MiddleLeft;
            mainVlg.childControlWidth = true;
            mainVlg.childControlHeight = true;
            mainVlg.childForceExpandWidth = true;
            mainVlg.childForceExpandHeight = false;
            var mainLe = mainCol.GetComponent<LayoutElement>();
            mainLe.flexibleWidth = 1f;
            mainLe.minWidth = 220f;
            mainLe.preferredHeight = 84f;

            var nameLabel = CreateStyledLabel("LobbyRowName", "Lobby", mainCol.transform, 21f,
                FontStyles.Normal, TextAlignmentOptions.MidlineLeft);
            nameLabel.enableWordWrapping = false;
            nameLabel.richText = true;
            nameLabel.overflowMode = TextOverflowModes.Ellipsis;
            var nameLe = nameLabel.gameObject.AddComponent<LayoutElement>();
            nameLe.flexibleWidth = 1f;
            nameLe.minHeight = 26f;
            nameLe.preferredHeight = 28f;

            var metaLabel = CreateStyledLabel("LobbyRowMeta", "Map stats…", mainCol.transform, 15f,
                FontStyles.Normal, TextAlignmentOptions.TopLeft);
            metaLabel.enableWordWrapping = true;
            metaLabel.richText = true;
            metaLabel.overflowMode = TextOverflowModes.Ellipsis;
            metaLabel.color = new Color(0.78f, 0.86f, 0.94f, 1f);
            var metaLe = metaLabel.gameObject.AddComponent<LayoutElement>();
            metaLe.flexibleWidth = 1f;
            metaLe.minHeight = 36f;
            metaLe.preferredHeight = 44f;

            var durationLabel = CreateStyledLabel("LobbyRowDuration", "—", rowObj.transform, 16f,
                FontStyles.Normal, TextAlignmentOptions.MidlineRight);
            durationLabel.color = DurationLabelColor;
            var durationLe = durationLabel.gameObject.AddComponent<LayoutElement>();
            durationLe.preferredWidth = 78f;
            durationLe.minWidth = 64f;
            durationLe.flexibleWidth = 0f;
            durationLe.preferredHeight = 44f;

            var playersLabel = CreateStyledLabel("LobbyRowPlayers", "0/0", rowObj.transform, 20f,
                FontStyles.Bold, TextAlignmentOptions.MidlineRight);
            playersLabel.color = new Color(0.82f, 0.9f, 0.98f, 1f);
            var playersLe = playersLabel.gameObject.AddComponent<LayoutElement>();
            playersLe.preferredWidth = 64f;
            playersLe.minWidth = 52f;
            playersLe.flexibleWidth = 0f;
            playersLe.preferredHeight = 44f;

            return rowObj;
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

        static Button CreateMenuButton(string name, string label, Transform parent, Vector2 size, bool primary)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.sizeDelta = size;

            var image = go.GetComponent<Image>();
            image.color = primary
                ? new Color(0.22f, 0.52f, 0.88f, 0.95f)
                : new Color(0.16f, 0.22f, 0.32f, 0.92f);
            image.raycastTarget = true;

            var btn = go.GetComponent<Button>();
            btn.targetGraphic = image;
            var colors = btn.colors;
            colors.normalColor = image.color;
            colors.highlightedColor = primary ? new Color(0.32f, 0.62f, 0.98f, 1f) : new Color(0.24f, 0.32f, 0.44f, 1f);
            colors.pressedColor = primary ? new Color(0.14f, 0.38f, 0.72f, 1f) : new Color(0.12f, 0.2f, 0.32f, 1f);
            colors.selectedColor = colors.normalColor;
            colors.disabledColor = new Color(0.2f, 0.22f, 0.28f, 0.55f);
            colors.fadeDuration = 0.1f;
            btn.colors = colors;

            var textGo = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
            textGo.transform.SetParent(go.transform, false);
            var textRt = textGo.GetComponent<RectTransform>();
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.offsetMin = new Vector2(8f, 4f);
            textRt.offsetMax = new Vector2(-8f, -4f);
            var tmp = textGo.GetComponent<TextMeshProUGUI>();
            if (TMP_Settings.defaultFontAsset != null)
                tmp.font = TMP_Settings.defaultFontAsset;
            tmp.text = label;
            tmp.fontSize = size.y <= 36f ? (primary ? 18f : 16f) : (primary ? 22f : 18f);
            tmp.fontStyle = FontStyles.Bold;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = new Color(0.96f, 0.98f, 1f, 1f);
            tmp.enableWordWrapping = true;
            tmp.raycastTarget = false;

            bool inHorizontalLayout = parent.GetComponent<HorizontalLayoutGroup>() != null;
            bool inVerticalLayout = parent.GetComponent<VerticalLayoutGroup>() != null;
            if (inHorizontalLayout || inVerticalLayout)
            {
                var le = go.AddComponent<LayoutElement>();
                le.preferredWidth = size.x;
                le.preferredHeight = size.y;
                le.minWidth = Mathf.Max(80f, size.x * 0.5f);
                le.minHeight = size.y;
                le.flexibleWidth = inHorizontalLayout ? 1f : 0f;
                le.flexibleHeight = 0f;
            }

            return btn;
        }
    }
}
