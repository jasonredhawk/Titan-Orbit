using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Core;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Special planet type that serves as a team's home base
    /// Cannot be neutral, has level system, and elimination condition
    /// </summary>
    public class HomePlanet : Planet
    {
        [Header("Home Planet Settings")]
        [SerializeField] private float[] gemThresholdsPerLevel = { 0f, 1000f, 2500f, 5000f, 10000f }; // Level 1-4
        [SerializeField] private int[] maxShipLevelPerPlanetLevel = { 0, 3, 4, 5, 6 }; // Level 1-4 supports ship levels

        private NetworkVariable<float> currentGems = new NetworkVariable<float>(0f);
        private NetworkVariable<int> homePlanetLevel = new NetworkVariable<int>(1);
        private NetworkVariable<TeamManager.Team> assignedTeam = new NetworkVariable<TeamManager.Team>(TeamManager.Team.None);

        public float CurrentGems => currentGems.Value;
        public int HomePlanetLevel => homePlanetLevel.Value;
        public TeamManager.Team AssignedTeam => assignedTeam.Value;
        public int MaxShipLevel => GetMaxShipLevelForPlanetLevel(homePlanetLevel.Value);

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            
            if (IsServer)
            {
                homePlanetLevel.Value = 1;
                currentGems.Value = 0f;
            }

            homePlanetLevel.OnValueChanged += OnLevelChanged;
        }

        public override void OnNetworkDespawn()
        {
            homePlanetLevel.OnValueChanged -= OnLevelChanged;
            base.OnNetworkDespawn();
        }

        [ServerRpc(RequireOwnership = false)]
        public void DepositGemsServerRpc(float amount, TeamManager.Team depositingTeam)
        {
            // Only allow team members to deposit gems
            if (assignedTeam.Value == TeamManager.Team.None)
            {
                // First deposit assigns the team
                assignedTeam.Value = depositingTeam;
            }
            else if (assignedTeam.Value != depositingTeam)
            {
                // Wrong team - don't accept gems
                return;
            }

            currentGems.Value += amount;

            // Check if level up is possible
            CheckLevelUp();
        }

        private void CheckLevelUp()
        {
            if (!IsServer) return;

            int currentLevel = homePlanetLevel.Value;
            
            // Check if we can level up (max level is 4)
            if (currentLevel < 4 && currentLevel < gemThresholdsPerLevel.Length)
            {
                float thresholdForNextLevel = gemThresholdsPerLevel[currentLevel];
                
                if (currentGems.Value >= thresholdForNextLevel)
                {
                    LevelUpServerRpc();
                }
            }
        }

        [ServerRpc(RequireOwnership = false)]
        private void LevelUpServerRpc()
        {
            if (homePlanetLevel.Value >= 4) return; // Max level

            homePlanetLevel.Value++;
            LevelUpClientRpc(homePlanetLevel.Value);
        }

        [ClientRpc]
        private void LevelUpClientRpc(int newLevel)
        {
            Debug.Log($"Home Planet leveled up to level {newLevel}! Max ship level is now {GetMaxShipLevelForPlanetLevel(newLevel)}");
        }

        private void OnLevelChanged(int previousLevel, int newLevel)
        {
            // Handle level up effects
            Debug.Log($"Home Planet level changed from {previousLevel} to {newLevel}");
        }

        public int GetMaxShipLevelForPlanetLevel(int planetLevel)
        {
            if (planetLevel >= 1 && planetLevel < maxShipLevelPerPlanetLevel.Length)
            {
                return maxShipLevelPerPlanetLevel[planetLevel];
            }
            return 3; // Default
        }

        public float GetGemsNeededForNextLevel()
        {
            int currentLevel = homePlanetLevel.Value;
            if (currentLevel >= 4) return 0f; // Max level

            if (currentLevel < gemThresholdsPerLevel.Length)
            {
                return gemThresholdsPerLevel[currentLevel] - currentGems.Value;
            }
            return 0f;
        }

        public override bool CanBeCapturedBy(TeamManager.Team team)
        {
            // Home planets can be captured, but if captured, the team loses
            return assignedTeam.Value != TeamManager.Team.None && assignedTeam.Value != team;
        }

        [ServerRpc(RequireOwnership = false)]
        public void OnHomePlanetCapturedServerRpc(TeamManager.Team capturingTeam)
        {
            // This is called when home planet is captured
            // The team that owned this planet is eliminated
            if (assignedTeam.Value != TeamManager.Team.None)
            {
                EliminateTeamServerRpc(assignedTeam.Value);
            }
        }

        [ServerRpc(RequireOwnership = false)]
        private void EliminateTeamServerRpc(TeamManager.Team eliminatedTeam)
        {
            // Notify all clients that a team was eliminated
            EliminateTeamClientRpc(eliminatedTeam);
            
            // Check win conditions
            CheckWinConditions();
        }

        [ClientRpc]
        private void EliminateTeamClientRpc(TeamManager.Team eliminatedTeam)
        {
            Debug.Log($"Team {eliminatedTeam} has been eliminated!");
        }

        private void CheckWinConditions()
        {
            // This would be handled by GameManager or a separate WinConditionManager
            // For now, just log
            Debug.Log("Checking win conditions...");
        }
    }
}
