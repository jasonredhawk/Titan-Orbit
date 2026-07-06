using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using TitanOrbit.NetCode;
using TitanOrbit.Services;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.Game
{
    /// <summary>Join Game screen: lists dedicated UGS lobbies and connects via Relay.</summary>
    public class JoinGameBrowserController : MonoBehaviour
    {
        const float ContentWidth = 540f;
        const float RowHeight = 72f;
        const float AutoRefreshIntervalSeconds = 45f;
        const float CacheGraceSeconds = 180f;
        const int RequestMatchPollAttempts = 18;
        const int RequestMatchPollIntervalMs = 5000;

        [SerializeField] GameObject mainMenuPanel;

        GameObject _screenRoot;
        Transform _listContainer;
        TextMeshProUGUI _statusText;
        Button _joinButton;
        Button _refreshButton;
        Button _requestMatchButton;

        readonly List<TitanOrbitLobbyService.LobbySummary> _cached = new List<TitanOrbitLobbyService.LobbySummary>();
        readonly List<GameObject> _rowObjects = new List<GameObject>();
        string _selectedLobbyId;
        int _selectedRowIndex = -1;
        bool _refreshInProgress;
        bool _joinInProgress;
        bool _requestInProgress;
        float _lastSuccessfulFetch = -1f;
        float _autoRefreshTimer;

        ScrollRect _lobbyScroll;

        public bool IsVisible => _screenRoot != null && _screenRoot.activeSelf;

        public void Configure(GameObject menuPanel) => mainMenuPanel = menuPanel;

        public void Show()
        {
            if (_screenRoot != null && _lobbyScroll == null)
            {
                Destroy(_screenRoot);
                _screenRoot = null;
                _listContainer = null;
            }

            EnsureUi();
            if (mainMenuPanel != null)
                mainMenuPanel.SetActive(false);
            _screenRoot.SetActive(true);
            _screenRoot.transform.SetAsLastSibling();
            _autoRefreshTimer = 0f;
            Debug.Log("[JoinGameBrowser] Opening join browser — refreshing lobby list.");
            _ = ShowAndRefreshAsync();
        }

        async Task ShowAndRefreshAsync()
        {
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
            if (_screenRoot != null)
                _screenRoot.SetActive(false);
            if (mainMenuPanel != null)
                mainMenuPanel.SetActive(true);
        }

        void Update()
        {
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
        }

        void EnsureUi()
        {
            if (_screenRoot != null)
                return;

            Transform host = ResolveUiHost();

            _screenRoot = new GameObject("JoinGameScreen", typeof(RectTransform), typeof(Image));
            _screenRoot.transform.SetParent(host, false);
            var screenRt = _screenRoot.GetComponent<RectTransform>();
            screenRt.anchorMin = Vector2.zero;
            screenRt.anchorMax = Vector2.one;
            screenRt.offsetMin = Vector2.zero;
            screenRt.offsetMax = Vector2.zero;
            _screenRoot.GetComponent<Image>().color = new Color(0.02f, 0.04f, 0.08f, 1f);

            var topBar = CreateChild("TopBar", _screenRoot.transform, typeof(HorizontalLayoutGroup));
            var topRt = topBar.GetComponent<RectTransform>();
            topRt.anchorMin = new Vector2(0f, 1f);
            topRt.anchorMax = new Vector2(1f, 1f);
            topRt.pivot = new Vector2(0.5f, 1f);
            topRt.sizeDelta = new Vector2(0f, 56f);
            topRt.anchoredPosition = Vector2.zero;

            var back = CreateButton("Back", "Back", topBar.transform, new Vector2(120f, 44f), false);
            back.onClick.AddListener(Hide);

            var title = CreateLabel("JoinGameTitle", "Join Game", topBar.transform, 24f);

            var body = CreateChild("JoinGameBody", _screenRoot.transform, typeof(VerticalLayoutGroup));
            var bodyRt = body.GetComponent<RectTransform>();
            bodyRt.anchorMin = Vector2.zero;
            bodyRt.anchorMax = Vector2.one;
            bodyRt.offsetMin = new Vector2(24f, 24f);
            bodyRt.offsetMax = new Vector2(-24f, -64f);
            var bodyLayout = body.GetComponent<VerticalLayoutGroup>();
            bodyLayout.spacing = 12f;
            bodyLayout.childAlignment = TextAnchor.UpperCenter;
            bodyLayout.childControlWidth = true;
            bodyLayout.childForceExpandWidth = false;

            var quickJoin = CreateButton("QuickJoinButton", "Quick join latest", body.transform, new Vector2(ContentWidth, 48f), true);
            quickJoin.onClick.AddListener(() => _ = QuickJoinAsync());

            _statusText = CreateLabel("JoinGameStatus", "Loading lobbies...", body.transform, 18f);

            var scrollRoot = CreateChild("LobbyScroll", body.transform, typeof(Image), typeof(ScrollRect));
            scrollRoot.GetComponent<Image>().color = new Color(0.05f, 0.08f, 0.12f, 1f);
            var scrollLe = scrollRoot.AddComponent<LayoutElement>();
            scrollLe.minHeight = 280f;
            scrollLe.preferredHeight = 360f;
            scrollLe.preferredWidth = ContentWidth;
            scrollLe.flexibleHeight = 1f;
            scrollLe.flexibleWidth = 0f;

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
            vlg.spacing = 8f;
            vlg.padding = new RectOffset(8, 8, 8, 8);
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

            var buttonRow = CreateChild("JoinGameButtons", body.transform, typeof(HorizontalLayoutGroup));
            var rowLayout = buttonRow.GetComponent<HorizontalLayoutGroup>();
            rowLayout.spacing = 12f;
            rowLayout.childAlignment = TextAnchor.MiddleCenter;

            _refreshButton = CreateButton("RefreshLobbies", "Refresh", buttonRow.transform, new Vector2(160f, 44f), false);
            _refreshButton.onClick.AddListener(() => _ = RefreshAsync(silent: false));

            _joinButton = CreateButton("JoinSelectedLobby", "Join selected", buttonRow.transform, new Vector2(200f, 44f), true);
            _joinButton.interactable = false;
            _joinButton.onClick.AddListener(() => _ = JoinSelectedAsync());

            _requestMatchButton = CreateButton("RequestDedicatedMatch", "Request match", buttonRow.transform, new Vector2(180f, 44f), false);
            _requestMatchButton.onClick.AddListener(() => _ = RequestMatchAsync());

            _screenRoot.SetActive(false);
        }

        async Task RefreshAsync(bool silent)
        {
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

            for (int i = 0; i < _cached.Count; i++)
            {
                var summary = _cached[i];
                var row = CreateButton("LobbyRow" + i, FormatRowLabel(summary), _listContainer, new Vector2(ContentWidth, RowHeight), false);
                int captured = i;
                row.onClick.RemoveAllListeners();
                row.onClick.AddListener(() => SelectRow(captured));
                _rowObjects.Add(row.gameObject);
            }

            Debug.Log("[JoinGameBrowser] RenderList rows=" + _cached.Count +
                      (_cached.Count > 0 ? " first=\"" + _cached[0].Name + "\"" : ""));
            RebuildLobbyListLayout();
            UpdateRequestButton();
        }

        static string FormatRowLabel(TitanOrbitLobbyService.LobbySummary summary)
        {
            string latest = summary.IsLatest ? " · Latest" : " · Older match";
            string joinable = summary.IsLatest ? "" : " (join via Quick join if latest is full)";
            string duration = FormatDuration(summary.CreatedAtEpochSeconds);
            return $"{summary.Name}{latest}{joinable}\n{duration} · {summary.CurrentPlayers}/{summary.MaxPlayers} players";
        }

        static string FormatDuration(long createdAtEpochSeconds)
        {
            if (createdAtEpochSeconds <= 0)
                return "—";
            long ageSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - createdAtEpochSeconds;
            if (ageSec < 60)
                return ageSec + "s";
            if (ageSec < 3600)
                return (ageSec / 60) + "m";
            return (ageSec / 3600) + "h";
        }

        void SelectRow(int index)
        {
            if (index < 0 || index >= _cached.Count)
                return;
            _selectedRowIndex = index;
            _selectedLobbyId = _cached[index].LobbyId;
            if (_joinButton != null)
                _joinButton.interactable = true;
            SetStatus("Selected: " + _cached[index].Name);
        }

        void UpdateRequestButton()
        {
            if (_requestMatchButton == null)
                return;
            _requestMatchButton.gameObject.SetActive(true);
            _requestMatchButton.interactable = !_requestInProgress && !_refreshInProgress;
        }

        async Task QuickJoinAsync()
        {
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

        async Task RequestMatchAsync()
        {
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

        static GameObject CreateChild(string name, Transform parent, params Type[] components)
        {
            var go = new GameObject(name, components);
            go.transform.SetParent(parent, false);
            return go;
        }

        static TextMeshProUGUI CreateLabel(string name, string text, Transform parent, float fontSize)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);
            var label = go.GetComponent<TextMeshProUGUI>();
            label.text = text;
            label.fontSize = fontSize;
            label.alignment = TextAlignmentOptions.Center;
            label.color = Color.white;
            var le = go.AddComponent<LayoutElement>();
            le.minHeight = fontSize + 12f;
            return label;
        }

        static Button CreateButton(string name, string label, Transform parent, Vector2 size, bool primary)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.sizeDelta = size;
            var image = go.GetComponent<Image>();
            image.color = primary ? new Color(0.18f, 0.38f, 0.62f, 0.98f) : new Color(0.11f, 0.17f, 0.28f, 0.98f);

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
            tmp.fontSize = primary ? 22f : 18f;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;
            tmp.enableWordWrapping = true;
            tmp.raycastTarget = false;

            var le = go.AddComponent<LayoutElement>();
            le.minWidth = size.x;
            le.preferredWidth = size.x;
            le.minHeight = size.y;
            le.preferredHeight = size.y;
            return go.GetComponent<Button>();
        }
    }
}
