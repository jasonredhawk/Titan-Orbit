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
        [Tooltip("Gems required to reach each level. Index 3 = gems to reach level 4, etc. Game starts at level 3. Max planet level 6.")]
        [SerializeField] private float[] gemThresholdsPerLevel = { 0f, 1000f, 2500f, 5000f, 10000f, 20000f, 40000f }; // Level 1-6
        [Tooltip("Max starship level allowed at each home planet level. Level 7 (MEGA) requires planet level 6 + full gems.")]
        [SerializeField] private int[] maxShipLevelPerPlanetLevel = { 0, 3, 4, 5, 6, 6, 6 }; // Planet levels 1-6 (ship 7 is special)

        private NetworkVariable<float> currentGems = new NetworkVariable<float>(0f);
        private NetworkVariable<int> homePlanetLevel = new NetworkVariable<int>(1);
        private NetworkVariable<TeamManager.Team> assignedTeam = new NetworkVariable<TeamManager.Team>(TeamManager.Team.None);

        public float CurrentGems => currentGems.Value;
        public int HomePlanetLevel => homePlanetLevel.Value;
        public TeamManager.Team AssignedTeam => assignedTeam.Value;
        public int MaxShipLevel => GetMaxShipLevelForPlanetLevel(homePlanetLevel.Value);

        /// <summary>
        /// Called by MapGenerator at spawn to set team and color. Call before NetworkObject.Spawn().
        /// </summary>
        public void InitForTeam(TeamManager.Team team)
        {
            assignedTeam.Value = team;
            SetInitialTeamOwnership(team); // Updates visual via Planet's team ownership
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            EnsureSolidColliderAndOrbitZone();
            if (IsServer)
            {
                homePlanetLevel.Value = 3; // Start at 3 so starships can level 1→2→3 without leveling planet
                currentGems.Value = 0f;
            }
            homePlanetLevel.OnValueChanged += OnLevelChanged;
        }

        /// <summary>Dark color so population text is readable on the white home planet ring.</summary>
        protected override Color GetPopulationTextColor() => new Color(0.12f, 0.12f, 0.15f);

        /// <summary>Position text above the ring so it's visible (not underneath).</summary>
        protected override Vector3 GetPopulationTextLocalPosition() => new Vector3(0f, 0.8f, 0f);

        /// <summary>Home planets have max 100 people (until level upgrade increases it later).</summary>
        protected override float GetMaxPopulationForPlanet() => 100f;

        /// <summary>Home planets: 1 person per 5 seconds.</summary>
        protected override float GetGrowthRatePerSecond() => 1f / 5f;

        /// <summary>
        /// Ensures body collider = planet sphere (radius 0.5), orbit zone = 0.5 to 0.6 (10% band). Base Planet may already create zone; we fix sizes.
        /// </summary>
        private void EnsureSolidColliderAndOrbitZone()
        {
            SphereCollider bodyCollider = GetComponent<SphereCollider>();
            if (bodyCollider != null)
            {
                bodyCollider.isTrigger = false;
                bodyCollider.radius = 0.5f; // Match Unity primitive sphere (diameter 1)
            }
            PlanetOrbitZone existing = GetComponentInChildren<PlanetOrbitZone>();
            if (existing != null)
            {
                var col = existing.GetComponent<SphereCollider>();
                if (col != null) col.radius = 0.8f;
                return;
            }
            GameObject orbitZoneObj = new GameObject("OrbitZone");
            orbitZoneObj.transform.SetParent(transform);
            orbitZoneObj.transform.localPosition = Vector3.zero;
            orbitZoneObj.transform.localScale = Vector3.one;
            SphereCollider orbitCol = orbitZoneObj.AddComponent<SphereCollider>();
            orbitCol.isTrigger = true;
            orbitCol.radius = 0.8f;
            PlanetOrbitZone zone = orbitZoneObj.AddComponent<PlanetOrbitZone>();
            zone.SetPlanet(this);
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
            
            // Level up when gems reach threshold for next level (max home planet level 6)
            if (currentLevel < 6 && currentLevel < gemThresholdsPerLevel.Length)
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
            if (homePlanetLevel.Value >= 6) return; // Max level

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
            return 6; // Default (e.g. planet level 3+)
        }

        /// <summary>Gems required to reach this level (level 6 = gemThresholdsPerLevel[5]).</summary>
        public float GetGemsThresholdForLevel(int level)
        {
            int idx = level - 1;
            if (idx >= 0 && idx < gemThresholdsPerLevel.Length)
                return gemThresholdsPerLevel[idx];
            return 0f;
        }

        /// <summary>True when home planet is level 6 and has at least the gem threshold for level 6 (unlocks ship level 7 MEGA).</summary>
        public bool IsFullGemsForLevel7Unlock()
        {
            if (homePlanetLevel.Value < 6) return false;
            float thresholdForLevel6 = GetGemsThresholdForLevel(6);
            return currentGems.Value >= thresholdForLevel6;
        }

        public float GetGemsNeededForNextLevel()
        {
            int currentLevel = homePlanetLevel.Value;
            if (currentLevel >= 6) return 0f; // Max level

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
