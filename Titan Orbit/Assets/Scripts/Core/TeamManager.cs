using UnityEngine;
using Unity.Netcode;
using System.Collections.Generic;
using System.Linq;
using TitanOrbit.Networking;

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

        /// <summary>Display color for UI, rings, minimap. Neutral = white; Team A/B/C = red/blue/green.</summary>
        public static Color GetTeamColor(Team team)
        {
            switch (team)
            {
                case Team.TeamA: return new Color(0.9f, 0.25f, 0.25f);
                case Team.TeamB: return new Color(0.25f, 0.4f, 0.9f);
                case Team.TeamC: return new Color(0.2f, 0.7f, 0.28f);
                default: return Color.white;
            }
        }

        private Dictionary<ulong, Team> playerTeams = new Dictionary<ulong, Team>();
        private Dictionary<Team, List<ulong>> teamPlayers = new Dictionary<Team, List<ulong>>();

        private NetworkVariable<int> networkTeamACount = new NetworkVariable<int>(0);
        private NetworkVariable<int> networkTeamBCount = new NetworkVariable<int>(0);
        private NetworkVariable<int> networkTeamCCount = new NetworkVariable<int>(0);

        public int MaxPlayersPerTeam => maxPlayersPerTeam;
        public int NumberOfTeams => numberOfTeams;

        public int TeamACount => networkTeamACount.Value;
        public int TeamBCount => networkTeamBCount.Value;
        public int TeamCCount => networkTeamCCount.Value;

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
                SyncTeamCountsToNetwork();
            }

            return assignedTeam;
        }

        private void SyncTeamCountsToNetwork()
        {
            if (!IsServer) return;
            networkTeamACount.Value = teamPlayers[Team.TeamA].Count;
            networkTeamBCount.Value = teamPlayers[Team.TeamB].Count;
            networkTeamCCount.Value = teamPlayers[Team.TeamC].Count;
        }

        public bool IsTeamOpen(Team team)
        {
            if (team == Team.None) return false;
            return GetTeamPlayerCount(team) < maxPlayersPerTeam;
        }

        public bool TryReassignPlayer(ulong clientId, Team newTeam)
        {
            if (!IsServer || newTeam == Team.None) return false;
            if (!IsTeamOpen(newTeam)) return false;
            if (!playerTeams.ContainsKey(clientId)) return false;
            Team currentTeam = playerTeams[clientId];
            if (currentTeam == newTeam) return true;
            teamPlayers[currentTeam].Remove(clientId);
            playerTeams[clientId] = newTeam;
            teamPlayers[newTeam].Add(clientId);
            SyncTeamCountsToNetwork();
            return true;
        }

        /// <summary>Server only: add a player to a team (first-time assignment, e.g. when they click Join from team selection).</summary>
        public bool AddPlayerToTeam(ulong clientId, Team team)
        {
            if (!IsServer || team == Team.None) return false;
            if (!IsTeamOpen(team)) return false;
            if (playerTeams.ContainsKey(clientId)) return false; // already on a team, use TryReassignPlayer
            playerTeams[clientId] = team;
            teamPlayers[team].Add(clientId);
            SyncTeamCountsToNetwork();
            return true;
        }

        [ServerRpc(RequireOwnership = false)]
        public void RequestTeamServerRpc(Team preferredTeam, ServerRpcParams rpcParams = default)
        {
            ulong clientId = rpcParams.Receive.SenderClientId;
            bool ok;
            Team actualTeam;
            if (GetPlayerTeam(clientId) == Team.None)
            {
                // First-time choice: add to preferred team and put ship in orbit
                ok = AddPlayerToTeam(clientId, preferredTeam);
                actualTeam = ok ? preferredTeam : Team.None;
            }
            else
            {
                ok = TryReassignPlayer(clientId, preferredTeam);
                actualTeam = GetPlayerTeam(clientId);
            }
            if (ok && actualTeam != Team.None)
            {
                var playerObj = NetworkManager.Singleton.SpawnManager.GetPlayerNetworkObject(clientId);
                if (playerObj != null)
                {
                    var ship = playerObj.GetComponent<TitanOrbit.Entities.Starship>();
                    if (ship != null)
                        ship.AssignTeamAndStartInOrbit(actualTeam);
                }
            }
            ResponseTeamClientRpc(clientId, actualTeam, ok);
        }

        [ClientRpc]
        public void ResponseTeamClientRpc(ulong clientId, Team assignedTeam, bool requestGranted)
        {
            if (NetworkManager.Singleton.LocalClientId == clientId && NetworkGameManager.Instance != null)
            {
                NetworkGameManager.Instance.OnTeamAssignmentResult(assignedTeam, requestGranted);
            }
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
                SyncTeamCountsToNetwork();
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
