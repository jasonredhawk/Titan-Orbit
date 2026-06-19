using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Unity.Netcode;
using TitanOrbit.Core;
using TitanOrbit.Systems;
using TitanOrbit.Entities;
using TitanOrbit.Networking;
using System.Collections.Generic;

namespace TitanOrbit.UI
{
    /// <summary>
    /// One column per team (2–5): players with scores, team stats (home level, gems, planets), and Join with balance logic.
    /// </summary>
    public class TeamSelectionUI : MonoBehaviour
    {
        [System.Serializable]
        public class TeamPanelRefs
        {
            public TextMeshProUGUI title;
            public TextMeshProUGUI playersText;
            public TextMeshProUGUI statsText;
            public Button joinButton;
        }

        [Header("Team Panels")]
        [SerializeField] private TeamPanelRefs teamAPanel;
        [SerializeField] private TeamPanelRefs teamBPanel;
        [SerializeField] private TeamPanelRefs teamCPanel;
        [SerializeField] private TeamPanelRefs teamDPanel;
        [SerializeField] private TeamPanelRefs teamEPanel;

        [Header("Refresh")]
        [SerializeField] private float refreshInterval = 0.5f;
        private float nextRefreshTime;

        private void Start()
        {
            WireJoinOnce(TeamManager.Team.TeamA, teamAPanel);
            WireJoinOnce(TeamManager.Team.TeamB, teamBPanel);
            WireJoinOnce(TeamManager.Team.TeamC, teamCPanel);
            WireJoinOnce(TeamManager.Team.TeamD, teamDPanel);
            WireJoinOnce(TeamManager.Team.TeamE, teamEPanel);
        }

        private static void WireJoinOnce(TeamManager.Team team, TeamPanelRefs refs)
        {
            if (refs == null || refs.joinButton == null) return;
            refs.joinButton.onClick.RemoveAllListeners();
            TeamManager.Team capture = team;
            refs.joinButton.onClick.AddListener(() => OnJoinTeamStatic(capture));
        }

        private static void OnJoinTeamStatic(TeamManager.Team team)
        {
            NetworkGameManager.RequestTeamFromLocalPlayer(team);
        }

        private void Update()
        {
            if (Time.realtimeSinceStartup < nextRefreshTime) return;
            nextRefreshTime = Time.realtimeSinceStartup + refreshInterval;
            RefreshAllPanels();
        }

        private void RefreshAllPanels()
        {
            if (TeamManager.Instance == null) return;
            int max = TeamManager.Instance.MaxPlayersPerTeam;
            int active = TeamManager.Instance.GetEffectiveTeamCountForUI();

            int minCount = int.MaxValue;
            for (int i = 0; i < active; i++)
            {
                TeamManager.Team t = (TeamManager.Team)(i + 1);
                minCount = Mathf.Min(minCount, TeamManager.Instance.GetTeamPlayerCount(t));
            }

            RefreshPanel(TeamManager.Team.TeamA, teamAPanel, 1, active, max, minCount);
            RefreshPanel(TeamManager.Team.TeamB, teamBPanel, 2, active, max, minCount);
            RefreshPanel(TeamManager.Team.TeamC, teamCPanel, 3, active, max, minCount);
            RefreshPanel(TeamManager.Team.TeamD, teamDPanel, 4, active, max, minCount);
            RefreshPanel(TeamManager.Team.TeamE, teamEPanel, 5, active, max, minCount);
        }

        /// <summary>Title lives under Content/TitleBar; join is under Content. Toggle the whole column (TeamXPanel root), not just TitleBar.</summary>
        private static void SetPanelColumnActive(TeamPanelRefs refs, bool on)
        {
            if (refs == null) return;
            Transform root = null;
            if (refs.joinButton != null)
                root = refs.joinButton.transform.parent != null ? refs.joinButton.transform.parent.parent : null;
            if (root == null && refs.title != null)
                root = refs.title.transform.parent != null && refs.title.transform.parent.parent != null
                    ? refs.title.transform.parent.parent.parent
                    : null;
            if (root != null)
                root.gameObject.SetActive(on);
            else if (refs.title != null && refs.title.transform.parent != null)
                refs.title.transform.parent.gameObject.SetActive(on);
        }

