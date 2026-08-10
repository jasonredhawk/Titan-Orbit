using TitanOrbit.Core;
using TMPro;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// [HYBRID] Writes live match stats into the Join Team panel TMP labels
    /// (Title / Stats / Players under each TeamAPanel…TeamEPanel).
    /// <para>
    /// The SampleScene panels ship with placeholder text ("Team A (0/20)", "Home Lv.0 | …",
    /// "No players"). <see cref="NceGameFlowController"/> only used to show/hide those panels —
    /// this binder is the missing wire-up. Data comes from <see cref="EcsGameBridge.FillJoinTeamSlotStats"/>
    /// (roster singleton + quarantine-safe planet cache + gated ship list).
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

            /// <summary>StatsBar/Stats — "Home Lv.1 | Gems 0/100 | Planets 1".</summary>
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
                ApplySlot((TeamId)(i + 1), in s_Panels[i], in s_StatsScratch[i]);
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
                    continue;
                }

                // --- Skip rebuild when the same panel root is still wired ---
                if (s_CachedPanelRoots[i] == root && s_Panels[i].HasAny)
                    continue;

                s_CachedPanelRoots[i] = root;
                s_Panels[i] = ResolvePanelTexts(root.transform);
            }
        }

        /// <summary>Finds the three Join Team TMP fields under one Team*Panel.</summary>
        static PanelTexts ResolvePanelTexts(Transform panelRoot)
        {
            var texts = new PanelTexts();
            if (panelRoot == null)
                return texts;

            // --- Preferred SampleScene paths ---
            texts.Title = FindTmp(panelRoot, "Content/TitleBar/Title");
            texts.Stats = FindTmp(panelRoot, "Content/StatsBar/Stats");
            texts.Players = FindTmp(panelRoot, "Content/PlayersQuota/Players");

            // --- Fallbacks if hierarchy was renamed lightly ---
            if (texts.Title == null)
                texts.Title = FindTmpByName(panelRoot, "Title");
            if (texts.Stats == null)
                texts.Stats = FindTmpByName(panelRoot, "Stats");
            if (texts.Players == null)
                texts.Players = FindTmpByName(panelRoot, "Players");

            texts.HasAny = texts.Title != null || texts.Stats != null || texts.Players != null;
            return texts;
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
        static void ApplySlot(TeamId team, in PanelTexts texts, in EcsGameBridge.JoinTeamSlotStats stats)
        {
            if (!texts.HasAny)
                return;

            // --- Title: "Team A (2/20)" ---
            if (texts.Title != null)
            {
                texts.Title.text = team.ToDisplayName() + " (" + stats.PlayerCount + "/" +
                                   stats.MaxPlayers + ")";
            }

            // --- Stats: home level, gem bar, owned planet count ---
            // Matches the scene placeholder format so layout/font stay familiar.
            if (texts.Stats != null)
            {
                int gems = Mathf.RoundToInt(stats.HomeGems);
                int maxGems = Mathf.RoundToInt(stats.HomeMaxGems);
                texts.Stats.text = "Home Lv." + stats.HomeLevel +
                                   " | Gems " + gems + "/" + maxGems +
                                   " | Planets " + stats.PlanetCount;
            }

            // --- Players list ---
            if (texts.Players != null)
                texts.Players.text = string.IsNullOrEmpty(stats.PlayersLabel)
                    ? "No players"
                    : stats.PlayersLabel;
        }
    }
}
