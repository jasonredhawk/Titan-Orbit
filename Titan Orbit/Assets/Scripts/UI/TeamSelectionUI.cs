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
            int a = TeamManager.Instance.TeamACount;
            int b = TeamManager.Instance.TeamBCount;
            int c = TeamManager.Instance.TeamCCount;
            int minCount = Mathf.Min(a, b, c);

            RefreshPanel(TeamManager.Team.TeamA, teamAPanel, a, max, minCount);
            RefreshPanel(TeamManager.Team.TeamB, teamBPanel, b, max, minCount);
            RefreshPanel(TeamManager.Team.TeamC, teamCPanel, c, max, minCount);
        }

        private void RefreshPanel(TeamManager.Team team, TeamPanelRefs refs, int count, int max, int minCount)
        {
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