        private void RefreshPanel(TeamManager.Team team, TeamPanelRefs refs, int teamOrdinal, int activeTeams, int max, int minCount)
        {
            bool inMatch = teamOrdinal <= activeTeams;
            if (refs == null)
                return;
            SetPanelColumnActive(refs, inMatch);
            if (!inMatch) return;

            int count = TeamManager.Instance.GetTeamPlayerCount(team);
            if (refs.title != null)
            {
                refs.title.text = "Team " + TeamLabel(team) + " (" + count + "/" + max + ")";
                refs.title.color = TeamManager.GetTeamColor(team);
            }
            if (refs.playersText != null)
            {
                string playersStr = GetPlayersListForTeam(team);
                refs.playersText.text = string.IsNullOrEmpty(playersStr) ? "No players" : playersStr;
            }
            if (refs.statsText != null)
                refs.statsText.text = GetTeamStatsString(team);
            // Until the local player has a team, any non-full team is joinable. Balance rule applies only when switching teams.
            // (Client roster counts use replicated NetworkVariables; playerTeams lists are server-only.)
            bool localHasNoTeam = true;
            if (NetworkManager.Singleton != null && TeamManager.Instance != null)
                localHasNoTeam = TeamManager.Instance.GetPlayerTeam(NetworkManager.Singleton.LocalClientId) == TeamManager.Team.None;
            bool teamEliminated = TeamManager.Instance != null && TeamManager.Instance.IsTeamEliminated(team);
            bool canJoin = !teamEliminated && (localHasNoTeam
                ? count < max
                : count < max && count <= minCount + 1);
            if (refs.joinButton != null)
                refs.joinButton.interactable = canJoin;
            if (refs.statsText != null && teamEliminated)
                refs.statsText.text = "Eliminated";
        }

        private static string TeamLabel(TeamManager.Team team)
        {
            switch (team)
            {
                case TeamManager.Team.TeamA: return "A";
                case TeamManager.Team.TeamB: return "B";
                case TeamManager.Team.TeamC: return "C";
                case TeamManager.Team.TeamD: return "D";
                case TeamManager.Team.TeamE: return "E";
                default: return "?";
            }
        }

        private string GetPlayersListForTeam(TeamManager.Team team)
        {
            if (ScoreSystem.Instance == null || ScoreSystem.Instance.Entries == null) return "";
            var entries = new List<ScoreEntry>();
            foreach (var e in ScoreSystem.Instance.Entries)
                if (e.Team == team) entries.Add(e);
            entries.Sort((x, y) => y.Score.CompareTo(x.Score));
            var lines = new List<string>();
            foreach (var e in entries)
            {
                string name = PlayerDisplayNames.GetDisplayName(e.OwnerClientId, e.IsAI);
                lines.Add(name + "  " + e.Score);
            }
            return string.Join("\n", lines);
        }

        private string GetTeamStatsString(TeamManager.Team team)
        {
            int homeLevel = 0;
            float homeGems = 0f;
            float homeMaxGems = 0f;
            int planetsOwned = 0;

            var homePlanets = HomePlanet.AllHomePlanets;
            foreach (var hp in homePlanets)
            {
                if (hp.AssignedTeam != team) continue;
                homeLevel = hp.HomePlanetLevel;
                homeGems = hp.CurrentGems;
                homeMaxGems = hp.MaxGems;
                break;
            }

            var planets = Planet.AllPlanets;
            foreach (var p in planets)
            {
                if (p.TeamOwnership == team) planetsOwned++;
            }

            return "Home Lv." + homeLevel + " | Gems " + homeGems.ToString("F0") + "/" + homeMaxGems.ToString("F0") + " | Planets " + planetsOwned;
        }

    }
}
