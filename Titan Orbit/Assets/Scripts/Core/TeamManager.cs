using UnityEngine;
using Unity.Netcode;
using System.Collections.Generic;
using System.Linq;

namespace TitanOrbit.Core
{
    /// <summary>
    /// Manages team assignment and team-related game logic
    /// </summary>
    public class TeamManager : NetworkBehaviour
    {
        public static TeamManager Instance { get; private set; }

        [Header("Team Settings")]
        [SerializeField] private int maxPlayersPerTeam = 20;
        [SerializeField] private int numberOfTeams = 3;

        public enum Team
        {
            None = 0,
            TeamA = 1,
            TeamB = 2,
            TeamC = 3
        }

        private Dictionary<ulong, Team> playerTeams = new Dictionary<ulong, Team>();
        private Dictionary<Team, List<ulong>> teamPlayers = new Dictionary<Team, List<ulong>>();

        public int MaxPlayersPerTeam => maxPlayersPerTeam;
        public int NumberOfTeams => numberOfTeams;

        private void Awake()
        {
            if (Instance == null)
            {
                Instance = this;
            }
            else
            {
                Destroy(gameObject);
            }

            // Initialize team lists
            teamPlayers[Team.TeamA] = new List<ulong>();
            teamPlayers[Team.TeamB] = new List<ulong>();
            teamPlayers[Team.TeamC] = new List<ulong>();
        }

        public Team AssignPlayerToTeam(ulong clientId)
        {
            if (!IsServer) return Team.None;

            // Check if player is already assigned
            if (playerTeams.ContainsKey(clientId))
            {
                return playerTeams[clientId];
            }

            // Find team with least players
            Team assignedTeam = GetTeamWithLeastPlayers();
            
            if (assignedTeam != Team.None)
            {
                playerTeams[clientId] = assignedTeam;
                teamPlayers[assignedTeam].Add(clientId);
            }

            return assignedTeam;
        }

        private Team GetTeamWithLeastPlayers()
        {
            Team leastPopulatedTeam = Team.TeamA;
            int minPlayers = teamPlayers[Team.TeamA].Count;

            foreach (Team team in System.Enum.GetValues(typeof(Team)))
            {
                if (team == Team.None) continue;

                int playerCount = teamPlayers[team].Count;
                if (playerCount < minPlayers && playerCount < maxPlayersPerTeam)
                {
                    minPlayers = playerCount;
                    leastPopulatedTeam = team;
                }
            }

            // Check if all teams are full
            if (minPlayers >= maxPlayersPerTeam)
            {
                return Team.None; // All teams full
            }

            return leastPopulatedTeam;
        }

        public Team GetPlayerTeam(ulong clientId)
        {
            if (playerTeams.ContainsKey(clientId))
            {
                return playerTeams[clientId];
            }
            return Team.None;
        }

        public List<ulong> GetTeamPlayers(Team team)
        {
            if (teamPlayers.ContainsKey(team))
            {
                return new List<ulong>(teamPlayers[team]);
            }
            return new List<ulong>();
        }

        public int GetTeamPlayerCount(Team team)
        {
            if (teamPlayers.ContainsKey(team))
            {
                return teamPlayers[team].Count;
            }
            return 0;
        }

        public void RemovePlayer(ulong clientId)
        {
            if (!IsServer) return;

            if (playerTeams.ContainsKey(clientId))
            {
                Team team = playerTeams[clientId];
                teamPlayers[team].Remove(clientId);
                playerTeams.Remove(clientId);
            }
        }

        public bool AreTeamsFull()
        {
            return teamPlayers[Team.TeamA].Count >= maxPlayersPerTeam &&
                   teamPlayers[Team.TeamB].Count >= maxPlayersPerTeam &&
                   teamPlayers[Team.TeamC].Count >= maxPlayersPerTeam;
        }
    }
}
