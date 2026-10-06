using System.Text;
using TitanOrbit.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.Game
{
    /// <summary>
    /// [HYBRID] Writes live match stats into the Join Team panel TMP labels
    /// (Title / Stats / Players under each TeamAPanel…TeamEPanel).
    /// <para>
    /// The SampleScene panels ship with placeholder text ("Team A (0/20)", "Home Lv.0 | …",
    /// "No players"). <see cref="NceGameFlowController"/> shows those panels; this binder
    /// paints them. Data comes from <see cref="EcsGameBridge.FillJoinTeamSlotStats"/>:
    /// roster singleton, planet ghosts (worlds, gem bars, crew), and ship match stats (score).
    /// </para>
    /// Client UI only — never drives sim or sends RPCs.
    /// </summary>
    public static class JoinTeamPanelStatsBinder
    {
        /// <summary>Cached TMP refs for one Team*Panel (resolved by hierarchy path).</summary>
        struct PanelTexts
        {
            /// <summary>TitleBar/Title — "Team A (2/20)".</summary>
            public TextMeshProUGUI Title;

            /// <summary>StatsBar/Stats — worlds, gem bars, crew, home level, team score.</summary>
            public TextMeshProUGUI Stats;

            /// <summary>PlayersQuota/Players — multi-line roster or "No players".</summary>
            public TextMeshProUGUI Players;

            /// <summary>True when at least one label was found under the panel.</summary>
            public bool HasAny;
        }

        /// <summary>Resolved labels for TeamA…TeamE (index matches <see cref="TeamId"/> − 1).</summary>
        static readonly PanelTexts[] s_Panels = new PanelTexts[5];

        /// <summary>Panel GameObjects that produced <see cref="s_Panels"/> — rebuild cache if they change.</summary>
        static readonly GameObject[] s_CachedPanelRoots = new GameObject[5];

        /// <summary>Scratch stats array reused every refresh (avoids GC on the Join Team screen).</summary>
        static readonly EcsGameBridge.JoinTeamSlotStats[] s_StatsScratch =
            new EcsGameBridge.JoinTeamSlotStats[5];

        /// <summary>
        /// Last numbers painted into each Stats label. Join Team refreshes every frame;
        /// rewriting TMP when nothing changed rebuilds the mesh for no reason.
        /// </summary>
        static readonly PaintedMapLine[] s_Painted = new PaintedMapLine[5];

        /// <summary>One builder for the stats line. Reused so a refresh does not allocate a builder.</summary>
        static readonly StringBuilder s_StatsLine = new StringBuilder(128);

        /// <summary>Last map line written to a team card. Compared so we skip identical TMP updates.</summary>
        struct PaintedMapLine
        {
            public int Worlds;
            public int Capturable;
            public int Gems;
            public int MaxGems;
            public int Crew;
            public int HomeLevel;
            public int Score;
            public bool ScoreKnown;
            public bool HasPaint;
        }

        /// <summary>
        /// Updates Title / Stats / Players on each active team panel from live ECS state.
        /// Call every frame while the Join Team screen is visible.
        /// </summary>
        /// <param name="teamPanels">TeamAPanel…TeamEPanel roots (null slots skipped).</param>
        /// <param name="activeTeamCount">How many teams this match rolled (2–5).</param>
        public static void Refresh(GameObject[] teamPanels, int activeTeamCount)
        {
            // --- Guard ---
            if (teamPanels == null || activeTeamCount <= 0)
                return;

            int slots = Mathf.Min(activeTeamCount, Mathf.Min(teamPanels.Length, 5));
            EnsurePanelCache(teamPanels, slots);

            // --- Gather once for all slots ---
            // [HYBRID] Bridge owns quarantine / GhostSpawnBacklog gates; we only paint TMP.
            EcsGameBridge.FillJoinTeamSlotStats(s_StatsScratch, slots);

            for (int i = 0; i < slots; i++)
                ApplySlot(i, (TeamId)(i + 1), in s_Panels[i], in s_StatsScratch[i]);
        }

        /// <summary>
        /// Resolves Title / Stats / Players TMP under each panel when the root GameObject changes.
        /// Hierarchy matches SampleScene: Content/TitleBar/Title, Content/StatsBar/Stats,
        /// Content/PlayersQuota/Players.
        /// </summary>
        static void EnsurePanelCache(GameObject[] teamPanels, int slots)
        {
            for (int i = 0; i < slots; i++)
            {
                GameObject root = teamPanels[i];
                if (root == null)
                {
                    s_CachedPanelRoots[i] = null;
                    s_Panels[i] = default;
                    s_Painted[i] = default;
                    continue;
                }

                // --- Skip rebuild when the same panel root is still wired ---
                if (s_CachedPanelRoots[i] == root && s_Panels[i].HasAny)
                    continue;

                s_CachedPanelRoots[i] = root;
                s_Panels[i] = ResolvePanelTexts(root.transform);
                s_Painted[i] = default;
            }
        }

        /// <summary>Finds the three Join Team TMP fields under one Team*Panel.</summary>
        static PanelTexts ResolvePanelTexts(Transform panelRoot)
        {
            var texts = new PanelTexts();
            if (panelRoot == null)
                return texts;

            // --- Preferred SampleScene paths ---
            // Players live under PlayersPanel (older comments said PlayersQuota).
            texts.Title = FindTmp(panelRoot, "Content/TitleBar/Title");
            texts.Stats = FindTmp(panelRoot, "Content/StatsBar/Stats");
            texts.Players = FindTmp(panelRoot, "Content/PlayersPanel/Players");
            if (texts.Players == null)
                texts.Players = FindTmp(panelRoot, "Content/PlayersQuota/Players");

            // --- Fallbacks if hierarchy was renamed lightly ---
            if (texts.Title == null)
                texts.Title = FindTmpByName(panelRoot, "Title");
            if (texts.Stats == null)
                texts.Stats = FindTmpByName(panelRoot, "Stats");
            if (texts.Players == null)
                texts.Players = FindTmpByName(panelRoot, "Players");

            texts.HasAny = texts.Title != null || texts.Stats != null || texts.Players != null;
            ConfigureStatsLine(texts.Stats);
            return texts;
        }

        /// <summary>
        /// Makes the stats bar tall enough for two telemetry lines.
        /// The scene bar is 18px (one placeholder line). Worlds, gems, crew, and score
        /// need a second line or they draw on top of the player list.
        /// </summary>
        static void ConfigureStatsLine(TextMeshProUGUI stats)
        {
            if (stats == null)
                return;

            // [UNITY] Centered, wrapping, so a narrow 5-team row can break instead of clipping.
            stats.enableWordWrapping = true;
            stats.overflowMode = TextOverflowModes.Overflow;
            stats.alignment = TextAlignmentOptions.Center;

            var bar = stats.transform.parent;
            if (bar != null && bar.TryGetComponent<LayoutElement>(out var layout) &&
                layout.preferredHeight < 52f)
            {
                // Two lines at 11px, plus room if a narrow card wraps to a third.
                layout.preferredHeight = 52f;
            }
        }

        /// <summary>TMP at a relative hierarchy path, or null.</summary>
        static TextMeshProUGUI FindTmp(Transform root, string relativePath)
        {
            var child = root.Find(relativePath);
            return child != null ? child.GetComponent<TextMeshProUGUI>() : null;
        }

        /// <summary>First descendant TMP whose GameObject name equals <paramref name="objectName"/>.</summary>
        static TextMeshProUGUI FindTmpByName(Transform root, string objectName)
        {
            var tmps = root.GetComponentsInChildren<TextMeshProUGUI>(true);
            for (int i = 0; i < tmps.Length; i++)
            {
                if (tmps[i] != null && tmps[i].gameObject.name == objectName)
                    return tmps[i];
            }

            return null;
        }

        /// <summary>Writes one slot's numbers into its cached TMP fields.</summary>
        static void ApplySlot(int slot, TeamId team, in PanelTexts texts, in EcsGameBridge.JoinTeamSlotStats stats)
        {
            if (!texts.HasAny)
                return;

            // --- Title: "Team A (2/20)" ---
            if (texts.Title != null)
            {
                texts.Title.text = team.ToDisplayName() + " (" + stats.PlayerCount + "/" +
                                   stats.MaxPlayers + ")";
            }

            // --- Stats: live map slice + team score ---
            // Worlds and gem bars are what is on the map right now. Score is the
            // leaderboard total (kills, deposited gems, delivered people).
            if (texts.Stats != null)
                PaintMapLine(slot, texts.Stats, in stats);

            // --- Players list ---
            if (texts.Players != null)
                texts.Players.text = string.IsNullOrEmpty(stats.PlayersLabel)
                    ? "No players"
                    : stats.PlayersLabel;
        }

        /// <summary>
        /// Paints worlds, gem banks, crew, home level, and team score.
        /// Skips the TMP write when those integers match the previous refresh.
        /// </summary>
        static void PaintMapLine(int slot, TextMeshProUGUI statsLabel, in EcsGameBridge.JoinTeamSlotStats stats)
        {
            int gems = Mathf.RoundToInt(stats.TeamGems);
            int maxGems = Mathf.RoundToInt(stats.TeamMaxGems);
            int capturable = stats.CapturableWorldCount;
            if (capturable < stats.PlanetCount)
                capturable = stats.PlanetCount;

            if (slot >= 0 && slot < s_Painted.Length)
            {
                PaintedMapLine prev = s_Painted[slot];
                if (prev.HasPaint &&
                    prev.Worlds == stats.PlanetCount &&
                    prev.Capturable == capturable &&
                    prev.Gems == gems &&
                    prev.MaxGems == maxGems &&
                    prev.Crew == stats.TeamPopulation &&
                    prev.HomeLevel == stats.HomeLevel &&
                    prev.Score == stats.TeamScore &&
                    prev.ScoreKnown == stats.TeamScoreKnown)
                {
                    return;
                }

                s_Painted[slot] = new PaintedMapLine
                {
                    Worlds = stats.PlanetCount,
                    Capturable = capturable,
                    Gems = gems,
                    MaxGems = maxGems,
                    Crew = stats.TeamPopulation,
                    HomeLevel = stats.HomeLevel,
                    Score = stats.TeamScore,
                    ScoreKnown = stats.TeamScoreKnown,
                    HasPaint = true,
                };
            }

            // --- Two lines, dark-cockpit captions ---
            // [TITAN-ORBIT] Ice labels, near-white numbers (the TMP color), gold score
            // so it matches the in-game leaderboard total.
            s_StatsLine.Clear();
            s_StatsLine.Append(stats.PlanetCount);
            if (capturable > 0)
            {
                s_StatsLine.Append('/');
                s_StatsLine.Append(capturable);
            }

            s_StatsLine.Append(" <color=#9EB6D8>WORLDS</color>  |  ");
            s_StatsLine.Append(gems);
            s_StatsLine.Append('/');
            s_StatsLine.Append(maxGems);
            s_StatsLine.Append(" <color=#9EB6D8>GEMS</color>\n");
            s_StatsLine.Append(stats.TeamPopulation);
            s_StatsLine.Append(" <color=#9EB6D8>CREW</color>  |  ");
            if (stats.HomeLevel > 0)
            {
                s_StatsLine.Append("<color=#9EB6D8>HOME LV.</color>");
                s_StatsLine.Append(stats.HomeLevel);
            }
            else
            {
                s_StatsLine.Append("<color=#9EB6D8>HOME</color> -");
            }

            s_StatsLine.Append("  |  <color=#F2DB8C>SCORE ");
            if (stats.TeamScoreKnown)
                s_StatsLine.Append(stats.TeamScore);
            else
                s_StatsLine.Append('-');
            s_StatsLine.Append("</color>");

            statsLabel.text = s_StatsLine.ToString();
        }
    }
}
