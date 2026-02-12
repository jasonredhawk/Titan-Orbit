using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Core;
using TitanOrbit.Entities;

namespace TitanOrbit.Core
{
    /// <summary>
    /// Manages match lifecycle including start, end, and win conditions
    /// </summary>
    public class MatchManager : NetworkBehaviour
    {
        public static MatchManager Instance { get; private set; }

        [Header("Match Settings")]
        [SerializeField] private float matchStartDelay = 5f;
        [SerializeField] private float matchDuration = 3600f; // 60 minutes

        private NetworkVariable<bool> matchStarted = new NetworkVariable<bool>(false);
        private NetworkVariable<float> matchTimer = new NetworkVariable<float>(0f);
        private NetworkVariable<TeamManager.Team> winningTeam = new NetworkVariable<TeamManager.Team>(TeamManager.Team.None);

        public bool MatchStarted => matchStarted.Value;
        public float MatchTimer => matchTimer.Value;
        public TeamManager.Team WinningTeam => winningTeam.Value;

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
        }

        public override void OnNetworkSpawn()
        {
            if (IsServer)
            {
                matchStarted.Value = false;
                matchTimer.Value = 0f;
                winningTeam.Value = TeamManager.Team.None;

                // Start match after delay
                Invoke(nameof(StartMatchServerRpc), matchStartDelay);
            }
        }

        [ServerRpc(RequireOwnership = false)]
        private void StartMatchServerRpc()
        {
            matchStarted.Value = true;
            matchTimer.Value = 0f;
            StartMatchClientRpc();
        }

        [ClientRpc]
        private void StartMatchClientRpc()
        {
            Debug.Log("Match started!");
        }

        private void Update()
        {
            if (IsServer && matchStarted.Value)
            {
                matchTimer.Value += Time.deltaTime;

                // Check win conditions
                CheckWinConditions();

                // Check match duration
                if (matchTimer.Value >= matchDuration)
                {
                    EndMatch(TeamManager.Team.None); // Draw
                }
            }
        }

        private void CheckWinConditions()
        {
            // Check if any team has captured all planets
            // This is handled by CaptureSystem, but we check here too
            Planet[] allPlanets = FindObjectsOfType<Planet>();
            HomePlanet[] allHomePlanets = FindObjectsOfType<HomePlanet>();

            foreach (TeamManager.Team team in System.Enum.GetValues(typeof(TeamManager.Team)))
            {
                if (team == TeamManager.Team.None) continue;

                bool ownsAllPlanets = true;
                bool ownsAllHomePlanets = true;

                foreach (Planet planet in allPlanets)
                {
                    if (planet.TeamOwnership != team)
                    {
                        ownsAllPlanets = false;
                        break;
                    }
                }

                foreach (HomePlanet homePlanet in allHomePlanets)
                {
                    if (homePlanet.AssignedTeam != team)
                    {
                        ownsAllHomePlanets = false;
                        break;
                    }
                }

                if (ownsAllPlanets && ownsAllHomePlanets)
                {
                    EndMatch(team);
                    return;
                }
            }
        }

        [ServerRpc(RequireOwnership = false)]
        public void EndMatchServerRpc(TeamManager.Team team)
        {
            EndMatch(team);
        }

        private void EndMatch(TeamManager.Team team)
        {
            if (!IsServer) return;

            winningTeam.Value = team;
            matchStarted.Value = false;
            EndMatchClientRpc(team);
        }

        [ClientRpc]
        private void EndMatchClientRpc(TeamManager.Team team)
        {
            Debug.Log($"Match ended! Winning team: {team}");
            // Show win/loss screen
            TitanOrbit.UI.WinLossScreen winLossScreen = FindObjectOfType<TitanOrbit.UI.WinLossScreen>();
            if (winLossScreen != null)
            {
                TeamManager.Team playerTeam = TeamManager.Team.None; // Get player's team
                winLossScreen.ShowWinScreen(team, playerTeam);
            }
        }
    }
}
