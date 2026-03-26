using UnityEngine;
using UnityEngine.UI;
using TMPro;
using TitanOrbit.Core;
using TitanOrbit.Systems;
using TitanOrbit.Entities;
using TitanOrbit.Networking;
using System.Collections.Generic;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Three team panels: players with scores, team stats (home level, gems, planets), and Join button with balance logic.
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
            int active = Mathf.Clamp(TeamManager.Instance.ActiveTeamCount, 2, 5);

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

        private static void SetPanelColumnActive(TeamPanelRefs refs, bool on)
        {
            if (refs == null || refs.title == null) return;
            Transform col = refs.title.transform.parent;
            if (col != null)
                col.gameObject.SetActive(on);
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
            bool canJoin = count < max && count <= minCount + 1;
            if (refs.joinButton != null)
            {
                refs.joinButton.interactable = canJoin;
                refs.joinButton.onClick.RemoveAllListeners();
                refs.joinButton.onClick.AddListener(() => OnJoinTeam(team));
            }
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

        private void OnJoinTeam(TeamManager.Team team)
        {
            if (TeamManager.Instance != null)
                TeamManager.Instance.RequestTeamServerRpc(team);
        }
    }
}
