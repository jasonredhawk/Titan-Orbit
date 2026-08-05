using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.Game;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace TitanOrbit.UI
{
    /// <summary>
    /// In-game team leaderboard in the top-right corner — same width as the minimap, height
    /// stretched down until it meets the minimap below. Press TAB to cycle Team A…E panels.
    /// <para>
    /// Shows a player list only: role icons (top killer / miner / transporter), rank, name, and a
    /// combined score. No per-stat K/G/P columns. Client presentation only — reads
    /// <see cref="MinimapBlipAnchor"/> caches from <see cref="MinimapEcsEntitySync"/> (no ship-entity
    /// gathers) and names from <see cref="EcsGameBridge.RefreshPlayerDisplayNameCache"/>.
    /// </para>
    /// <para>
    /// [TITAN-ORBIT] Restores the pre-ECS <c>HUDController</c> leaderboard. Combined score matches
    /// the old NGO <c>ScoreSystem</c> weights: kill=100, deposited gem=2, delivered person=5.
    /// Layout uses the minimap's own rect size (not renderer bounds) so moving blips cannot drift
    /// the panel while the ship flies.
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TeamLeaderboardHUD : MonoBehaviour
    {
        // -------------------------------------------------------------------------
        // Inspector
        // -------------------------------------------------------------------------

        [Header("Input")]
        [Tooltip("Key that cycles the viewed team panel (legacy HUD used Tab).")]
        [SerializeField] Key cycleTeamKey = Key.Tab;

        [Header("Refresh")]
        [Tooltip("How often row text / sort refresh (seconds).")]
        [SerializeField] float refreshInterval = 0.25f;

        [Header("Layout")]
        [Tooltip("Gap between the leaderboard bottom edge and the minimap top edge.")]
        [SerializeField] float gapAboveMinimap = 8f;

        [Tooltip("Inset from the top of the HUD canvas.")]
        [SerializeField] float topMargin = 10f;

        [Tooltip("When no minimap is found, use this width so the panel still looks right.")]
        [SerializeField] float fallbackWidth = 333f;

        [Header("Visibility")]
        [Tooltip("Hide while the minimap is fullscreen-expanded.")]
        [SerializeField] bool hideWhenMinimapExpanded = true;

        [Tooltip("Hide while the ship upgrade tree obscures other HUD chrome.")]
        [SerializeField] bool hideWhenUpgradeTreeOpen = true;

        // -------------------------------------------------------------------------
        // Score weights (old NGO ScoreSystem defaults)
        // -------------------------------------------------------------------------

        /// <summary>Points per enemy kill — matches legacy <c>pointsPerEnemyKill</c>.</summary>
        const int PointsPerKill = 100;

        /// <summary>Points per deposited gem — matches legacy <c>pointsPerDepositedGem</c>.</summary>
        const int PointsPerGem = 2;

        /// <summary>Points per delivered person — matches legacy <c>pointsPerHostileUnloadPerson</c>.</summary>
        const int PointsPerPerson = 5;

        // -------------------------------------------------------------------------
        // Runtime UI roots
        // -------------------------------------------------------------------------

        GameObject _panelRoot;
        RectTransform _panelRect;
        Image _panelBg;
        Image _accentStripe;
        TextMeshProUGUI _titleText;
        RectTransform _tabDotsRoot;
        ScrollRect _scrollRect;
        RectTransform _viewportRect;
        RectTransform _contentRect;
        TextMeshProUGUI _emptyText;
        CanvasGroup _canvasGroup;

        RectTransform _layoutSpace;
        RectTransform _minimapRect;
        MinimapController _minimapController;
        MinimapEcsEntitySync _entitySync;

        int _viewedTeamIndex = -1;
        float _nextRefreshTime;
        Vector4 _lastLayoutSignature;

        readonly List<RowWidgets> _rows = new List<RowWidgets>(16);
        readonly List<MinimapBlipAnchor> _teamShips = new List<MinimapBlipAnchor>(16);
        readonly List<RowData> _sorted = new List<RowData>(16);

        static Sprite s_WhiteSprite;

        const float HeaderHeight = 28f;
        const float RowHeight = 40f;
        const float RowSpacing = 3f;
        const float ContentPadding = 4f;
        const float PanelSidePad = 6f;
        const int MaxKeepExtraRows = 4;

        // Role icon colors — match minimap top-of-team dots.
        static readonly Color BadgeKiller = new Color(0.35f, 0.55f, 1f, 1f);
        static readonly Color BadgeMiner = new Color(0.95f, 0.35f, 0.35f, 1f);
        static readonly Color BadgeTransporter = new Color(0.95f, 0.85f, 0.25f, 1f);

        /// <summary>Widgets for one pooled leaderboard row.</summary>
        sealed class RowWidgets
        {
            public GameObject Root;
            public Image Background;
            public RectTransform BadgeContainer;
            public TextMeshProUGUI RankText;
            public TextMeshProUGUI NameText;
            public TextMeshProUGUI ScoreText;
        }

        /// <summary>One sorted scoreboard entry for the current refresh.</summary>
        struct RowData
        {
            public int OwnerNetworkId;
            public string Name;
            public int Kills;
            public int Gems;
            public int People;
            public int Score;
        }

        // =========================================================================
        // Unity lifecycle
        // =========================================================================

        /// <summary>
        /// [UNITY] Builds the panel once under the gameplay HUD root (shown after team spawn).
        /// </summary>
        void Awake()
        {
            _layoutSpace = transform as RectTransform;
            if (_layoutSpace == null)
                _layoutSpace = gameObject.AddComponent<RectTransform>();

            EnsurePanelExists();
            CacheSceneRefs();
        }

#if UNITY_EDITOR
        /// <summary>
        /// [EDITOR] Builds demo rows so layout can be judged without Play Mode.
        /// </summary>
        [ContextMenu("Preview Leaderboard Layout")]
        public void EditorPreviewPopulate()
        {
            _layoutSpace = transform as RectTransform;
            EnsurePanelExists();
            CacheSceneRefs();
            UpdatePanelLayoutIfNeeded();

            Color teamColor = TeamId.TeamA.ToColor();
            if (_accentStripe != null)
                _accentStripe.color = new Color(teamColor.r, teamColor.g, teamColor.b, 0.95f);
            if (_titleText != null)
                _titleText.text = "Team A  <size=80%><color=#9EB6D8>[TAB]</color></size>";
            UpdateTabDots(3, 0, teamColor);

            string[] names = { "Nova", "Viper", "Echo", "Ranger" };
            int[] scores = { 1280, 640, 210, 40 };
            EnsureRowCount(names.Length);
            for (int i = 0; i < names.Length; i++)
            {
                RowWidgets w = _rows[i];
                w.Root.SetActive(true);
                w.RankText.text = "#" + (i + 1);
                w.NameText.text = names[i];
                w.ScoreText.text = scores[i].ToString();
                w.Background.color = new Color(0f, 0f, 0f, i % 2 == 0 ? 0.28f : 0.18f);
                PopulateBadges(w.BadgeContainer, i == 0, i == 1, i == 2);
            }

            if (_emptyText != null)
                _emptyText.gameObject.SetActive(false);
            LayoutRows(names.Length);
        }
#endif

        /// <summary>
        /// [UNITY] TAB cycles teams; periodic refresh paints rows; layout stays glued to minimap.
        /// </summary>
        void Update()
        {
            EnsurePanelExists();

            // --- TAB always handled (even if chrome is faded) so cycling never feels dead ---
            if (WasCycleKeyPressed())
                CycleViewedTeam();

            // --- Visibility gates (fade only — do not block TAB) ---
            bool hide = false;
            if (hideWhenUpgradeTreeOpen && HUDController.ShipUpgradeTreeObscuresHud)
                hide = true;
            if (hideWhenMinimapExpanded && _minimapController != null && _minimapController.IsExpanded)
                hide = true;

            if (_canvasGroup != null)
            {
                _canvasGroup.alpha = hide ? 0f : 1f;
                _canvasGroup.blocksRaycasts = false; // never steal clicks / UI nav from gameplay
                _canvasGroup.interactable = false;
            }

            if (hide)
                return;

            UpdatePanelLayoutIfNeeded();

            if (Time.unscaledTime >= _nextRefreshTime)
            {
                _nextRefreshTime = Time.unscaledTime + Mathf.Max(0.1f, refreshInterval);
                RefreshRows();
            }
        }

        /// <summary>Re-cache refs when the HUD is shown again after lobby / loading.</summary>
        void OnEnable()
        {
            CacheSceneRefs();
            _nextRefreshTime = 0f;
            _lastLayoutSignature = Vector4.zero;
        }

        /// <summary>
        /// True on the frame the cycle key was pressed.
        /// Prefers <see cref="Keyboard.tabKey"/> for Tab; falls back to the serialized key.
        /// </summary>
        bool WasCycleKeyPressed()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null)
                return false;

            // [UNITY] Tab is often bound to UI Navigate — tabKey.wasPressedThisFrame still fires.
            if (cycleTeamKey == Key.Tab)
                return keyboard.tabKey.wasPressedThisFrame;

            return keyboard[cycleTeamKey].wasPressedThisFrame;
        }

        // =========================================================================
        // Scene refs
        // =========================================================================

        /// <summary>
        /// Locates minimap + entity-sync singleton. Sync is created at runtime by
        /// <see cref="MinimapController"/> — prefer <see cref="MinimapEcsEntitySync.Instance"/>.
        /// </summary>
        void CacheSceneRefs()
        {
            if (_minimapController == null)
                _minimapController = FindFirstObjectByType<MinimapController>();
            if (_minimapController != null)
                _minimapRect = _minimapController.transform as RectTransform;

            _entitySync = MinimapEcsEntitySync.Instance;
        }

        // =========================================================================
        // Team cycling
        // =========================================================================

        /// <summary>
        /// Advances the viewed team within the match's active team count (2–5).
        /// Empty teams stay empty — we do not snap back to the local team (that made TAB look broken).
        /// </summary>
        void CycleViewedTeam()
        {
            int teamCount = GetActiveTeamCount();
            if (teamCount <= 0)
                return;

            if (_viewedTeamIndex < 0)
                _viewedTeamIndex = 0;
            else
                _viewedTeamIndex = (_viewedTeamIndex + 1) % teamCount;

            _nextRefreshTime = 0f;
            RefreshRows();
        }

        /// <summary>Active teams this match (meta / TeamState).</summary>
        static int GetActiveTeamCount()
        {
            if (EcsGameBridge.TryGetActiveTeamCount(out int count) && count > 0)
                return Mathf.Clamp(count, 1, 5);
            return 2;
        }

        /// <summary>Maps TeamId → 0-based index, clamped to active count.</summary>
        static int TeamToIndex(TeamId team, int activeCount)
        {
            if (team == TeamId.None)
                return 0;
            return Mathf.Clamp((int)team - 1, 0, Mathf.Max(0, activeCount - 1));
        }

        /// <summary>0-based index → TeamA…TeamE.</summary>
        static TeamId IndexToTeam(int index) => (TeamId)(Mathf.Clamp(index, 0, 4) + 1);

        /// <summary>
        /// Combined match score from ghosted stats — same weights as the old NGO ScoreSystem.
        /// </summary>
        static int ComputeCombinedScore(int kills, int gemsDeposited, int peopleDelivered)
        {
            return kills * PointsPerKill
                   + gemsDeposited * PointsPerGem
                   + peopleDelivered * PointsPerPerson;
        }

        // =========================================================================
        // Layout — pinned to minimap rect size (never renderer bounds)
        // =========================================================================

        /// <summary>
        /// Width = minimap width; bottom sits just above the minimap; top inset from canvas top.
        /// Uses <see cref="RectTransform.rect"/> only — blip children must not affect layout
        /// (CalculateRelativeRectTransformBounds was drifting the panel while flying).
        /// </summary>
        void UpdatePanelLayoutIfNeeded()
        {
            if (_panelRect == null || _layoutSpace == null)
                return;

            if (_minimapRect == null)
                CacheSceneRefs();

            float width = fallbackWidth;
            float minimapHeight = fallbackWidth;

            if (_minimapRect != null)
            {
                // [TITAN-ORBIT] Read the widget's own rect — ignore moving blip children.
                width = Mathf.Max(120f, _minimapRect.rect.width);
                minimapHeight = Mathf.Max(120f, _minimapRect.rect.height);
            }

            // HUD is a full-stretch overlay. Minimap sits flush bottom-right of the same canvas,
            // so its top edge in HUD local space is yMin + minimapHeight.
            float bottomY = _layoutSpace.rect.yMin + minimapHeight + gapAboveMinimap;
            float topY = _layoutSpace.rect.yMax - topMargin;
            float height = Mathf.Max(120f, topY - bottomY);

            var signature = new Vector4(width, height, minimapHeight, topMargin);
            if ((signature - _lastLayoutSignature).sqrMagnitude < 0.01f)
                return;
            _lastLayoutSignature = signature;

            _panelRect.anchorMin = new Vector2(1f, 1f);
            _panelRect.anchorMax = new Vector2(1f, 1f);
            _panelRect.pivot = new Vector2(1f, 1f);
            _panelRect.anchoredPosition = new Vector2(0f, -topMargin);
            _panelRect.sizeDelta = new Vector2(width, height);
        }

        // =========================================================================
        // Data refresh
        // =========================================================================

        /// <summary>
        /// Rebuilds sorted rows for the viewed team from minimap ship anchors + name cache.
        /// </summary>
        void RefreshRows()
        {
            if (_panelRoot == null || _titleText == null || _contentRect == null)
                return;

            CacheSceneRefs();

            int teamCount = GetActiveTeamCount();

            // --- Default viewed team = local player's team (first time only) ---
            if (_viewedTeamIndex < 0)
            {
                TeamId localTeam = TeamId.TeamA;
                if (EcsGameBridge.TryGetLocalShipState(out var localShip) && localShip.Team != TeamId.None)
                    localTeam = localShip.Team;
                _viewedTeamIndex = TeamToIndex(localTeam, teamCount);
            }

            _viewedTeamIndex = Mathf.Clamp(_viewedTeamIndex, 0, teamCount - 1);
            TeamId viewedTeam = IndexToTeam(_viewedTeamIndex);
            Color teamColor = viewedTeam.ToColor();

            if (_accentStripe != null)
                _accentStripe.color = new Color(teamColor.r, teamColor.g, teamColor.b, 0.95f);

            // Transparent black panel — team wash only on the left edge stripe.
            if (_panelBg != null)
                _panelBg.color = new Color(0f, 0f, 0f, 0.72f);

            _titleText.text = viewedTeam.ToDisplayName() + "  <size=80%><color=#9EB6D8>[TAB]</color></size>";
            UpdateTabDots(teamCount, _viewedTeamIndex, teamColor);

            // --- Collect ships from presentation cache ---
            // [TITAN-ORBIT] Anchors are filled by MinimapEcsEntitySync under join-safe rules.
            _teamShips.Clear();
            IReadOnlyList<MinimapBlipAnchor> ships = _entitySync != null ? _entitySync.Ships : null;
            if (ships != null)
            {
                for (int i = 0; i < ships.Count; i++)
                {
                    MinimapBlipAnchor a = ships[i];
                    if (a == null || a.Kind != MinimapBlipKind.Ship)
                        continue;
                    if (a.Team != viewedTeam || a.AwaitingTeamSelection)
                        continue;
                    _teamShips.Add(a);
                }
            }

            if (_teamShips.Count == 0)
            {
                for (int i = 0; i < _rows.Count; i++)
                    _rows[i].Root.SetActive(false);
                if (_emptyText != null)
                {
                    _emptyText.gameObject.SetActive(true);
                    _emptyText.text = "No players on this team.";
                }
                LayoutRows(0);
                return;
            }

            if (_emptyText != null)
                _emptyText.gameObject.SetActive(false);

            EcsGameBridge.RefreshPlayerDisplayNameCache();

            _sorted.Clear();
            for (int i = 0; i < _teamShips.Count; i++)
            {
                MinimapBlipAnchor a = _teamShips[i];
                int kills = Mathf.Max(0, a.Kills);
                int gems = Mathf.Max(0, a.GemsDeposited);
                int people = Mathf.Max(0, a.PeopleDelivered);
                string name = EcsGameBridge.GetCachedPlayerDisplayName(a.OwnerNetworkId);
                if (name.Length > 22)
                    name = name.Substring(0, 22);

                _sorted.Add(new RowData
                {
                    OwnerNetworkId = a.OwnerNetworkId,
                    Name = name,
                    Kills = kills,
                    Gems = gems,
                    People = people,
                    Score = ComputeCombinedScore(kills, gems, people),
                });
            }

            // Sort by combined score, then kills, then network id.
            _sorted.Sort((a, b) =>
            {
                int c = b.Score.CompareTo(a.Score);
                if (c != 0) return c;
                c = b.Kills.CompareTo(a.Kills);
                if (c != 0) return c;
                return a.OwnerNetworkId.CompareTo(b.OwnerNetworkId);
            });

            // --- Top-of-team role winners (icons on those rows) ---
            int bestKills = 0, bestGems = 0, bestPeople = 0;
            int bestKillerId = 0, bestMinerId = 0, bestTransporterId = 0;
            for (int i = 0; i < _sorted.Count; i++)
            {
                RowData r = _sorted[i];
                if (r.Kills > bestKills) { bestKills = r.Kills; bestKillerId = r.OwnerNetworkId; }
                if (r.Gems > bestGems) { bestGems = r.Gems; bestMinerId = r.OwnerNetworkId; }
                if (r.People > bestPeople) { bestPeople = r.People; bestTransporterId = r.OwnerNetworkId; }
            }

            EnsureRowCount(_sorted.Count);
            for (int i = 0; i < _sorted.Count; i++)
            {
                RowData r = _sorted[i];
                RowWidgets w = _rows[i];
                w.Root.SetActive(true);
                w.RankText.text = "#" + (i + 1);
                w.NameText.text = r.Name;
                w.ScoreText.text = r.Score.ToString();
                w.Background.color = new Color(0f, 0f, 0f, i % 2 == 0 ? 0.30f : 0.18f);

                PopulateBadges(
                    w.BadgeContainer,
                    bestKills > 0 && r.OwnerNetworkId == bestKillerId,
                    bestGems > 0 && r.OwnerNetworkId == bestMinerId,
                    bestPeople > 0 && r.OwnerNetworkId == bestTransporterId);
            }

            for (int i = _sorted.Count; i < _rows.Count; i++)
                _rows[i].Root.SetActive(false);

            LayoutRows(_sorted.Count);
        }

        /// <summary>Grows/shrinks the row pool.</summary>
        void EnsureRowCount(int count)
        {
            while (_rows.Count < count)
                _rows.Add(CreateRow(_rows.Count));

            while (_rows.Count > count + MaxKeepExtraRows)
            {
                int last = _rows.Count - 1;
                if (_rows[last].Root != null)
                    Destroy(_rows[last].Root);
                _rows.RemoveAt(last);
            }
        }

        /// <summary>Stacks visible rows top-down inside the scroll content.</summary>
        void LayoutRows(int visibleCount)
        {
            if (_contentRect == null || _viewportRect == null)
                return;

            float y = -ContentPadding;
            float contentWidth = Mathf.Max(1f, _viewportRect.rect.width);
            float rowWidth = Mathf.Max(1f, contentWidth - ContentPadding * 2f);

            for (int i = 0; i < visibleCount && i < _rows.Count; i++)
            {
                var rowRect = _rows[i].Root.transform as RectTransform;
                if (rowRect == null)
                    continue;
                rowRect.anchorMin = new Vector2(0f, 1f);
                rowRect.anchorMax = new Vector2(0f, 1f);
                rowRect.pivot = new Vector2(0f, 1f);
                rowRect.anchoredPosition = new Vector2(ContentPadding, y);
                rowRect.sizeDelta = new Vector2(rowWidth, RowHeight);
                y -= RowHeight + RowSpacing;
            }

            float needed = ContentPadding * 2f;
            if (visibleCount > 0)
                needed += visibleCount * RowHeight + (visibleCount - 1) * RowSpacing;
            float viewportH = Mathf.Max(1f, _viewportRect.rect.height);
            _contentRect.sizeDelta = new Vector2(contentWidth, Mathf.Max(viewportH, needed));
        }

        // =========================================================================
        // Badge helpers
        // =========================================================================

        /// <summary>
        /// Rebuilds K / G / T role icons for team leaders. Collapses width to 0 when none apply
        /// so an empty badge slot cannot leave a blank square beside the name.
        /// </summary>
        static void PopulateBadges(RectTransform parent, bool isKiller, bool isMiner, bool isTransporter)
        {
            if (parent == null)
                return;

            for (int i = parent.childCount - 1; i >= 0; i--)
                Destroy(parent.GetChild(i).gameObject);

            int count = 0;
            if (isKiller) { CreateBadge(parent, "K", BadgeKiller); count++; }
            if (isMiner) { CreateBadge(parent, "G", BadgeMiner); count++; }
            if (isTransporter) { CreateBadge(parent, "T", BadgeTransporter); count++; }

            var layout = parent.GetComponent<LayoutElement>();
            if (layout != null)
            {
                float width = count > 0 ? count * 16f + Mathf.Max(0, count - 1) * 3f : 0f;
                layout.preferredWidth = width;
                layout.minWidth = width;
                layout.flexibleWidth = 0f;
            }
        }

        /// <summary>Small colored square with a letter — top killer / miner / transporter.</summary>
        static void CreateBadge(RectTransform parent, string symbol, Color color)
        {
            var go = new GameObject("Badge_" + symbol, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(LayoutElement));
            go.transform.SetParent(parent, false);
            var rect = go.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(15f, 15f);
            var le = go.GetComponent<LayoutElement>();
            le.preferredWidth = 15f;
            le.preferredHeight = 15f;
            var img = go.GetComponent<Image>();
            img.sprite = GetWhiteSprite();
            img.color = color;
            img.raycastTarget = false;

            var labelGo = new GameObject("Label", typeof(RectTransform));
            labelGo.transform.SetParent(go.transform, false);
            var labelRect = labelGo.GetComponent<RectTransform>();
            StretchFull(labelRect);
            var tmp = labelGo.AddComponent<TextMeshProUGUI>();
            tmp.text = symbol;
            tmp.fontSize = 10f;
            tmp.fontStyle = FontStyles.Bold;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;
            tmp.raycastTarget = false;
        }

        /// <summary>Updates the A/B/C… tab dots under the compact title.</summary>
        void UpdateTabDots(int teamCount, int activeIndex, Color activeColor)
        {
            if (_tabDotsRoot == null)
                return;

            while (_tabDotsRoot.childCount < teamCount)
            {
                var dot = new GameObject("Dot", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(LayoutElement));
                dot.transform.SetParent(_tabDotsRoot, false);
                var le = dot.GetComponent<LayoutElement>();
                le.preferredWidth = 9f;
                le.preferredHeight = 9f;
                var img = dot.GetComponent<Image>();
                img.sprite = GetWhiteSprite();
                img.raycastTarget = false;
            }

            for (int i = 0; i < _tabDotsRoot.childCount; i++)
            {
                var child = _tabDotsRoot.GetChild(i).gameObject;
                bool on = i < teamCount;
                child.SetActive(on);
                if (!on)
                    continue;
                var img = child.GetComponent<Image>();
                if (img == null)
                    continue;
                TeamId t = IndexToTeam(i);
                Color c = t.ToColor();
                img.color = i == activeIndex
                    ? new Color(activeColor.r, activeColor.g, activeColor.b, 1f)
                    : new Color(c.r, c.g, c.b, 0.35f);
                var le = child.GetComponent<LayoutElement>();
                if (le != null)
                {
                    le.preferredWidth = i == activeIndex ? 16f : 9f;
                    le.preferredHeight = 9f;
                }
            }
        }

        // =========================================================================
        // Runtime UI construction
        // =========================================================================

        /// <summary>
        /// Builds the panel once: transparent black plate, team accent edge, compact title, player list.
        /// </summary>
        void EnsurePanelExists()
        {
            if (_panelRoot != null && _titleText != null && _contentRect != null)
                return;

            // Clean up a half-built tree after domain reload / script recompile.
            Transform existing = transform.Find("TeamLeaderboardPanel");
            if (existing != null)
                Destroy(existing.gameObject);

            _panelRoot = new GameObject("TeamLeaderboardPanel", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(CanvasGroup));
            _panelRoot.transform.SetParent(transform, false);
            _panelRect = _panelRoot.GetComponent<RectTransform>();
            _panelBg = _panelRoot.GetComponent<Image>();
            _panelBg.sprite = GetWhiteSprite();
            _panelBg.color = new Color(0f, 0f, 0f, 0.72f);
            _panelBg.raycastTarget = false;
            _canvasGroup = _panelRoot.GetComponent<CanvasGroup>();
            _canvasGroup.blocksRaycasts = false;
            _canvasGroup.interactable = false;

            // Team accent edge (kept — user liked this).
            _accentStripe = CreateUiImage(_panelRoot.transform, "AccentStripe", Color.white);
            var stripeRt = _accentStripe.rectTransform;
            stripeRt.anchorMin = new Vector2(0f, 0f);
            stripeRt.anchorMax = new Vector2(0f, 1f);
            stripeRt.pivot = new Vector2(0f, 0.5f);
            stripeRt.anchoredPosition = Vector2.zero;
            stripeRt.sizeDelta = new Vector2(4f, 0f);

            // --- Compact header: team name + [TAB] + dots (no ship widget / no column headers) ---
            var header = new GameObject("Header", typeof(RectTransform));
            header.transform.SetParent(_panelRoot.transform, false);
            var headerRt = header.GetComponent<RectTransform>();
            headerRt.anchorMin = new Vector2(0f, 1f);
            headerRt.anchorMax = new Vector2(1f, 1f);
            headerRt.pivot = new Vector2(0.5f, 1f);
            headerRt.anchoredPosition = new Vector2(0f, -4f);
            headerRt.sizeDelta = new Vector2(-(PanelSidePad * 2f + 4f), HeaderHeight);
            headerRt.offsetMin = new Vector2(PanelSidePad + 4f, headerRt.offsetMin.y);
            headerRt.offsetMax = new Vector2(-PanelSidePad, headerRt.offsetMax.y);

            _titleText = CreateTmp(header.transform, "Title", 15f, TextAlignmentOptions.MidlineLeft,
                new Color(0.90f, 0.94f, 1f, 1f));
            var titleRt = _titleText.rectTransform;
            titleRt.anchorMin = new Vector2(0f, 0f);
            titleRt.anchorMax = new Vector2(0.62f, 1f);
            titleRt.offsetMin = Vector2.zero;
            titleRt.offsetMax = Vector2.zero;
            _titleText.fontStyle = FontStyles.Bold;
            _titleText.richText = true;
            _titleText.text = "Team A  [TAB]";

            _tabDotsRoot = new GameObject("TabDots", typeof(RectTransform), typeof(HorizontalLayoutGroup)).GetComponent<RectTransform>();
            _tabDotsRoot.SetParent(header.transform, false);
            _tabDotsRoot.anchorMin = new Vector2(0.62f, 0f);
            _tabDotsRoot.anchorMax = new Vector2(1f, 1f);
            _tabDotsRoot.offsetMin = Vector2.zero;
            _tabDotsRoot.offsetMax = Vector2.zero;
            var dotsLayout = _tabDotsRoot.GetComponent<HorizontalLayoutGroup>();
            dotsLayout.childAlignment = TextAnchor.MiddleRight;
            dotsLayout.spacing = 4f;
            dotsLayout.childForceExpandWidth = false;
            dotsLayout.childForceExpandHeight = false;
            dotsLayout.padding = new RectOffset(0, 2, 0, 0);

            // --- Scroll area fills everything under the compact header ---
            var scrollGo = new GameObject("Scroll", typeof(RectTransform), typeof(ScrollRect));
            scrollGo.transform.SetParent(_panelRoot.transform, false);
            var scrollRt = scrollGo.GetComponent<RectTransform>();
            scrollRt.anchorMin = Vector2.zero;
            scrollRt.anchorMax = Vector2.one;
            scrollRt.offsetMin = new Vector2(PanelSidePad + 2f, PanelSidePad);
            scrollRt.offsetMax = new Vector2(-PanelSidePad, -(HeaderHeight + 6f));
            _scrollRect = scrollGo.GetComponent<ScrollRect>();
            _scrollRect.horizontal = false;
            _scrollRect.vertical = true;
            _scrollRect.movementType = ScrollRect.MovementType.Clamped;
            _scrollRect.scrollSensitivity = 28f;

            var viewportGo = new GameObject("Viewport", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Mask));
            viewportGo.transform.SetParent(scrollGo.transform, false);
            _viewportRect = viewportGo.GetComponent<RectTransform>();
            StretchFull(_viewportRect);
            var vpImg = viewportGo.GetComponent<Image>();
            vpImg.sprite = GetWhiteSprite();
            vpImg.color = Color.white;
            vpImg.raycastTarget = false;
            viewportGo.GetComponent<Mask>().showMaskGraphic = false;

            var contentGo = new GameObject("Content", typeof(RectTransform));
            contentGo.transform.SetParent(viewportGo.transform, false);
            _contentRect = contentGo.GetComponent<RectTransform>();
            _contentRect.anchorMin = new Vector2(0f, 1f);
            _contentRect.anchorMax = new Vector2(0f, 1f);
            _contentRect.pivot = new Vector2(0f, 1f);
            _contentRect.anchoredPosition = Vector2.zero;
            _contentRect.sizeDelta = new Vector2(300f, 100f);

            _emptyText = CreateTmp(viewportGo.transform, "Empty", 14f, TextAlignmentOptions.Center,
                new Color(0.75f, 0.80f, 0.90f, 0.9f));
            StretchFull(_emptyText.rectTransform);
            _emptyText.rectTransform.offsetMin = new Vector2(10f, 10f);
            _emptyText.rectTransform.offsetMax = new Vector2(-10f, -10f);
            _emptyText.text = "No players on this team.";
            _emptyText.raycastTarget = false;

            _scrollRect.viewport = _viewportRect;
            _scrollRect.content = _contentRect;
            _scrollRect.verticalNormalizedPosition = 1f;

            UpdatePanelLayoutIfNeeded();
        }

        /// <summary>
        /// One pooled row: role icons | rank | name | combined score.
        /// </summary>
        RowWidgets CreateRow(int index)
        {
            var rowGo = new GameObject("Row_" + (index + 1), typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            rowGo.transform.SetParent(_contentRect, false);
            var bg = rowGo.GetComponent<Image>();
            bg.sprite = GetWhiteSprite();
            bg.color = new Color(0f, 0f, 0f, 0.25f);
            bg.raycastTarget = false;

            var hlg = rowGo.AddComponent<HorizontalLayoutGroup>();
            hlg.padding = new RectOffset(6, 8, 5, 5);
            hlg.spacing = 6f;
            hlg.childAlignment = TextAnchor.MiddleLeft;
            hlg.childControlHeight = true;
            hlg.childControlWidth = true;
            hlg.childForceExpandHeight = true;
            hlg.childForceExpandWidth = false;

            // Role icons first — width collapses to 0 when the player has none.
            var badges = CreateCell(rowGo.transform, "Badges", 0f);
            var badgesLayout = badges.gameObject.AddComponent<HorizontalLayoutGroup>();
            badgesLayout.spacing = 3f;
            badgesLayout.childAlignment = TextAnchor.MiddleLeft;
            badgesLayout.childControlWidth = false;
            badgesLayout.childControlHeight = false;
            badgesLayout.childForceExpandWidth = false;
            badgesLayout.childForceExpandHeight = false;

            var rank = CreateRowLabel(CreateCell(rowGo.transform, "Rank", 30f), 13, TextAlignmentOptions.Center);
            rank.color = new Color(0.85f, 0.90f, 1f);

            var nameCell = CreateCell(rowGo.transform, "Name", -1f);
            nameCell.GetComponent<LayoutElement>().flexibleWidth = 1f;
            var name = CreateRowLabel(nameCell, 14, TextAlignmentOptions.Left);
            name.color = Color.white;

            var score = CreateRowLabel(CreateCell(rowGo.transform, "Score", 64f), 15, TextAlignmentOptions.Right);
            score.color = new Color(0.95f, 0.86f, 0.55f);
            score.fontStyle = FontStyles.Bold;

            return new RowWidgets
            {
                Root = rowGo,
                Background = bg,
                BadgeContainer = badges,
                RankText = rank,
                NameText = name,
                ScoreText = score,
            };
        }

        /// <summary>Fixed- or flexible-width cell under a horizontal row layout.</summary>
        static RectTransform CreateCell(Transform parent, string name, float width)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(LayoutElement));
            go.transform.SetParent(parent, false);
            var le = go.GetComponent<LayoutElement>();
            if (width > 0f)
            {
                le.preferredWidth = width;
                le.minWidth = width;
            }
            else if (width == 0f)
            {
                le.preferredWidth = 0f;
                le.minWidth = 0f;
            }

            return go.GetComponent<RectTransform>();
        }

        /// <summary>TMP label that fills its cell; ellipsis when names are long.</summary>
        static TextMeshProUGUI CreateRowLabel(RectTransform parent, int fontSize, TextAlignmentOptions align)
        {
            var go = new GameObject("Text", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            StretchFull(go.GetComponent<RectTransform>());
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.fontSize = fontSize;
            tmp.alignment = align;
            tmp.enableWordWrapping = false;
            tmp.overflowMode = TextOverflowModes.Ellipsis;
            tmp.raycastTarget = false;
            return tmp;
        }

        /// <summary>Utility: Image child with white sprite for tinting.</summary>
        static Image CreateUiImage(Transform parent, string name, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>();
            img.sprite = GetWhiteSprite();
            img.color = color;
            img.raycastTarget = false;
            return img;
        }

        /// <summary>Utility: TMP under a parent with sensible defaults.</summary>
        static TextMeshProUGUI CreateTmp(Transform parent, string name, float fontSize, TextAlignmentOptions align, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.fontSize = fontSize;
            tmp.alignment = align;
            tmp.color = color;
            tmp.raycastTarget = false;
            tmp.enableWordWrapping = false;
            tmp.overflowMode = TextOverflowModes.Ellipsis;
            return tmp;
        }

        /// <summary>Stretch a rect to fill its parent.</summary>
        static void StretchFull(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        /// <summary>Shared 1×1 white sprite for UGUI Image color tints.</summary>
        static Sprite GetWhiteSprite()
        {
            if (s_WhiteSprite != null)
                return s_WhiteSprite;
            var texture = Texture2D.whiteTexture;
            s_WhiteSprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f), 100f);
            s_WhiteSprite.name = "TeamLeaderboardWhite";
            return s_WhiteSprite;
        }
    }
}
