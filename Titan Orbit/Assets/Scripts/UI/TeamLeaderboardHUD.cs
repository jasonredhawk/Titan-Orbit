using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Game;
using TitanOrbit.NetCode;
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
    /// Shows a player list only: a gold Command Deck for earned commanders (living
    /// top killer / miner / troop mover), then the crew. Role icons, rank, profile
    /// badge, name, combined score, and a small Comms Matrix mute toggle. No per-stat K/G/P
    /// columns. Client presentation only — reads <see cref="MinimapBlipAnchor"/> caches from
    /// <see cref="MinimapEcsEntitySync"/> (no ship-entity gathers) and identity from
    /// <see cref="EcsGameBridge.RefreshPlayerDisplayNameCache"/>. Mute is local
    /// (<see cref="CommsMuteList"/>) — the server still broadcasts; this client drops chips
    /// and path pings from that <c>GhostOwner.NetworkId</c>.
    /// </para>
    /// <para>
    /// The header tabs are a planet-control bar: the full leaderboard width is every capturable
    /// planet (homes + neutrals). Each team's colored tab is that team's owned share; leftover
    /// width is still-neutral worlds. Combined player score still uses the old NGO
    /// <c>ScoreSystem</c> weights: kill=100, deposited gem=2, delivered person=5.
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
        // Score weights live in ShipMatchScoreLogic (shared with ship nameplates).

        // -------------------------------------------------------------------------
        // Runtime UI roots
        // -------------------------------------------------------------------------

        GameObject _panelRoot;
        RectTransform _panelRect;
        Image _panelBg;
        Image _accentStripe;
        TextMeshProUGUI _titleText;
        RectTransform _planetBarRoot;
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
        SectionBanner _commandDeckBanner;
        SectionBanner _crewBanner;
        /// <summary>Pooled team + unowned slices for the planet-control bar.</summary>
        readonly List<PlanetBarSegment> _planetBarSegments = new List<PlanetBarSegment>(6);

        /// <summary>Owned capturable planets per team index (Team A = 0). Filled each refresh.</summary>
        readonly int[] _planetCountsByTeam = new int[5];

        static Sprite s_WhiteSprite;

        // [TITAN-ORBIT] Control bar sits above the title. Its full width is every capturable world.
        const float PlanetControlBarHeight = 14f;
        const float HeaderHeight = 24f;
        const float TopChromePad = 4f;
        const float RowHeight = 40f;
        /// <summary>Command-deck rows sit a little taller so the gold rail and star rank read.</summary>
        const float CommanderRowHeight = 46f;
        const float SectionBannerHeight = 20f;
        const float RowSpacing = 3f;
        const float CommandDeckAfterGap = 7f;
        const float ContentPadding = 4f;
        const float PanelSidePad = 6f;
        const int MaxKeepExtraRows = 4;
        /// <summary>Profile emblem beside the name. Fits the 40px row after 5px vertical padding.</summary>
        const float PlayerBadgeSize = 26f;
        /// <summary>Comms mute hit target after the score. Small so the name column stays readable.</summary>
        const float MuteCellSize = 22f;

        // Role icon colors — match minimap top-of-team dots.
        static readonly Color BadgeKiller = new Color(0.35f, 0.55f, 1f, 1f);
        static readonly Color BadgeMiner = new Color(0.95f, 0.35f, 0.35f, 1f);
        static readonly Color BadgeTransporter = new Color(0.95f, 0.85f, 0.25f, 1f);
        static readonly Color CommanderGold = TeamCommanderRules.Gold;
        static readonly Color CommanderWashA = new Color(0.20f, 0.15f, 0.04f, 0.72f);
        static readonly Color CommanderWashB = new Color(0.12f, 0.10f, 0.03f, 0.58f);
        static readonly Color CommanderName = new Color(1f, 0.94f, 0.78f, 1f);
        static readonly Color CrewCaption = new Color(0.62f, 0.78f, 0.95f, 0.92f);
        /// <summary>Dark-glass fill on the mute cell — same void as tooltip chrome.</summary>
        static readonly Color MuteFill = new Color(0.012f, 0.016f, 0.028f, 0.96f);
        /// <summary>Ice caption when this client still hears that speaker.</summary>
        static readonly Color MuteOpenCaption = new Color(0.62f, 0.78f, 0.95f, 0.95f);
        /// <summary>Dimmer caption + slash when that speaker is muted.</summary>
        static readonly Color MuteClosedCaption = new Color(0.42f, 0.52f, 0.62f, 0.88f);

        /// <summary>
        /// One colored slice of the planet-control bar (a team, or the leftover unowned worlds).
        /// Width is a share of every capturable planet, not a fixed tab size.
        /// </summary>
        sealed class PlanetBarSegment
        {
            /// <summary>Slice GameObject; deactivated when this team owns nothing.</summary>
            public GameObject Root;

            /// <summary>Anchors are set as a fraction of capturable planets.</summary>
            public RectTransform Rect;

            /// <summary>Team color (or dark ice for unowned worlds).</summary>
            public Image Fill;

            /// <summary>Planet count, shown only when the slice is wide enough to read.</summary>
            public TextMeshProUGUI CountText;
        }

        /// <summary>Widgets for one pooled leaderboard row.</summary>
        sealed class RowWidgets
        {
            public GameObject Root;
            public Image Background;
            public Image CommanderRail;
            public Image CommanderGlow;
            public RectTransform BadgeContainer;
            public RectTransform PlayerBadgeCell;
            public Image PlayerBadgeImage;
            public TextMeshProUGUI RankText;
            public TextMeshProUGUI NameText;
            public TextMeshProUGUI ScoreText;
            /// <summary>GhostOwner.NetworkId painted this refresh. Mute click reads this, not the sort index.</summary>
            public int OwnerNetworkId;
            /// <summary>True when this row is the local ship. Mute stays hidden even if NetworkId is still 0.</summary>
            public bool IsLocalPlayer;
            /// <summary>22px comms mute cell. Hidden on the local player's row.</summary>
            public GameObject MuteRoot;
            public Button MuteButton;
            public Image MuteFill;
            public TextMeshProUGUI MuteMark;
            /// <summary>Diagonal slash over the speaker mark while muted.</summary>
            public Image MuteSlash;
        }

        /// <summary>
        /// In-list banner that splits the Command Deck from the crew. Not a player row.
        /// </summary>
        sealed class SectionBanner
        {
            public GameObject Root;
            public Image Fill;
            public Image Accent;
            public TextMeshProUGUI Label;
        }

        /// <summary>One sorted scoreboard entry for the current refresh.</summary>
        struct RowData
        {
            public int OwnerNetworkId;
            public string Name;
            public int BadgeId;
            public int Kills;
            public int Gems;
            public int People;
            public int Score;
            /// <summary>True when this hull is the local player's ship (minimap anchor flag).</summary>
            public bool IsLocalPlayer;
            /// <summary>Dead hulls stay on the list but cannot hold a command seat.</summary>
            public bool IsDead;
            /// <summary>True when this owner holds a living killer / miner / troop title.</summary>
            public bool IsCommander;
            /// <summary>1-based place by combined score. Gold chrome uses <see cref="IsCommander"/>, not this.</summary>
            public int ScoreRank;
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
                _titleText.text = "Team A  <size=80%><color=#9EB6D8>4/10  [TAB]</color></size>";

            // Demo: 10 capturable worlds — A leads, some still neutral.
            int[] demoCounts = { 4, 2, 1, 0, 0 };
            PaintPlanetControlBar(3, 0, demoCounts, 10);

            string[] names = { "Nova", "Viper", "Echo", "Ranger" };
            int[] scores = { 1280, 640, 210, 40 };
            int[] demoBadgeIds = { 1, 2, 3, 0 };
            EnsureRowCount(names.Length);
            for (int i = 0; i < names.Length; i++)
            {
                RowWidgets w = _rows[i];
                w.Root.SetActive(true);
                bool commander = TeamCommanderRules.IsCommanderRank(i + 1);
                PaintPlayerRow(
                    w,
                    i + 1,
                    names[i],
                    scores[i],
                    commander,
                    i == 0,
                    i == 1,
                    i == 2);
                ApplyPlayerBadge(w, demoBadgeIds[i]);
            }

            if (_emptyText != null)
                _emptyText.gameObject.SetActive(false);
                LayoutScoreboard(names.Length, Mathf.Min(TeamCommanderRules.Slots, names.Length));
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
            if (hideWhenMinimapExpanded &&
                (HUDController.MinimapExpandedObscuresHud ||
                 HUDController.CommsMatrixObscuresHud ||
                 (_minimapController != null && _minimapController.IsExpanded)))
                hide = true;
            // [TITAN-ORBIT] Same death hide as minimap / rockets — keep the explosion unobstructed.
            if (HUDController.LocalPlayerDeathHidesHud)
                hide = true;

            if (_canvasGroup != null)
            {
                _canvasGroup.alpha = hide ? 0f : 1f;
                // [TITAN-ORBIT] Mute is the only Graphic with raycastTarget = true.
                // Enable the group only while visible so a faded panel cannot steal
                // combat clicks. Names, scores, and the plate stay click-through.
                bool allowMuteClicks = !hide;
                _canvasGroup.blocksRaycasts = allowMuteClicks;
                _canvasGroup.interactable = allowMuteClicks;
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
        /// Combined match score from ghosted stats — same weights as the old NGO ScoreSystem
        /// (shared with ship nameplates via <see cref="ShipMatchScoreLogic"/>).
        /// </summary>
        static int ComputeCombinedScore(int kills, int gemsDeposited, int peopleDelivered)
        {
            return ShipMatchScoreLogic.ComputeCombinedScore(kills, gemsDeposited, peopleDelivered);
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
        /// Rebuilds the planet-control bar and sorted rows for the viewed team from minimap
        /// planet/ship anchors + the name cache. No ECS entity gathers.
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

            // --- Planet-control tabs (full width = every world that can be owned) ---
            CountPlanetOwnership(teamCount, out int capturableTotal);
            PaintPlanetControlBar(teamCount, _viewedTeamIndex, _planetCountsByTeam, capturableTotal);

            int viewedOwned = _viewedTeamIndex >= 0 && _viewedTeamIndex < _planetCountsByTeam.Length
                ? _planetCountsByTeam[_viewedTeamIndex]
                : 0;
            _titleText.text = viewedTeam.ToDisplayName()
                + "  <size=80%><color=#9EB6D8>" + viewedOwned + "/" + capturableTotal + "  [TAB]</color></size>";

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
                LayoutScoreboard(0, 0);
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
                    BadgeId = EcsGameBridge.GetCachedPlayerBadgeId(a.OwnerNetworkId),
                    Kills = kills,
                    Gems = gems,
                    People = people,
                    Score = ComputeCombinedScore(kills, gems, people),
                    IsLocalPlayer = a.IsLocalPlayer,
                    IsDead = a.IsDead,
                });
            }

            // --- Earned category titles (same rules as nameplates / server snapshot) ---
            // Zero scores never win. Dead hulls stay on the list but cannot sit Command Deck.
            int bestKills = 0, bestGems = 0, bestPeople = 0;
            int bestKillerId = 0, bestMinerId = 0, bestTransporterId = 0;
            for (int i = 0; i < _sorted.Count; i++)
            {
                RowData r = _sorted[i];
                if (r.IsDead || r.OwnerNetworkId <= 0)
                    continue;

                if (TeamCommandRoleRules.IsBetterTop(r.Kills, r.OwnerNetworkId, bestKills, bestKillerId))
                {
                    bestKills = r.Kills;
                    bestKillerId = r.OwnerNetworkId;
                }

                if (TeamCommandRoleRules.IsBetterTop(r.Gems, r.OwnerNetworkId, bestGems, bestMinerId))
                {
                    bestGems = r.Gems;
                    bestMinerId = r.OwnerNetworkId;
                }

                if (TeamCommandRoleRules.IsBetterTop(r.People, r.OwnerNetworkId, bestPeople, bestTransporterId))
                {
                    bestPeople = r.People;
                    bestTransporterId = r.OwnerNetworkId;
                }
            }

            int commanderCount = 0;
            for (int i = 0; i < _sorted.Count; i++)
            {
                RowData r = _sorted[i];
                r.IsCommander = TeamCommanderRules.HoldsCommandSeat(
                    r.OwnerNetworkId == bestKillerId,
                    r.OwnerNetworkId == bestMinerId,
                    r.OwnerNetworkId == bestTransporterId);
                _sorted[i] = r;
                if (r.IsCommander)
                    commanderCount++;
            }

            // --- Score rank (leaderboard place) ---
            // Assigned before we lift commanders to the deck so # still means combined score.
            _sorted.Sort((a, b) =>
            {
                int c = b.Score.CompareTo(a.Score);
                if (c != 0) return c;
                c = b.Kills.CompareTo(a.Kills);
                if (c != 0) return c;
                return a.OwnerNetworkId.CompareTo(b.OwnerNetworkId);
            });
            for (int i = 0; i < _sorted.Count; i++)
            {
                RowData r = _sorted[i];
                r.ScoreRank = i + 1;
                _sorted[i] = r;
            }

            // Command Deck first (earned seats), then crew. Inside each slice: score, kills, id.
            _sorted.Sort((a, b) =>
            {
                int c = b.IsCommander.CompareTo(a.IsCommander);
                if (c != 0) return c;
                c = b.Score.CompareTo(a.Score);
                if (c != 0) return c;
                c = b.Kills.CompareTo(a.Kills);
                if (c != 0) return c;
                return a.OwnerNetworkId.CompareTo(b.OwnerNetworkId);
            });

            EnsureRowCount(_sorted.Count);
            for (int i = 0; i < _sorted.Count; i++)
            {
                RowData r = _sorted[i];
                RowWidgets w = _rows[i];
                w.Root.SetActive(true);
                w.OwnerNetworkId = r.OwnerNetworkId;
                w.IsLocalPlayer = r.IsLocalPlayer;
                PaintPlayerRow(
                    w,
                    r.ScoreRank,
                    r.Name,
                    r.Score,
                    r.IsCommander,
                    r.OwnerNetworkId == bestKillerId,
                    r.OwnerNetworkId == bestMinerId,
                    r.OwnerNetworkId == bestTransporterId);
                ApplyPlayerBadge(w, r.BadgeId);
                PaintMuteButton(w);
            }

            for (int i = _sorted.Count; i < _rows.Count; i++)
                _rows[i].Root.SetActive(false);

            LayoutScoreboard(_sorted.Count, commanderCount);
        }

        /// <summary>
        /// Paints one scoreboard row. Earned Command Deck seats get gold wash,
        /// a left rail, and a CDR pip. Crew rows stay ice-dark.
        /// </summary>
        static void PaintPlayerRow(
            RowWidgets w,
            int rank,
            string name,
            int score,
            bool commander,
            bool isKiller,
            bool isMiner,
            bool isTransporter)
        {
            if (w == null)
                return;

            if (w.RankText != null)
            {
                w.RankText.text = "#" + rank;
                w.RankText.color = commander
                    ? CommanderGold
                    : new Color(0.85f, 0.90f, 1f);
                w.RankText.fontStyle = commander ? FontStyles.Bold : FontStyles.Normal;
            }

            if (w.NameText != null)
            {
                w.NameText.text = name;
                w.NameText.color = commander ? CommanderName : Color.white;
            }

            if (w.ScoreText != null)
            {
                w.ScoreText.text = score.ToString();
                w.ScoreText.color = commander
                    ? CommanderGold
                    : new Color(0.95f, 0.86f, 0.55f);
            }

            if (w.Background != null)
            {
                if (commander)
                    w.Background.color = rank % 2 == 1 ? CommanderWashA : CommanderWashB;
                else
                    w.Background.color = new Color(0f, 0f, 0f, rank % 2 == 1 ? 0.30f : 0.18f);
            }

            if (w.CommanderRail != null)
            {
                w.CommanderRail.enabled = commander;
                w.CommanderRail.color = CommanderGold;
            }

            if (w.CommanderGlow != null)
            {
                w.CommanderGlow.enabled = commander;
                w.CommanderGlow.color = new Color(CommanderGold.r, CommanderGold.g, CommanderGold.b, 0.18f);
            }

            PopulateBadges(w.BadgeContainer, isKiller, isMiner, isTransporter);
        }

        /// <summary>
        /// Shows or hides the comms mute cell and paints open vs muted chrome.
        /// Hidden on the local row and on editor demo rows (OwnerNetworkId ≤ 0).
        /// </summary>
        /// <param name="w">Pooled row whose <see cref="RowWidgets.OwnerNetworkId"/> is current.</param>
        static void PaintMuteButton(RowWidgets w)
        {
            if (w == null || w.MuteRoot == null)
                return;

            int ownerId = w.OwnerNetworkId;
            int localId = EcsGameBridge.GetLocalNetworkId();

            // --- Who can be muted ---
            // Hide on self (NetworkId or anchor flag) and on editor demo rows (id ≤ 0).
            bool show = ownerId > 0 && !w.IsLocalPlayer && (localId <= 0 || ownerId != localId);
            w.MuteRoot.SetActive(show);
            if (!show)
                return;

            // --- Open vs muted chrome ---
            // "C" = comms still heard. Slash over the mark = this client dropped their chips.
            bool muted = CommsMuteList.IsMuted(ownerId);
            if (w.MuteFill != null)
                w.MuteFill.color = MuteFill;

            if (w.MuteMark != null)
            {
                w.MuteMark.text = "C";
                w.MuteMark.color = muted ? MuteClosedCaption : MuteOpenCaption;
            }

            if (w.MuteSlash != null)
            {
                w.MuteSlash.enabled = muted;
                w.MuteSlash.color = muted ? MuteClosedCaption : MuteOpenCaption;
            }
        }

        /// <summary>
        /// [UNITY] Mute button click. Toggles <see cref="CommsMuteList"/> for the
        /// NetworkId stored on this pooled row (not the click index). Live chips
        /// from that speaker die on the next presenter tick.
        /// </summary>
        static void OnMuteClicked(RowWidgets w)
        {
            // --- Guard ---
            // Pooled rows can be reused; OwnerNetworkId is the live occupant, not the
            // row index. Never mute yourself — hide the button and refuse the click.
            if (w == null || w.OwnerNetworkId <= 0 || w.IsLocalPlayer)
                return;

            int localId = EcsGameBridge.GetLocalNetworkId();
            if (localId > 0 && w.OwnerNetworkId == localId)
                return;

            // --- Toggle + repaint ---
            // Inbox drops the next callout. TickBubbles kills chips already on screen.
            CommsMuteList.Toggle(w.OwnerNetworkId);
            PaintMuteButton(w);
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

        /// <summary>
        /// Stacks the Command Deck banner + earned commander rows, then the crew banner +
        /// remaining rows. Banners hide when that slice is empty. An empty team at match
        /// start shows no Command Deck — seats are earned, not given to rank 1–3.
        /// </summary>
        /// <param name="visibleCount">How many player rows are on this team.</param>
        /// <param name="commanderCount">How many of those rows hold a living title.</param>
        void LayoutScoreboard(int visibleCount, int commanderCount)
        {
            if (_contentRect == null || _viewportRect == null)
                return;

            int commanders = Mathf.Clamp(commanderCount, 0, Mathf.Min(TeamCommanderRules.Slots, Mathf.Max(0, visibleCount)));
            int crew = Mathf.Max(0, visibleCount - commanders);
            float contentWidth = Mathf.Max(1f, _viewportRect.rect.width);
            float rowWidth = Mathf.Max(1f, contentWidth - ContentPadding * 2f);
            float y = -ContentPadding;

            y = PlaceSectionBanner(_commandDeckBanner, y, rowWidth, commanders > 0, true);
            for (int i = 0; i < commanders && i < _rows.Count; i++)
            {
                y = PlaceScoreRow(_rows[i], y, rowWidth, CommanderRowHeight);
            }

            if (commanders > 0 && crew > 0)
                y -= CommandDeckAfterGap - RowSpacing;

            y = PlaceSectionBanner(_crewBanner, y, rowWidth, crew > 0, false);
            for (int i = commanders; i < visibleCount && i < _rows.Count; i++)
            {
                y = PlaceScoreRow(_rows[i], y, rowWidth, RowHeight);
            }

            float needed = ContentPadding - y + ContentPadding;
            float viewportH = Mathf.Max(1f, _viewportRect.rect.height);
            _contentRect.sizeDelta = new Vector2(contentWidth, Mathf.Max(viewportH, needed));
        }

        /// <summary>
        /// Pins a Command Deck / CREW banner under the current cursor. Hidden banners
        /// take no height so a solo team does not leave a dead CREW strip.
        /// </summary>
        static float PlaceSectionBanner(
            SectionBanner banner,
            float y,
            float width,
            bool show,
            bool commandDeck)
        {
            if (banner == null || banner.Root == null)
                return y;

            banner.Root.SetActive(show);
            if (!show)
                return y;

            var rt = banner.Root.transform as RectTransform;
            if (rt == null)
                return y;

            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(ContentPadding, y);
            rt.sizeDelta = new Vector2(width, SectionBannerHeight);

            if (banner.Label != null)
            {
                banner.Label.text = commandDeck ? "COMMANDERS" : "CREW";
                banner.Label.color = commandDeck ? CommanderGold : CrewCaption;
            }

            if (banner.Accent != null)
                banner.Accent.color = commandDeck
                    ? CommanderGold
                    : new Color(0.35f, 0.72f, 0.95f, 0.85f);

            return y - SectionBannerHeight - RowSpacing;
        }

        /// <summary>Pins one player row and advances the layout cursor.</summary>
        static float PlaceScoreRow(RowWidgets row, float y, float width, float height)
        {
            if (row == null || row.Root == null)
                return y;

            var rowRect = row.Root.transform as RectTransform;
            if (rowRect == null)
                return y;

            rowRect.anchorMin = new Vector2(0f, 1f);
            rowRect.anchorMax = new Vector2(0f, 1f);
            rowRect.pivot = new Vector2(0f, 1f);
            rowRect.anchoredPosition = new Vector2(ContentPadding, y);
            rowRect.sizeDelta = new Vector2(width, height);
            return y - height - RowSpacing;
        }

        // =========================================================================
        // Badge helpers
        // =========================================================================

        /// <summary>
        /// Paints the profile emblem from <see cref="PlayerBadgeCatalog"/>. Collapses the cell
        /// when this player has no sprite so names stay tight against rank.
        /// </summary>
        static void ApplyPlayerBadge(RowWidgets w, int badgeId)
        {
            if (w == null || w.PlayerBadgeImage == null)
                return;

            Sprite sprite = PlayerBadgeCatalog.FindSprite(PlayerBadgeIdUtil.Sanitize(badgeId));
            bool show = sprite != null;
            w.PlayerBadgeImage.sprite = sprite;
            w.PlayerBadgeImage.enabled = show;
            w.PlayerBadgeImage.color = Color.white;
            w.PlayerBadgeImage.preserveAspect = true;

            var layout = w.PlayerBadgeCell != null
                ? w.PlayerBadgeCell.GetComponent<LayoutElement>()
                : null;
            if (layout != null)
            {
                float width = show ? PlayerBadgeSize : 0f;
                layout.preferredWidth = width;
                layout.minWidth = width;
                layout.preferredHeight = PlayerBadgeSize;
                layout.minHeight = PlayerBadgeSize;
            }
        }

        /// <summary>
        /// Rebuilds K / G / T role icons for team leaders. Collapses width to 0 when none apply
        /// so an empty badge slot cannot leave a blank square beside the name.
        /// </summary>
        static void PopulateBadges(
            RectTransform parent,
            bool isKiller,
            bool isMiner,
            bool isTransporter)
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

        /// <summary>
        /// Tallies home + regular planets per team from <see cref="MinimapEcsEntitySync"/> anchors.
        /// The bar denominator is every capturable world: live ghost count, else session meta
        /// (homes + neutrals). Never queries ship or map-body entities — join-safe presentation only.
        /// </summary>
        /// <param name="teamCount">Active teams; ownership indices past this are ignored.</param>
        /// <param name="capturableTotal">Bar width in planets (at least 1 so layout cannot divide by zero).</param>
        void CountPlanetOwnership(int teamCount, out int capturableTotal)
        {
            // --- Reset this refresh's owned-planet tallies ---
            for (int i = 0; i < _planetCountsByTeam.Length; i++)
                _planetCountsByTeam[i] = 0;

            // Homes and neutrals live on separate minimap lists — both can be owned.
            int seen = 0;
            seen += TallyPlanetAnchors(_entitySync != null ? _entitySync.Planets : null, teamCount);
            seen += TallyPlanetAnchors(_entitySync != null ? _entitySync.HomePlanets : null, teamCount);

            // [TITAN-ORBIT] Meta is the match recipe: every home and every neutral can be owned.
            // Prefer LivePlanetCount so the bar stays full-width before every ghost has arrived.
            int metaTotal = 0;
            if (MapSessionMetaCache.LivePlanetCount > 0)
                metaTotal = MapSessionMetaCache.LivePlanetCount;
            else if (MapSessionMetaCache.TeamCount > 0 || MapSessionMetaCache.NeutralPlanetCount > 0)
            {
                metaTotal = Mathf.Max(0, MapSessionMetaCache.TeamCount)
                            + Mathf.Max(0, MapSessionMetaCache.NeutralPlanetCount);
            }

            capturableTotal = Mathf.Max(seen, metaTotal);
            if (capturableTotal <= 0)
                capturableTotal = Mathf.Max(1, teamCount);
        }

        /// <summary>
        /// Adds each planet blip's owner into <see cref="_planetCountsByTeam"/>. Neutral worlds
        /// (<see cref="TeamId.None"/>) are not stored — leftover bar width is unowned.
        /// </summary>
        /// <returns>How many planet anchors were read (owned + unowned).</returns>
        int TallyPlanetAnchors(IReadOnlyList<MinimapBlipAnchor> anchors, int teamCount)
        {
            if (anchors == null)
                return 0;

            int seen = 0;
            for (int i = 0; i < anchors.Count; i++)
            {
                MinimapBlipAnchor a = anchors[i];
                if (a == null)
                    continue;

                // Every planet counts toward the bar's total, even if nobody owns it yet.
                seen++;

                // TeamA = 1 → slot 0. TeamId.None lands at -1 and stays in the unowned remainder.
                int index = (int)a.Team - 1;
                if (index < 0 || index >= teamCount || index >= _planetCountsByTeam.Length)
                    continue;
                _planetCountsByTeam[index]++;
            }

            return seen;
        }

        /// <summary>
        /// Sizes team-color tabs so the bar width is every capturable planet. Anchors are
        /// fractions of <paramref name="capturableTotal"/> — four owned of twenty worlds is 20%
        /// of the leaderboard width. A dim remainder slice is still-neutral worlds. The viewed
        /// team is brighter so TAB still has a selected tab.
        /// </summary>
        /// <param name="teamCount">How many team slices to consider.</param>
        /// <param name="viewedIndex">Selected team for the player list below.</param>
        /// <param name="counts">Owned planets per team index (Team A = 0). May be longer than teamCount.</param>
        /// <param name="capturableTotal">Planets that can be owned — the bar's 100%.</param>
        void PaintPlanetControlBar(int teamCount, int viewedIndex, int[] counts, int capturableTotal)
        {
            if (_planetBarRoot == null || counts == null)
                return;

            teamCount = Mathf.Clamp(teamCount, 1, 5);
            capturableTotal = Mathf.Max(1, capturableTotal);

            // --- Owned vs leftover ---
            int ownedTotal = 0;
            for (int i = 0; i < teamCount && i < counts.Length; i++)
                ownedTotal += Mathf.Max(0, counts[i]);

            int unowned = Mathf.Max(0, capturableTotal - ownedTotal);

            // One slice per team plus the leftover unowned worlds.
            EnsurePlanetBarSegmentCount(teamCount + 1);

            // Pixel width is only used to hide numbers on hairline slices.
            float barWidth = _planetBarRoot.rect.width;
            if (barWidth < 8f && _panelRect != null)
                barWidth = Mathf.Max(8f, _panelRect.rect.width - PanelSidePad * 2f);

            // --- Place slices left → right in Team A…E order, then unowned ---
            float cursor = 0f;
            for (int i = 0; i < _planetBarSegments.Count; i++)
            {
                PlanetBarSegment seg = _planetBarSegments[i];
                bool isNeutral = i == teamCount;
                bool isTeam = i < teamCount;
                int count = isNeutral
                    ? unowned
                    : (isTeam && i < counts.Length ? Mathf.Max(0, counts[i]) : 0);
                bool viewed = isTeam && i == viewedIndex;
                bool show = (isTeam || isNeutral) && count > 0;
                seg.Root.SetActive(show);
                if (!show)
                    continue;

                float share = count / (float)capturableTotal;
                PlacePlanetBarSlice(seg.Rect, cursor, share, viewed);
                cursor += share;

                if (isNeutral)
                {
                    // Unowned remainder — dark ice so team colors stay the score read.
                    seg.Fill.color = new Color(0.16f, 0.20f, 0.28f, 0.95f);
                    bool showLabel = barWidth * share >= 22f;
                    seg.CountText.text = showLabel ? count.ToString() : string.Empty;
                    seg.CountText.color = new Color(0.62f, 0.72f, 0.84f, 0.85f);
                }
                else
                {
                    Color c = IndexToTeam(i).ToColor();
                    float alpha = viewed ? 1f : 0.78f;
                    seg.Fill.color = new Color(c.r, c.g, c.b, alpha);
                    bool showLabel = barWidth * share >= 18f;
                    seg.CountText.text = showLabel ? count.ToString() : string.Empty;
                    seg.CountText.color = Color.white;
                }
            }
        }

        /// <summary>
        /// Stretches one slice across <paramref name="share"/> of the bar, starting at
        /// <paramref name="start"/>. A 1px inset keeps neighboring team colors from bleeding.
        /// The viewed team uses the full bar height; other teams inset slightly so TAB
        /// still has a selected tab.
        /// </summary>
        static void PlacePlanetBarSlice(RectTransform rt, float start, float share, bool viewed)
        {
            if (rt == null)
                return;

            rt.anchorMin = new Vector2(start, viewed ? 0f : 0.12f);
            rt.anchorMax = new Vector2(start + share, viewed ? 1f : 0.88f);
            rt.offsetMin = new Vector2(1f, 0f);
            rt.offsetMax = new Vector2(-1f, 0f);
        }

        /// <summary>Grows the planet-bar slice pool (teams + unowned remainder).</summary>
        void EnsurePlanetBarSegmentCount(int count)
        {
            while (_planetBarSegments.Count < count)
                _planetBarSegments.Add(CreatePlanetBarSegment(_planetBarSegments.Count));
        }

        /// <summary>
        /// Builds one stretchy bar slice: tinted fill plus an optional planet-count label.
        /// Anchors are assigned later by <see cref="PlacePlanetBarSlice"/>.
        /// </summary>
        PlanetBarSegment CreatePlanetBarSegment(int index)
        {
            // [UNITY] Image + TMP child; parent later assigns anchors as planet-share fractions.
            var go = new GameObject("PlanetShare_" + index, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(_planetBarRoot, false);
            var rt = go.GetComponent<RectTransform>();
            var img = go.GetComponent<Image>();
            img.sprite = GetWhiteSprite();
            img.raycastTarget = false;

            var labelGo = new GameObject("Count", typeof(RectTransform));
            labelGo.transform.SetParent(go.transform, false);
            StretchFull(labelGo.GetComponent<RectTransform>());
            var tmp = labelGo.AddComponent<TextMeshProUGUI>();
            tmp.fontSize = 10f;
            tmp.fontStyle = FontStyles.Bold;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;
            tmp.raycastTarget = false;
            tmp.enableWordWrapping = false;
            tmp.overflowMode = TextOverflowModes.Ellipsis;

            return new PlanetBarSegment
            {
                Root = go,
                Rect = rt,
                Fill = img,
                CountText = tmp,
            };
        }

        // =========================================================================
        // Runtime UI construction
        // =========================================================================

        /// <summary>
        /// Builds the panel once: transparent black plate, team accent edge, full-width planet-control
        /// bar, compact title, and the player list.
        /// </summary>
        void EnsurePanelExists()
        {
            if (_panelRoot != null
                && _titleText != null
                && _contentRect != null
                && _planetBarRoot != null
                && _commandDeckBanner != null
                && _crewBanner != null)
                return;

            // Clean up a half-built tree after domain reload / script recompile.
            Transform existing = transform.Find("TeamLeaderboardPanel");
            if (existing != null)
                Destroy(existing.gameObject);
            _planetBarSegments.Clear();
            _commandDeckBanner = null;
            _crewBanner = null;

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

            // --- Planet-control bar: full panel width = every capturable world ---
            float planetBarTop = -TopChromePad;
            _planetBarRoot = new GameObject("PlanetControlBar", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image)).GetComponent<RectTransform>();
            _planetBarRoot.SetParent(_panelRoot.transform, false);
            _planetBarRoot.anchorMin = new Vector2(0f, 1f);
            _planetBarRoot.anchorMax = new Vector2(1f, 1f);
            _planetBarRoot.pivot = new Vector2(0.5f, 1f);
            _planetBarRoot.anchoredPosition = new Vector2(0f, planetBarTop);
            _planetBarRoot.sizeDelta = new Vector2(-(PanelSidePad * 2f + 4f), PlanetControlBarHeight);
            _planetBarRoot.offsetMin = new Vector2(PanelSidePad + 4f, _planetBarRoot.offsetMin.y);
            _planetBarRoot.offsetMax = new Vector2(-PanelSidePad, _planetBarRoot.offsetMax.y);
            var barTrack = _planetBarRoot.GetComponent<Image>();
            barTrack.sprite = GetWhiteSprite();
            barTrack.color = new Color(0.04f, 0.055f, 0.09f, 0.95f);
            barTrack.raycastTarget = false;

            // --- Compact header: viewed team name + [TAB] (tabs now live in the bar above) ---
            float headerTop = planetBarTop - PlanetControlBarHeight - 3f;
            var header = new GameObject("Header", typeof(RectTransform));
            header.transform.SetParent(_panelRoot.transform, false);
            var headerRt = header.GetComponent<RectTransform>();
            headerRt.anchorMin = new Vector2(0f, 1f);
            headerRt.anchorMax = new Vector2(1f, 1f);
            headerRt.pivot = new Vector2(0.5f, 1f);
            headerRt.anchoredPosition = new Vector2(0f, headerTop);
            headerRt.sizeDelta = new Vector2(-(PanelSidePad * 2f + 4f), HeaderHeight);
            headerRt.offsetMin = new Vector2(PanelSidePad + 4f, headerRt.offsetMin.y);
            headerRt.offsetMax = new Vector2(-PanelSidePad, headerRt.offsetMax.y);

            _titleText = CreateTmp(header.transform, "Title", 15f, TextAlignmentOptions.MidlineLeft,
                new Color(0.90f, 0.94f, 1f, 1f));
            StretchFull(_titleText.rectTransform);
            _titleText.fontStyle = FontStyles.Bold;
            _titleText.richText = true;
            _titleText.text = "Team A  [TAB]";

            // --- Scroll area fills everything under the planet bar + title ---
            float scrollTop = TopChromePad + PlanetControlBarHeight + 3f + HeaderHeight + 2f;
            var scrollGo = new GameObject("Scroll", typeof(RectTransform), typeof(ScrollRect));
            scrollGo.transform.SetParent(_panelRoot.transform, false);
            var scrollRt = scrollGo.GetComponent<RectTransform>();
            scrollRt.anchorMin = Vector2.zero;
            scrollRt.anchorMax = Vector2.one;
            scrollRt.offsetMin = new Vector2(PanelSidePad + 2f, PanelSidePad);
            scrollRt.offsetMax = new Vector2(-PanelSidePad, -scrollTop);
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

            _commandDeckBanner = CreateSectionBanner(_contentRect, "CommandDeckBanner");
            _crewBanner = CreateSectionBanner(_contentRect, "CrewBanner");

            UpdatePanelLayoutIfNeeded();
        }

        /// <summary>
        /// Dark-glass section rail with a thin accent stripe. Used for COMMAND DECK
        /// (gold) and CREW (ice) so the list reads as a cockpit roster, not a table.
        /// </summary>
        SectionBanner CreateSectionBanner(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(parent, false);
            var fill = go.GetComponent<Image>();
            fill.sprite = GetWhiteSprite();
            fill.color = new Color(0.018f, 0.028f, 0.045f, 0.96f);
            fill.raycastTarget = false;

            var accent = CreateUiImage(go.transform, "Accent", CommanderGold);
            var accentRt = accent.rectTransform;
            accentRt.anchorMin = new Vector2(0f, 1f);
            accentRt.anchorMax = new Vector2(1f, 1f);
            accentRt.pivot = new Vector2(0.5f, 1f);
            accentRt.anchoredPosition = Vector2.zero;
            accentRt.sizeDelta = new Vector2(-8f, 1.4f);

            var label = CreateTmp(go.transform, "Label", 11f, TextAlignmentOptions.Center, CommanderGold);
            StretchFull(label.rectTransform);
            label.fontStyle = FontStyles.Bold;
            label.characterSpacing = 1.6f;
            label.text = "COMMAND DECK";

            go.SetActive(false);
            return new SectionBanner
            {
                Root = go,
                Fill = fill,
                Accent = accent,
                Label = label,
            };
        }

        /// <summary>
        /// One pooled row: role icons | rank | profile badge | name | combined score | mute.
        /// Mute is the only Graphic that can receive pointer hits.
        /// </summary>
        RowWidgets CreateRow(int index)
        {
            var rowGo = new GameObject("Row_" + (index + 1), typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            rowGo.transform.SetParent(_contentRect, false);
            var bg = rowGo.GetComponent<Image>();
            bg.sprite = GetWhiteSprite();
            bg.color = new Color(0f, 0f, 0f, 0.25f);
            bg.raycastTarget = false;

            // Overlay chrome — ignoreLayout so the gold rail is not a HLG cell.
            var glow = CreateUiImage(rowGo.transform, "CommanderGlow", new Color(1f, 0.84f, 0.38f, 0.16f));
            var glowRt = glow.rectTransform;
            StretchFull(glowRt);
            glowRt.offsetMin = new Vector2(4f, 1f);
            glowRt.offsetMax = new Vector2(-1f, -1f);
            glow.enabled = false;
            IgnoreLayout(glow.gameObject);

            var rail = CreateUiImage(rowGo.transform, "CommanderRail", CommanderGold);
            var railRt = rail.rectTransform;
            railRt.anchorMin = new Vector2(0f, 0f);
            railRt.anchorMax = new Vector2(0f, 1f);
            railRt.pivot = new Vector2(0f, 0.5f);
            railRt.anchoredPosition = Vector2.zero;
            railRt.sizeDelta = new Vector2(3.5f, 0f);
            rail.enabled = false;
            IgnoreLayout(rail.gameObject);

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

            var rank = CreateRowLabel(CreateCell(rowGo.transform, "Rank", 36f), 13, TextAlignmentOptions.Center);
            rank.color = new Color(0.85f, 0.90f, 1f);

            var playerBadgeCell = CreateCell(rowGo.transform, "PlayerBadge", 0f);
            var playerBadgeImage = playerBadgeCell.gameObject.AddComponent<Image>();
            playerBadgeImage.preserveAspect = true;
            playerBadgeImage.raycastTarget = false;
            playerBadgeImage.enabled = false;
            playerBadgeImage.color = Color.white;

            var nameCell = CreateCell(rowGo.transform, "Name", -1f);
            nameCell.GetComponent<LayoutElement>().flexibleWidth = 1f;
            var name = CreateRowLabel(nameCell, 14, TextAlignmentOptions.Left);
            name.color = Color.white;

            var score = CreateRowLabel(CreateCell(rowGo.transform, "Score", 64f), 15, TextAlignmentOptions.Right);
            score.color = new Color(0.95f, 0.86f, 0.55f);
            score.fontStyle = FontStyles.Bold;

            // --- Comms mute (after score so names and scores stay aligned) ---
            // [TITAN-ORBIT] Local presentation mute. Click reads OwnerNetworkId on the
            // pooled widget so a reused row never toggles the previous occupant.
            var muteCell = CreateCell(rowGo.transform, "Mute", MuteCellSize);
            var muteFill = muteCell.gameObject.AddComponent<Image>();
            muteFill.sprite = GetWhiteSprite();
            muteFill.color = MuteFill;
            muteFill.raycastTarget = true;

            var muteButton = muteCell.gameObject.AddComponent<Button>();
            muteButton.targetGraphic = muteFill;
            muteButton.transition = Selectable.Transition.ColorTint;
            var muteColors = muteButton.colors;
            muteColors.normalColor = Color.white;
            muteColors.highlightedColor = new Color(0.85f, 0.92f, 1f, 1f);
            muteColors.pressedColor = new Color(0.70f, 0.80f, 0.92f, 1f);
            muteColors.selectedColor = Color.white;
            muteButton.colors = muteColors;
            muteButton.navigation = new Navigation { mode = Navigation.Mode.None };

            var muteMark = CreateRowLabel(muteCell, 11, TextAlignmentOptions.Center);
            muteMark.text = "C";
            muteMark.fontStyle = FontStyles.Bold;
            muteMark.color = MuteOpenCaption;

            var slash = CreateUiImage(muteCell, "Slash", MuteClosedCaption);
            var slashRt = slash.rectTransform;
            StretchFull(slashRt);
            slashRt.offsetMin = new Vector2(5f, 8f);
            slashRt.offsetMax = new Vector2(-5f, -8f);
            slash.rectTransform.localEulerAngles = new Vector3(0f, 0f, -38f);
            slash.raycastTarget = false;
            slash.enabled = false;

            muteCell.gameObject.SetActive(false);

            var widgets = new RowWidgets
            {
                Root = rowGo,
                Background = bg,
                CommanderRail = rail,
                CommanderGlow = glow,
                BadgeContainer = badges,
                PlayerBadgeCell = playerBadgeCell,
                PlayerBadgeImage = playerBadgeImage,
                RankText = rank,
                NameText = name,
                ScoreText = score,
                MuteRoot = muteCell.gameObject,
                MuteButton = muteButton,
                MuteFill = muteFill,
                MuteMark = muteMark,
                MuteSlash = slash,
            };

            muteButton.onClick.AddListener(() => OnMuteClicked(widgets));
            return widgets;
        }

        /// <summary>
        /// Marks a child so <see cref="HorizontalLayoutGroup"/> does not treat it as a cell.
        /// Used for the gold command rail overlaid on the row.
        /// </summary>
        static void IgnoreLayout(GameObject go)
        {
            var le = go.GetComponent<LayoutElement>();
            if (le == null)
                le = go.AddComponent<LayoutElement>();
            le.ignoreLayout = true;
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
