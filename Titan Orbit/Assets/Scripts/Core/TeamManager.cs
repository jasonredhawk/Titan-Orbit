using UnityEngine;
using Unity.Netcode;
using System.Collections.Generic;
using TitanOrbit.Networking;
using TitanOrbit.Entities;

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

        public enum Team
        {
            None = 0,
            TeamA = 1,
            TeamB = 2,
            TeamC = 3,
            TeamD = 4,
            TeamE = 5
        }

        /// <summary>Display color for UI, rings, minimap. Neutral = white.</summary>
        public static Color GetTeamColor(Team team)
        {
            switch (team)
            {
                case Team.TeamA: return new Color(0.9f, 0.25f, 0.25f);
                case Team.TeamB: return new Color(0.25f, 0.4f, 0.9f);
                case Team.TeamC: return new Color(0.2f, 0.7f, 0.28f);
                case Team.TeamD: return new Color(0.95f, 0.55f, 0.12f);
                case Team.TeamE: return new Color(0.65f, 0.25f, 0.85f);
                default: return Color.white;
            }
        }

        private Dictionary<ulong, Team> playerTeams = new Dictionary<ulong, Team>();
        private Dictionary<Team, List<ulong>> teamPlayers = new Dictionary<Team, List<ulong>>();

        private NetworkVariable<int> networkTeamACount = new NetworkVariable<int>(0);
        private NetworkVariable<int> networkTeamBCount = new NetworkVariable<int>(0);
        private NetworkVariable<int> networkTeamCCount = new NetworkVariable<int>(0);
        private NetworkVariable<int> networkTeamDCount = new NetworkVariable<int>(0);
        private NetworkVariable<int> networkTeamECount = new NetworkVariable<int>(0);

        /// <summary>How many teams are in the current match (2–5). Set by MapGenerator when home worlds spawn.</summary>
        private NetworkVariable<int> activeTeamCount = new NetworkVariable<int>(3);

        /// <summary>Server: applied in OnNetworkSpawn if <see cref="SetActiveTeamCountFromServer"/> ran before spawn.</summary>
        private int pendingActiveTeamCount = -1;

        public int MaxPlayersPerTeam => maxPlayersPerTeam;
        /// <summary>Teams in the current match (2–5). Mirrors the number of home planets.</summary>
        public int NumberOfTeams => activeTeamCount.Value;
        public int ActiveTeamCount => activeTeamCount.Value;

        /// <summary>
        /// For menus/HUD: how many teams exist in this match. Uses spawned home worlds when available (authoritative for map size);
        /// otherwise falls back to <see cref="ActiveTeamCount"/> (e.g. lobby before map spawn).
        /// </summary>
        public int GetEffectiveTeamCountForUI()
        {
            int n = HomePlanet.AllHomePlanets != null ? HomePlanet.AllHomePlanets.Count : 0;
            if (n >= 2 && n <= 5)
                return n;
            return Mathf.Clamp(activeTeamCount.Value, 2, 5);
        }

        public int TeamACount => networkTeamACount.Value;
        public int TeamBCount => networkTeamBCount.Value;
        public int TeamCCount => networkTeamCCount.Value;
        public int TeamDCount => networkTeamDCount.Value;
        public int TeamECount => networkTeamECount.Value;

        public bool IsTeamInCurrentMatch(Team team)
        {
            if (team == Team.None) return false;
            int ord = (int)team;
            return ord >= 1 && ord <= activeTeamCount.Value;
        }

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

            teamPlayers[Team.TeamA] = new List<ulong>();
            teamPlayers[Team.TeamB] = new List<ulong>();
            teamPlayers[Team.TeamC] = new List<ulong>();
            teamPlayers[Team.TeamD] = new List<ulong>();
            teamPlayers[Team.TeamE] = new List<ulong>();
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            if (IsServer && pendingActiveTeamCount >= 2)
            {
                activeTeamCount.Value = Mathf.Clamp(pendingActiveTeamCount, 2, 5);
                pendingActiveTeamCount = -1;
            }
        }

        /// <summary>Server only: called after home planets are generated (2–5 teams).</summary>
        public void SetActiveTeamCountFromServer(int count)
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return;
            int c = Mathf.Clamp(count, 2, 5);
            if (IsSpawned)
                activeTeamCount.Value = c;
            else
                pendingActiveTeamCount = c;
        }

        public Team AssignPlayerToTeam(ulong clientId)
        {
            if (!IsServer) return Team.None;

            if (playerTeams.ContainsKey(clientId))
            {
                return playerTeams[clientId];
            }

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
            networkTeamDCount.Value = teamPlayers[Team.TeamD].Count;
            networkTeamECount.Value = teamPlayers[Team.TeamE].Count;
        }

        public bool IsTeamOpen(Team team)
        {
            if (team == Team.None || !IsTeamInCurrentMatch(team)) return false;
            return GetTeamPlayerCount(team) < maxPlayersPerTeam;
        }

        public bool TryReassignPlayer(ulong clientId, Team newTeam)
        {
            if (!IsServer || newTeam == Team.None || !IsTeamInCurrentMatch(newTeam)) return false;
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
            if (!IsServer || team == Team.None || !IsTeamInCurrentMatch(team)) return false;
            if (!IsTeamOpen(team)) return false;
            if (playerTeams.ContainsKey(clientId)) return false;
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
            int active = activeTeamCount.Value;
            if (active < 2) active = 3;

            Team leastPopulatedTeam = Team.TeamA;
            int minPlayers = teamPlayers[Team.TeamA].Count;

            for (int ord = 1; ord <= active; ord++)
            {
                Team team = (Team)ord;
                int playerCount = teamPlayers[team].Count;
                if (playerCount < minPlayers && playerCount < maxPlayersPerTeam)
                {
                    minPlayers = playerCount;
                    leastPopulatedTeam = team;
                }
            }

            if (minPlayers >= maxPlayersPerTeam)
            {
                return Team.None;
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
            int active = activeTeamCount.Value;
            if (active < 2) active = 3;
            for (int ord = 1; ord <= active; ord++)
            {
                Team t = (Team)ord;
                if (teamPlayers[t].Count < maxPlayersPerTeam)
                    return false;
            }
            return true;
        }
    }
}
