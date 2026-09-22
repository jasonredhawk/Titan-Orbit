using System.Collections.Generic;
using System.Globalization;
using TitanOrbit.Core;
using TitanOrbit.ECS;
using TitanOrbit.NetCode;
using TMPro;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Full-screen congrats card when one team is the only color left on the map.
    /// Neutral worlds can still be uncaptured. Shows the winning
    /// team's Command Deck (ace / miner / marshal), a match-wide scoreboard, and a
    /// button that disconnects back to the Main Menu.
    /// <para>
    /// Client presentation only. The server decides the winner in
    /// <see cref="CaptureSystem"/>. The match singleton is not a ghost, so the card
    /// reads the server world on a local host and <see cref="MatchWonRpc"/> on a
    /// remote client. A complete planet list with only one team color also opens it.
    /// Stats are the ghosted <see cref="ShipMatchStats"/> already on each ship
    /// (kills, gems deposited, troops delivered). We copy them once when the win
    /// arrives — this overlay does not tick a scoreboard every frame.
    /// </para>
    /// Paired with <see cref="NceGameFlowController"/> (hides the gameplay HUD while
    /// the match is won) and <see cref="GameplayCursorController"/> (OS arrow while
    /// <see cref="IsShowing"/>). Leave path matches
    /// <see cref="PlayerEliminatedScreenController"/>.
    /// </summary>
    public class MatchEndScreenController : MonoBehaviour
    {
        /// <summary>
        /// How long we wait for ship ghosts before painting an empty roster.
        /// A win can replicate one frame before the client world is readable.
        /// </summary>
        const float SnapshotRetrySeconds = 0.75f;

        /// <summary>
        /// Longer wait while ship gathers are illegal (join-settle / Instantiates).
        /// The menu button still works; we just hold the roster until the copy is safe.
        /// </summary>
        const float SnapshotBlockedSeconds = 4f;

        /// <summary>Void glass fill — same family as the eliminated overlay.</summary>
        static readonly Color VoidFill = new Color(0.012f, 0.016f, 0.028f, 0.97f);

        /// <summary>Ice caption used for telemetry labels and the menu button.</summary>
        static readonly Color IceCaption = new Color(0.62f, 0.78f, 0.95f, 0.92f);

        /// <summary>Near-white body copy on the dark card.</summary>
        static readonly Color BodyText = new Color(0.88f, 0.92f, 0.98f, 1f);

        /// <summary>Dimmer body for a hull that is dead at the moment of victory.</summary>
        static readonly Color DeadText = new Color(0.62f, 0.68f, 0.76f, 0.85f);

        /// <summary>
        /// True while the congrats card is visible. <see cref="GameplayCursorController"/>
        /// reads this to restore the OS arrow over the menu button.
        /// </summary>
        public static bool IsShowing { get; private set; }

        /// <summary>
        /// [UNITY] Domain Reload off leaves this static hot. Called from
        /// <see cref="GameplayCursorController"/> before scene load.
        /// </summary>
        public static void ClearShowingFlag() => IsShowing = false;

        /// <summary>Root canvas. Hidden until a team wins.</summary>
        [SerializeField] GameObject overlayRoot;

        /// <summary>Big "{Team} Wins" line, tinted with that team's color.</summary>
        [SerializeField] TextMeshProUGUI titleText;

        /// <summary>Disconnect button. Wired once when the card is built.</summary>
        [SerializeField] Button continueButton;

        /// <summary>Thin team-color stripe along the left edge of the card.</summary>
        Image _teamRail;

        /// <summary>Frozen match length, captured the frame the win first arrives.</summary>
        TextMeshProUGUI _timeValue;

        /// <summary>Winning team's worlds over every world that can be owned.</summary>
        TextMeshProUGUI _worldsValue;

        /// <summary>Sum of combined scores on the winning team.</summary>
        TextMeshProUGUI _scoreValue;

        /// <summary>Ace, miner, and marshal cards. Index matches <see cref="SeatOrder"/>.</summary>
        readonly SeatWidgets[] _seats = new SeatWidgets[3];

        /// <summary>Scroll content. Rows are parented here once, then left alone.</summary>
        RectTransform _scrollContent;

        /// <summary>So the roster opens at the winning team, not the bottom of the list.</summary>
        ScrollRect _scroll;

        /// <summary>Shown inside the scroll area until the snapshot lands, then hidden.</summary>
        TextMeshProUGUI _boardStatus;

        /// <summary>Winning team we have already started painting. None = card is down.</summary>
        TeamId _shownWinner = TeamId.None;

        /// <summary>True after the roster has been copied. Update stops touching the card.</summary>
        bool _snapshotLocked;

        /// <summary>Unscaled time when we first saw this win, for the retry window.</summary>
        float _snapshotStartedAt;

        /// <summary>Match seconds frozen at the win, so the chip does not keep climbing.</summary>
        float _frozenMatchSeconds;

        /// <summary>True while <see cref="TitanOrbitSessionManager.ReturnToMainMenuAsync"/> is in flight.</summary>
        bool _leaving;

        /// <summary>Same GameObject as this component. Clears menu latches before disconnect.</summary>
        NceGameFlowController _flow;

        /// <summary>Reused roster. Cleared on each snapshot attempt.</summary>
        readonly List<ScoreRow> _rows = new List<ScoreRow>(32);

        /// <summary>Planet ids borrowed from <see cref="EcsGameBridge.CopyKnownPlanetIds"/>.</summary>
        readonly List<int> _planetIds = new List<int>(32);

        /// <summary>Owned planets per team index (Team A = 0). Length 5.</summary>
        readonly int[] _planetsByTeam = new int[5];

        /// <summary>
        /// One pilot on the results card. Copied from ghosted ship stats, not live cargo.
        /// </summary>
        struct ScoreRow
        {
            /// <summary>[NETCODE] GhostOwner.NetworkId. Tie-break and name lookup.</summary>
            public int NetworkId;

            /// <summary>Team this hull was on when the match ended.</summary>
            public TeamId Team;

            /// <summary>Roster name, already truncated for the row.</summary>
            public string Name;

            /// <summary>Match-long enemy ships destroyed.</summary>
            public int Kills;

            /// <summary>Match-long gems deposited at moons.</summary>
            public int Gems;

            /// <summary>Match-long troops unloaded onto planets.</summary>
            public int People;

            /// <summary>kills×100 + gems×2 + people×5.</summary>
            public int Score;

            /// <summary>True for this machine's ship, so the row can say YOU.</summary>
            public bool IsLocal;

            /// <summary>True when the hull was dead at the snapshot.</summary>
            public bool IsDead;

            /// <summary>1-based place across every team, by combined score.</summary>
            public int GlobalRank;
        }

        /// <summary>The three Command Deck cards. Built once, text swapped on snapshot.</summary>
        struct SeatWidgets
        {
            /// <summary>Card fill. Gold wash when someone earned the seat.</summary>
            public Image Background;

            /// <summary>Pilot name, or an em dash when nobody scored in that category.</summary>
            public TextMeshProUGUI Name;

            /// <summary>Winning stat, for example "12 KILLS".</summary>
            public TextMeshProUGUI Stat;
        }

        /// <summary>Build the card once and keep it hidden until a team wins.</summary>
        void Awake()
        {
            _flow = GetComponent<NceGameFlowController>();
            EnsureUi();
            Hide();
        }

        /// <summary>
        /// Watches the ghosted match singleton. The frame a winner appears we open the
        /// card and copy stats. After that copy lands, this method does nothing until
        /// the winner clears (leave match, or a new session on the same scene).
        /// </summary>
        void Update()
        {
            // --- No winner ---
            // Also clears the leave latch. Disconnect keeps this component alive on
            // the menu root; if _leaving stayed true, the next match could never open.
            bool haveWinner = EcsGameBridge.TryGetMatchState(out var match) && match.WinningTeam != TeamId.None;
            if (!haveWinner)
            {
                if (_shownWinner != TeamId.None || IsShowing || _leaving)
                    Dismiss();
                return;
            }

            // The button already hid the card. Do not pop it again while disconnect runs.
            if (_leaving)
                return;

            if (_shownWinner != match.WinningTeam)
                BeginShow(match.WinningTeam, match.MatchTimer);
            else if (!_snapshotLocked)
                TrySnapshot();
        }

        /// <summary>
        /// Opens the card immediately with the team name and frozen clock, then starts
        /// the short wait for ship ghosts.
        /// </summary>
        /// <param name="team">Winning team from the ghosted match singleton.</param>
        /// <param name="matchSeconds">Elapsed seconds at the moment we first saw the win.</param>
        void BeginShow(TeamId team, float matchSeconds)
        {
            // --- Latch this win ---
            // Drop any roster left over from a previous match on this same scene.
            _rows.Clear();
            _shownWinner = team;
            _frozenMatchSeconds = matchSeconds;
            _snapshotStartedAt = Time.unscaledTime;
            _snapshotLocked = false;

            EnsureUi();
            if (overlayRoot != null)
                overlayRoot.SetActive(true);
            IsShowing = true;

            PaintHeader(team);
            PaintSeatsUnclaimed();
            if (_boardStatus != null)
            {
                _boardStatus.gameObject.SetActive(true);
                _boardStatus.text = "READING MATCH TELEMETRY";
            }

            if (continueButton != null)
                continueButton.interactable = true;

            TrySnapshot();
        }

        /// <summary>
        /// Copies ships and planet ownership once they are readable. Retries for
        /// <see cref="SnapshotRetrySeconds"/>, or up to <see cref="SnapshotBlockedSeconds"/>
        /// while ship gathers are illegal, then paints whatever we have (even an
        /// empty roster) so the button is never stuck behind a spinner.
        /// </summary>
        void TrySnapshot()
        {
            // [TITAN-ORBIT] Do not ToEntityArray ships while join-settle forbids it.
            // Hold the "reading" line instead of freezing an empty roster.
            bool gatherBlocked = ClientJoinSettleCache.ShouldSkipShipEntityQueries;
            bool gotShips = !gatherBlocked && TryCopyShips();
            if (!gotShips)
            {
                float limit = gatherBlocked ? SnapshotBlockedSeconds : SnapshotRetrySeconds;
                if (Time.unscaledTime - _snapshotStartedAt < limit)
                    return;
            }

            // --- Freeze the card ---
            // [TITAN-ORBIT] One copy. The server keeps simulating after the win, but
            // the congrats numbers stay the moment domination landed.
            AssignGlobalRanks();
            PaintHeader(_shownWinner);
            PaintCommandDeck(_shownWinner);
            PaintScoreboard(_shownWinner);
            _snapshotLocked = true;
        }

        /// <summary>
        /// One ship-archetype read on the client presentation world. Skipped while
        /// join-settle still forbids ship gathers — we retry next frame instead.
        /// </summary>
        /// <returns>True when at least one teamed ship was copied.</returns>
        bool TryCopyShips()
        {
            _rows.Clear();

            // [TITAN-ORBIT] Same gate as minimap ship sync. A gather during ship
            // Instantiates has crashed the client; waiting out the retry window is safer.
            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
                return false;

            World world = EcsGameBridge.GetVisualizationWorld();
            if (world == null || !world.IsCreated)
                return false;

            // Names first, then the ship copy, so the roster does not paint "Player N"
            // for someone whose name was already announced.
            EcsGameBridge.RefreshPlayerDisplayNameCache();

            var em = world.EntityManager;
            using var query = em.CreateEntityQuery(
                ComponentType.ReadOnly<ShipTag>(),
                ComponentType.ReadOnly<GhostOwner>(),
                ComponentType.ReadOnly<ShipState>());
            using var entities = query.ToEntityArray(Allocator.Temp);
            using var owners = query.ToComponentDataArray<GhostOwner>(Allocator.Temp);
            using var states = query.ToComponentDataArray<ShipState>(Allocator.Temp);

            int localId = EcsGameBridge.GetLocalNetworkId();
            for (int i = 0; i < entities.Length; i++)
            {
                ShipState ship = states[i];
                if (ship.Team == TeamId.None || ship.AwaitingTeamSelection)
                    continue;

                int networkId = owners[i].NetworkId;
                int kills = 0;
                int gems = 0;
                int people = 0;
                if (em.HasComponent<ShipMatchStats>(entities[i]))
                {
                    ShipMatchStats stats = em.GetComponentData<ShipMatchStats>(entities[i]);
                    kills = Mathf.Max(0, stats.Kills);
                    gems = Mathf.Max(0, stats.GemsDeposited);
                    people = Mathf.Max(0, stats.PeopleDelivered);
                }

                _rows.Add(new ScoreRow
                {
                    NetworkId = networkId,
                    Team = ship.Team,
                    Name = TruncateName(EcsGameBridge.GetCachedPlayerDisplayName(networkId)),
                    Kills = kills,
                    Gems = gems,
                    People = people,
                    Score = TeamCommanderRules.CombinedScore(kills, gems, people),
                    IsLocal = networkId > 0 && networkId == localId,
                    IsDead = ship.IsDead,
                });
            }

            return _rows.Count > 0;
        }

        /// <summary>
        /// Writes 1-based places. Higher combined score wins; kills break ties; then
        /// the lower NetworkId, matching the in-match leaderboard.
        /// </summary>
        void AssignGlobalRanks()
        {
            _rows.Sort(CompareScoreDesc);
            for (int i = 0; i < _rows.Count; i++)
            {
                ScoreRow row = _rows[i];
                row.GlobalRank = i + 1;
                _rows[i] = row;
            }
        }

        /// <summary>Score, then kills, then lower NetworkId. Used by <see cref="List{T}.Sort"/>.</summary>
        static int CompareScoreDesc(ScoreRow a, ScoreRow b)
        {
            int c = b.Score.CompareTo(a.Score);
            if (c != 0)
                return c;
            c = b.Kills.CompareTo(a.Kills);
            if (c != 0)
                return c;
            return a.NetworkId.CompareTo(b.NetworkId);
        }

        /// <summary>Team name, clock, worlds held, and the winning team's combined score.</summary>
        /// <param name="team">Winning team. Colors the title and the side rail.</param>
        void PaintHeader(TeamId team)
        {
            Color teamColor = team.ToColor();
            if (_teamRail != null)
                _teamRail.color = teamColor;

            if (titleText != null)
            {
                titleText.text = team.ToDisplayName().ToUpperInvariant() + " WINS";
                titleText.color = teamColor;
            }

            CountPlanets(out int capturable);
            int teamIndex = (int)team - 1;
            int owned = teamIndex >= 0 && teamIndex < _planetsByTeam.Length
                ? _planetsByTeam[teamIndex]
                : 0;

            int teamScore = 0;
            for (int i = 0; i < _rows.Count; i++)
            {
                if (_rows[i].Team == team)
                    teamScore += _rows[i].Score;
            }

            if (_timeValue != null)
                _timeValue.text = FormatMatchTime(_frozenMatchSeconds);
            if (_worldsValue != null)
            {
                // Empty cache means the planet list is not ready. A win no longer
                // implies every world was taken, so we do not invent a full tally.
                _worldsValue.text = _planetIds.Count == 0
                    ? "—"
                    : owned + "/" + capturable;
            }
            if (_scoreValue != null)
                _scoreValue.text = _snapshotLocked || _rows.Count > 0
                    ? FormatInt(teamScore)
                    : "—";
        }

        /// <summary>
        /// Tallies owned worlds from the bridge planet cache (no new map-body query)
        /// and the session recipe so the denominator is every capturable planet.
        /// </summary>
        /// <param name="capturable">Worlds that can be owned. At least 1.</param>
        void CountPlanets(out int capturable)
        {
            for (int i = 0; i < _planetsByTeam.Length; i++)
                _planetsByTeam[i] = 0;

            int teamCount = 2;
            if (EcsGameBridge.TryGetActiveTeamCount(out int active) && active > 0)
                teamCount = Mathf.Clamp(active, 1, 5);
            else if (MapSessionMetaCache.TeamCount > 0)
                teamCount = Mathf.Clamp(MapSessionMetaCache.TeamCount, 1, 5);

            // [HYBRID] CopyKnownPlanetIds reads the per-frame cache EcsGameBridge
            // already maintains. It does not walk the map when join-settle skipped it.
            EcsGameBridge.CopyKnownPlanetIds(_planetIds);
            for (int i = 0; i < _planetIds.Count; i++)
            {
                if (!EcsGameBridge.TryGetPlanetStateByPlanetId(_planetIds[i], out PlanetState planet))
                    continue;
                int index = (int)planet.Ownership - 1;
                if (index < 0 || index >= teamCount || index >= _planetsByTeam.Length)
                    continue;
                _planetsByTeam[index]++;
            }

            int metaTotal = 0;
            if (MapSessionMetaCache.LivePlanetCount > 0)
                metaTotal = MapSessionMetaCache.LivePlanetCount;
            else if (MapSessionMetaCache.TeamCount > 0 || MapSessionMetaCache.NeutralPlanetCount > 0)
            {
                metaTotal = Mathf.Max(0, MapSessionMetaCache.TeamCount)
                            + Mathf.Max(0, MapSessionMetaCache.NeutralPlanetCount);
            }

            capturable = Mathf.Max(_planetIds.Count, metaTotal);
            if (capturable <= 0)
                capturable = Mathf.Max(1, teamCount);
        }

        /// <summary>Clears the three seats while we are still waiting on ship ghosts.</summary>
        void PaintSeatsUnclaimed()
        {
            for (int i = 0; i < _seats.Length; i++)
                PaintSeat(_seats[i], claimed: false, "—", "UNCLAIMED");
        }

        /// <summary>
        /// Names the winning team's category leaders from lifetime match totals.
        /// Dead hulls still qualify — an ace who died on the last push keeps the seat.
        /// Zero in a category leaves that card unclaimed. One pilot can hold all three.
        /// </summary>
        /// <param name="team">Winning team. Other teams are not on the Command Deck.</param>
        void PaintCommandDeck(TeamId team)
        {
            int aceId = 0, minerId = 0, marshalId = 0;
            int aceStat = 0, minerStat = 0, marshalStat = 0;
            string aceName = "—", minerName = "—", marshalName = "—";

            for (int i = 0; i < _rows.Count; i++)
            {
                ScoreRow row = _rows[i];
                if (row.Team != team || row.NetworkId <= 0)
                    continue;

                if (TeamCommandRoleRules.IsBetterTop(row.Kills, row.NetworkId, aceStat, aceId))
                {
                    aceStat = row.Kills;
                    aceId = row.NetworkId;
                    aceName = row.Name;
                }

                if (TeamCommandRoleRules.IsBetterTop(row.Gems, row.NetworkId, minerStat, minerId))
                {
                    minerStat = row.Gems;
                    minerId = row.NetworkId;
                    minerName = row.Name;
                }

                if (TeamCommandRoleRules.IsBetterTop(row.People, row.NetworkId, marshalStat, marshalId))
                {
                    marshalStat = row.People;
                    marshalId = row.NetworkId;
                    marshalName = row.Name;
                }
            }

            PaintSeat(_seats[0], aceId > 0, aceName, FormatInt(aceStat) + " KILLS");
            PaintSeat(_seats[1], minerId > 0, minerName, FormatInt(minerStat) + " GEMS");
            PaintSeat(_seats[2], marshalId > 0, marshalName, FormatInt(marshalStat) + " TROOPS");
        }

        /// <summary>Writes one Command Deck card. Gold wash only when the seat was earned.</summary>
        static void PaintSeat(SeatWidgets seat, bool claimed, string name, string stat)
        {
            if (seat.Background != null)
            {
                seat.Background.color = claimed
                    ? new Color(TeamCommanderRules.Gold.r, TeamCommanderRules.Gold.g, TeamCommanderRules.Gold.b, 0.16f)
                    : new Color(0.03f, 0.04f, 0.07f, 0.95f);
            }

            if (seat.Name != null)
            {
                seat.Name.text = name;
                seat.Name.color = claimed ? TeamCommanderRules.Gold : DeadText;
            }

            if (seat.Stat != null)
            {
                seat.Stat.text = claimed ? stat : "UNCLAIMED";
                seat.Stat.color = claimed ? BodyText : IceCaption;
            }
        }

        /// <summary>
        /// Rebuilds the scrolling roster. Winning team first, then the other teams
        /// in A–E order. Rows stay in global score order inside each team.
        /// </summary>
        /// <param name="winner">Team that just won. Their block is on top.</param>
        void PaintScoreboard(TeamId winner)
        {
            if (_scrollContent == null)
                return;

            for (int i = _scrollContent.childCount - 1; i >= 0; i--)
                Destroy(_scrollContent.GetChild(i).gameObject);

            if (_rows.Count == 0)
            {
                if (_boardStatus != null)
                {
                    _boardStatus.gameObject.SetActive(true);
                    _boardStatus.text = "NO CREW TELEMETRY";
                }

                return;
            }

            if (_boardStatus != null)
                _boardStatus.gameObject.SetActive(false);

            int teamCount = 5;
            if (EcsGameBridge.TryGetActiveTeamCount(out int active) && active > 0)
                teamCount = Mathf.Clamp(active, 1, 5);

            AddTeamBlock(winner);
            for (int i = 0; i < teamCount; i++)
            {
                TeamId team = (TeamId)(i + 1);
                if (team == winner)
                    continue;
                AddTeamBlock(team);
            }

            if (_scroll != null)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(_scrollContent);
                _scroll.verticalNormalizedPosition = 1f;
            }
        }

        /// <summary>One team header plus that team's pilots. Skips a team with nobody on it.</summary>
        /// <param name="team">Faction to append under the current scroll cursor.</param>
        void AddTeamBlock(TeamId team)
        {
            int count = 0;
            int teamScore = 0;
            for (int i = 0; i < _rows.Count; i++)
            {
                if (_rows[i].Team != team)
                    continue;
                count++;
                teamScore += _rows[i].Score;
            }

            if (count == 0)
                return;

            Color teamColor = team.ToColor();
            var header = CreateText(_scrollContent, "TeamHeader", 15f, FontStyles.Bold, TextAlignmentOptions.MidlineLeft);
            header.color = teamColor;
            header.text = team.ToDisplayName().ToUpperInvariant() + "    " + FormatInt(teamScore);
            var headerLe = header.gameObject.AddComponent<LayoutElement>();
            headerLe.preferredHeight = 26f;
            headerLe.minHeight = 26f;

            for (int i = 0; i < _rows.Count; i++)
            {
                if (_rows[i].Team != team)
                    continue;
                AddScoreRow(_rows[i], teamColor);
            }
        }

        /// <summary>One pilot line: place, name, score, and the three raw stats.</summary>
        /// <param name="row">Already ranked snapshot row.</param>
        /// <param name="teamColor">Used as a hairline on the local player's row.</param>
        void AddScoreRow(ScoreRow row, Color teamColor)
        {
            var go = new GameObject("Row", typeof(RectTransform), typeof(Image), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
            go.transform.SetParent(_scrollContent, false);

            var bg = go.GetComponent<Image>();
            bg.raycastTarget = false;
            bg.color = row.IsLocal
                ? new Color(teamColor.r, teamColor.g, teamColor.b, 0.22f)
                : new Color(0.03f, 0.04f, 0.07f, 0.72f);

            var layout = go.GetComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(8, 8, 0, 0);
            layout.spacing = 6f;
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = true;

            var le = go.GetComponent<LayoutElement>();
            le.preferredHeight = 28f;
            le.minHeight = 28f;

            Color text = row.IsDead ? DeadText : BodyText;
            string name = row.IsLocal ? row.Name + "  YOU" : row.Name;

            AddCell(go.transform, "#" + row.GlobalRank, 52f, text, TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
            AddCell(go.transform, name, 0f, row.IsLocal ? Color.white : text, TextAlignmentOptions.MidlineLeft, FontStyles.Normal);
            AddCell(go.transform, FormatInt(row.Score), 88f, text, TextAlignmentOptions.MidlineRight, FontStyles.Bold);
            AddCell(go.transform, row.Kills + " K", 64f, IceCaption, TextAlignmentOptions.MidlineRight, FontStyles.Normal);
            AddCell(go.transform, row.Gems + " G", 72f, IceCaption, TextAlignmentOptions.MidlineRight, FontStyles.Normal);
            AddCell(go.transform, row.People + " T", 64f, IceCaption, TextAlignmentOptions.MidlineRight, FontStyles.Normal);
        }

        /// <summary>
        /// Fixed-width stat cell, or a flexible name cell when <paramref name="width"/> is 0.
        /// </summary>
        static void AddCell(
            Transform parent,
            string text,
            float width,
            Color color,
            TextAlignmentOptions align,
            FontStyles style)
        {
            var tmp = CreateText(parent, "Cell", 15f, style, align);
            tmp.color = color;
            tmp.text = text;
            var cell = tmp.gameObject.AddComponent<LayoutElement>();
            if (width <= 0f)
            {
                cell.flexibleWidth = 1f;
                cell.minWidth = 80f;
            }
            else
            {
                cell.preferredWidth = width;
                cell.minWidth = width;
            }
        }

        /// <summary>
        /// Menu button: same disconnect as the eliminated overlay and the Escape menu.
        /// Hides the card first so a slow leave does not leave the roster up.
        /// </summary>
        async void OnReturnToMenuClicked()
        {
            if (_leaving)
                return;

            _leaving = true;
            if (continueButton != null)
                continueButton.interactable = false;
            Hide();

            if (_flow != null)
                _flow.NotifyReturningToMainMenu();

            var session = TitanOrbitSessionManager.Instance;
            if (session == null)
            {
                _leaving = false;
                return;
            }

            try
            {
                await session.ReturnToMainMenuAsync();
            }
            catch (System.Exception ex)
            {
                Debug.LogError("[MatchEnd] Leave failed: " + ex.Message);
                _leaving = false;
                if (continueButton != null)
                    continueButton.interactable = true;
                if (_shownWinner != TeamId.None && overlayRoot != null)
                {
                    overlayRoot.SetActive(true);
                    IsShowing = true;
                }
            }
        }

        /// <summary>Drops the win latch so a later match on this scene can show the card again.</summary>
        void Dismiss()
        {
            _shownWinner = TeamId.None;
            _snapshotLocked = false;
            _leaving = false;
            Hide();
        }

        /// <summary>Hides the canvas and clears <see cref="IsShowing"/> so combat cursors can return.</summary>
        void Hide()
        {
            if (overlayRoot != null)
                overlayRoot.SetActive(false);
            IsShowing = false;
        }

        /// <summary>[UNITY] Clears the static flag if this instance was the one showing.</summary>
        void OnDestroy()
        {
            if (IsShowing)
                IsShowing = false;
        }

        /// <summary>
        /// Builds the void-glass card if this component does not already own one.
        /// A hot-reload leftover named MatchEndOverlay is destroyed and replaced,
        /// because the old card was only a title and a Continue button.
        /// </summary>
        void EnsureUi()
        {
            if (overlayRoot != null && titleText != null && continueButton != null && _scrollContent != null)
                return;

            Transform existing = transform.Find("MatchEndOverlay");
            if (existing != null)
                Destroy(existing.gameObject);

            var canvasGo = new GameObject("MatchEndOverlay");
            canvasGo.transform.SetParent(transform, false);

            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 9000;
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            canvasGo.AddComponent<GraphicRaycaster>();
            overlayRoot = canvasGo;

            // Dim the match behind the card so the roster reads as the focus.
            var backdrop = CreateImage(canvasGo.transform, "Backdrop", new Color(0f, 0f, 0f, 0.62f));
            Stretch(backdrop.rectTransform);
            backdrop.raycastTarget = true;

            var card = CreateImage(canvasGo.transform, "Card", VoidFill);
            var cardRt = card.rectTransform;
            cardRt.anchorMin = cardRt.anchorMax = new Vector2(0.5f, 0.5f);
            cardRt.pivot = new Vector2(0.5f, 0.5f);
            cardRt.sizeDelta = new Vector2(1040f, 900f);

            var cardLayout = card.gameObject.AddComponent<VerticalLayoutGroup>();
            cardLayout.padding = new RectOffset(28, 28, 18, 22);
            cardLayout.spacing = 8f;
            cardLayout.childAlignment = TextAnchor.UpperCenter;
            cardLayout.childControlWidth = true;
            cardLayout.childControlHeight = true;
            cardLayout.childForceExpandWidth = true;
            cardLayout.childForceExpandHeight = false;

            _teamRail = CreateImage(card.transform, "TeamRail", Color.white);
            var railLe = _teamRail.gameObject.AddComponent<LayoutElement>();
            railLe.ignoreLayout = true;
            var railRt = _teamRail.rectTransform;
            railRt.anchorMin = new Vector2(0f, 0f);
            railRt.anchorMax = new Vector2(0f, 1f);
            railRt.pivot = new Vector2(0f, 0.5f);
            railRt.sizeDelta = new Vector2(4f, 0f);
            railRt.anchoredPosition = Vector2.zero;
            _teamRail.raycastTarget = false;

            var caption = CreateText(card.transform, "Caption", 16f, FontStyles.Bold, TextAlignmentOptions.Center);
            caption.text = "VICTORY";
            caption.color = IceCaption;
            caption.characterSpacing = 6f;
            SetPreferredHeight(caption.gameObject, 22f);

            titleText = CreateText(card.transform, "Title", 46f, FontStyles.Bold, TextAlignmentOptions.Center);
            titleText.text = "TEAM WINS";
            titleText.characterSpacing = 1.6f;
            SetPreferredHeight(titleText.gameObject, 58f);

            BuildTelemetryRow(card.transform);
            BuildSectionLabel(card.transform, "COMMAND DECK");
            BuildSeatRow(card.transform);
            BuildSectionLabel(card.transform, "SCOREBOARD");
            BuildColumnHeader(card.transform);
            BuildScroll(card.transform);
            BuildMenuButton(card.transform);
        }

        /// <summary>Three chips under the title: time, worlds, winning-team score.</summary>
        void BuildTelemetryRow(Transform card)
        {
            var row = new GameObject("Telemetry", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
            row.transform.SetParent(card, false);
            var layout = row.GetComponent<HorizontalLayoutGroup>();
            layout.spacing = 10f;
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = true;
            SetPreferredHeight(row, 68f);

            _timeValue = BuildChip(row.transform, "TIME");
            _worldsValue = BuildChip(row.transform, "WORLDS");
            _scoreValue = BuildChip(row.transform, "TEAM SCORE");
        }

        /// <summary>One telemetry chip: small ice caption, large value. Returns the value label.</summary>
        /// <param name="caption">Uppercase chip title, for example TIME.</param>
        TextMeshProUGUI BuildChip(Transform parent, string caption)
        {
            var chip = CreateImage(parent, caption + "Chip", new Color(0.03f, 0.045f, 0.07f, 0.95f));
            var layout = chip.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(8, 8, 6, 6);
            layout.spacing = 0f;
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            var cap = CreateText(chip.transform, "Caption", 13f, FontStyles.Bold, TextAlignmentOptions.Center);
            cap.text = caption;
            cap.color = IceCaption;
            cap.characterSpacing = 2.2f;
            SetPreferredHeight(cap.gameObject, 18f);

            var value = CreateText(chip.transform, "Value", 26f, FontStyles.Bold, TextAlignmentOptions.Center);
            value.text = "—";
            value.color = BodyText;
            SetPreferredHeight(value.gameObject, 32f);
            return value;
        }

        /// <summary>Uppercase section caption between the chips, the deck, and the roster.</summary>
        static void BuildSectionLabel(Transform card, string text)
        {
            var label = CreateText(card, text, 14f, FontStyles.Bold, TextAlignmentOptions.MidlineLeft);
            label.text = text;
            label.color = IceCaption;
            label.characterSpacing = 2.4f;
            SetPreferredHeight(label.gameObject, 20f);
        }

        /// <summary>Ace / Miner / Marshal cards in a horizontal row.</summary>
        void BuildSeatRow(Transform card)
        {
            var row = new GameObject("Seats", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
            row.transform.SetParent(card, false);
            var layout = row.GetComponent<HorizontalLayoutGroup>();
            layout.spacing = 10f;
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = true;
            SetPreferredHeight(row, 96f);

            _seats[0] = BuildSeat(row.transform, "ACE");
            _seats[1] = BuildSeat(row.transform, "MINER");
            _seats[2] = BuildSeat(row.transform, "MARSHAL");
        }

        /// <summary>One Command Deck card. Role is fixed; name and stat are filled later.</summary>
        /// <param name="role">ACE, MINER, or MARSHAL.</param>
        static SeatWidgets BuildSeat(Transform parent, string role)
        {
            var bg = CreateImage(parent, role, new Color(0.03f, 0.04f, 0.07f, 0.95f));
            var layout = bg.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(12, 12, 8, 8);
            layout.spacing = 2f;
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            var roleLabel = CreateText(bg.transform, "Role", 13f, FontStyles.Bold, TextAlignmentOptions.Center);
            roleLabel.text = role;
            roleLabel.color = IceCaption;
            roleLabel.characterSpacing = 2f;
            SetPreferredHeight(roleLabel.gameObject, 18f);

            var name = CreateText(bg.transform, "Name", 20f, FontStyles.Bold, TextAlignmentOptions.Center);
            name.text = "—";
            name.color = TeamCommanderRules.Gold;
            SetPreferredHeight(name.gameObject, 28f);

            var stat = CreateText(bg.transform, "Stat", 14f, FontStyles.Normal, TextAlignmentOptions.Center);
            stat.text = "UNCLAIMED";
            stat.color = IceCaption;
            SetPreferredHeight(stat.gameObject, 20f);

            return new SeatWidgets
            {
                Background = bg,
                Name = name,
                Stat = stat,
            };
        }

        /// <summary>Column captions above the scroll so they stay put while the roster moves.</summary>
        static void BuildColumnHeader(Transform card)
        {
            var row = new GameObject("Columns", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
            row.transform.SetParent(card, false);
            var layout = row.GetComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(8, 8, 0, 0);
            layout.spacing = 6f;
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = true;
            SetPreferredHeight(row, 18f);

            AddHeaderCell(row.transform, "#", 52f, TextAlignmentOptions.MidlineLeft);
            AddHeaderCell(row.transform, "PILOT", 0f, TextAlignmentOptions.MidlineLeft);
            AddHeaderCell(row.transform, "SCORE", 88f, TextAlignmentOptions.MidlineRight);
            AddHeaderCell(row.transform, "K", 64f, TextAlignmentOptions.MidlineRight);
            AddHeaderCell(row.transform, "G", 72f, TextAlignmentOptions.MidlineRight);
            AddHeaderCell(row.transform, "T", 64f, TextAlignmentOptions.MidlineRight);
        }

        /// <summary>One column caption. Width 0 means the flexible pilot column.</summary>
        static void AddHeaderCell(Transform parent, string text, float width, TextAlignmentOptions align)
        {
            var tmp = CreateText(parent, text, 12f, FontStyles.Bold, align);
            tmp.text = text;
            tmp.color = IceCaption;
            tmp.characterSpacing = 1.4f;
            var cell = tmp.gameObject.AddComponent<LayoutElement>();
            if (width <= 0f)
            {
                cell.flexibleWidth = 1f;
                cell.minWidth = 80f;
            }
            else
            {
                cell.preferredWidth = width;
                cell.minWidth = width;
            }
        }

        /// <summary>Scroll view that eats the leftover card height under the column captions.</summary>
        void BuildScroll(Transform card)
        {
            var scrollGo = new GameObject("Scroll", typeof(RectTransform), typeof(ScrollRect), typeof(LayoutElement));
            scrollGo.transform.SetParent(card, false);
            var scrollLe = scrollGo.GetComponent<LayoutElement>();
            scrollLe.flexibleHeight = 1f;
            scrollLe.minHeight = 180f;
            scrollLe.preferredHeight = 360f;

            _scroll = scrollGo.GetComponent<ScrollRect>();
            _scroll.horizontal = false;
            _scroll.vertical = true;
            _scroll.movementType = ScrollRect.MovementType.Clamped;
            _scroll.scrollSensitivity = 28f;

            var viewportGo = new GameObject("Viewport", typeof(RectTransform), typeof(Image), typeof(RectMask2D));
            viewportGo.transform.SetParent(scrollGo.transform, false);
            Stretch(viewportGo.GetComponent<RectTransform>());
            var vpImage = viewportGo.GetComponent<Image>();
            vpImage.color = new Color(0f, 0f, 0f, 0.01f);
            vpImage.raycastTarget = true;

            var contentGo = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            contentGo.transform.SetParent(viewportGo.transform, false);
            _scrollContent = contentGo.GetComponent<RectTransform>();
            _scrollContent.anchorMin = new Vector2(0f, 1f);
            _scrollContent.anchorMax = new Vector2(1f, 1f);
            _scrollContent.pivot = new Vector2(0.5f, 1f);
            _scrollContent.anchoredPosition = Vector2.zero;
            _scrollContent.sizeDelta = new Vector2(0f, 0f);

            var contentLayout = contentGo.GetComponent<VerticalLayoutGroup>();
            contentLayout.spacing = 3f;
            contentLayout.padding = new RectOffset(0, 0, 2, 8);
            contentLayout.childAlignment = TextAnchor.UpperCenter;
            contentLayout.childControlWidth = true;
            contentLayout.childControlHeight = true;
            contentLayout.childForceExpandWidth = true;
            contentLayout.childForceExpandHeight = false;

            var fitter = contentGo.GetComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            _scroll.viewport = viewportGo.GetComponent<RectTransform>();
            _scroll.content = _scrollContent;

            _boardStatus = CreateText(viewportGo.transform, "Status", 16f, FontStyles.Bold, TextAlignmentOptions.Center);
            Stretch(_boardStatus.rectTransform);
            _boardStatus.text = "READING MATCH TELEMETRY";
            _boardStatus.color = IceCaption;
            _boardStatus.raycastTarget = false;
        }

        /// <summary>Bottom call to action. Leaves the match; it does not dismiss into spectate.</summary>
        void BuildMenuButton(Transform card)
        {
            var buttonGo = new GameObject("ReturnButton", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
            buttonGo.transform.SetParent(card, false);
            SetPreferredHeight(buttonGo, 48f);

            var image = buttonGo.GetComponent<Image>();
            image.color = new Color(0.10f, 0.14f, 0.20f, 0.95f);
            continueButton = buttonGo.GetComponent<Button>();
            var colors = continueButton.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(0.82f, 0.9f, 1f, 1f);
            colors.pressedColor = new Color(0.65f, 0.74f, 0.86f, 1f);
            colors.selectedColor = colors.highlightedColor;
            colors.disabledColor = new Color(0.5f, 0.5f, 0.5f, 0.6f);
            continueButton.colors = colors;
            continueButton.onClick.AddListener(OnReturnToMenuClicked);

            var label = CreateText(buttonGo.transform, "Label", 20f, FontStyles.Bold, TextAlignmentOptions.Center);
            Stretch(label.rectTransform);
            label.text = "RETURN TO MAIN MENU";
            label.color = IceCaption;
            label.characterSpacing = 1.8f;
        }

        /// <summary>Solid-color UI image. Raycasts off unless a caller turns them on.</summary>
        static Image CreateImage(Transform parent, string name, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var image = go.GetComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            return image;
        }

        /// <summary>TMP label using the HUD Rajdhani face when that asset is in Resources.</summary>
        static TextMeshProUGUI CreateText(
            Transform parent,
            string name,
            float fontSize,
            FontStyles style,
            TextAlignmentOptions align)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.alignment = align;
            tmp.fontSize = fontSize;
            tmp.fontStyle = style;
            tmp.color = BodyText;
            tmp.enableWordWrapping = false;
            tmp.overflowMode = TextOverflowModes.Ellipsis;
            tmp.raycastTarget = false;
            TMP_FontAsset font = Resources.Load<TMP_FontAsset>("Rajdhani-SemiBold SDF");
            if (font != null)
                tmp.font = font;
            else if (TMP_Settings.defaultFontAsset != null)
                tmp.font = TMP_Settings.defaultFontAsset;
            return tmp;
        }

        /// <summary>Pins a rect to its parent. Used by the backdrop, scroll viewport, and button label.</summary>
        static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        /// <summary>Tells the card's vertical layout how tall this block wants to be.</summary>
        static void SetPreferredHeight(GameObject go, float height)
        {
            var le = go.GetComponent<LayoutElement>();
            if (le == null)
                le = go.AddComponent<LayoutElement>();
            le.preferredHeight = height;
            le.minHeight = height;
        }

        /// <summary>Keeps a long roster name from blowing the pilot column.</summary>
        static string TruncateName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "Pilot";
            if (name.Length > 22)
                return name.Substring(0, 22);
            return name;
        }

        /// <summary>Thousands separators so a 4-digit score scans as a score, not a blob.</summary>
        static string FormatInt(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

        /// <summary>Clock chip. Under a minute stays "42s"; longer matches read "12:05".</summary>
        static string FormatMatchTime(float seconds)
        {
            int total = Mathf.Max(0, Mathf.RoundToInt(seconds));
            int minutes = total / 60;
            int secs = total % 60;
            if (minutes <= 0)
                return secs + "s";
            return minutes + ":" + secs.ToString("00");
        }
    }
}
