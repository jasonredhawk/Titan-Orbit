using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Entities;
using TitanOrbit.Data;
using TitanOrbit.Core;

namespace TitanOrbit.Systems
{
    /// <summary>
    /// Handles ship and planet upgrade mechanics
    /// </summary>
    public class UpgradeSystem : NetworkBehaviour
    {
        public static UpgradeSystem Instance { get; private set; }

        [Header("Upgrade Settings")]
        [SerializeField] private UpgradeTree upgradeTree;

        public UpgradeTree UpgradeTree => upgradeTree;

        private void Awake()
        {
            if (Instance == null)
            {
                Instance = this;
            }
            else
            {
                Destroy(gameObject);
                return;
            }
            // Fallback: if tree not assigned (e.g. scene set up before tree existed), try Resources
            if (upgradeTree == null)
                upgradeTree = Resources.Load<UpgradeTree>("UpgradeTree");
        }

        [ServerRpc(RequireOwnership = false)]
        public void UpgradeShipServerRpc(ulong shipNetworkId, int targetLevel, ShipFocusType targetFocus, int shipIndex)
        {
            if (upgradeTree == null) return;
            NetworkObject shipNetObj = GetNetworkObject(shipNetworkId);
            if (shipNetObj == null) return;

            Starship ship = shipNetObj.GetComponent<Starship>();
            if (ship == null) return;

            const float fullEpsilon = 0.01f;
            if (ship.CurrentGems < ship.GemCapacity - fullEpsilon) return; // must be full of gems
            float gemCost = upgradeTree.GetGemCostForLevel(targetLevel);
            float actualCharge = Mathf.Min(gemCost, ship.CurrentGems); // never charge more than they have

            HomePlanet homePlanet = GetHomePlanetForTeam(ship.ShipTeam);
            if (homePlanet == null) return;

            // Ship level cannot exceed home planet level (enforced in code, not serialized array). Level 7 only when planet 6 + full gems.
            int planetLevel = homePlanet.HomePlanetLevel;
            if (targetLevel == 7)
            {
                if (planetLevel < 6 || !homePlanet.IsFullGemsForLevel7Unlock()) return;
            }
            else if (targetLevel > planetLevel)
            {
                return; // e.g. planet 4 → ship can only go up to 4
            }

            var availableUpgrades = upgradeTree.GetAvailableUpgrades(ship.ShipLevel, ship.BranchIndex);
            if (shipIndex < 0 || shipIndex >= availableUpgrades.Count) return;

            ShipUpgradeNode upgradeNode = availableUpgrades[shipIndex];
            int previousBranchIndex = ship.BranchIndex;

            ship.RemoveGemsServerRpc(actualCharge);
            ApplyShipUpgrade(ship, upgradeNode, targetLevel);

            UpgradeShipClientRpc(shipNetworkId, targetLevel, previousBranchIndex, shipIndex);
        }

        private void ApplyShipUpgrade(Starship ship, ShipUpgradeNode upgradeNode, int newLevel)
        {
            if (upgradeNode.shipData != null)
            {
                ship.SetShipData(upgradeNode.shipData);
            }

            // Apply multipliers to ship stats
            // Note: This would need to be implemented in Starship class
            // For now, we'll rely on ShipData
        }

        /// <summary>True when ship is full of gems and can upgrade. Ship level cannot exceed home planet level; level 7 requires planet 6 + full gems.</summary>
        public bool CanUpgradeStarshipLevel(Starship ship)
        {
            if (ship == null || upgradeTree == null) return false;
            if (ship.ShipLevel >= 7) return false;
            const float fullEpsilon = 0.01f;
            if (ship.CurrentGems < ship.GemCapacity - fullEpsilon) return false; // must be full of gems
            int nextLevel = ship.ShipLevel + 1;

            HomePlanet homePlanet = GetHomePlanetForTeam(ship.ShipTeam);
            if (homePlanet == null) return false;
            int planetLevel = homePlanet.HomePlanetLevel; // use level directly so cap works regardless of serialized array
            if (nextLevel == 7)
            {
                if (planetLevel < 6 || !homePlanet.IsFullGemsForLevel7Unlock()) return false;
            }
            else if (nextLevel > planetLevel)
            {
                return false; // e.g. planet 4 → ship can only go up to 4
            }
            return upgradeTree.GetAvailableUpgrades(ship.ShipLevel, ship.BranchIndex).Count > 0;
        }

        private HomePlanet GetHomePlanetForTeam(TeamManager.Team team)
        {
            HomePlanet[] allHomePlanets = FindObjectsOfType<HomePlanet>();
            foreach (var homePlanet in allHomePlanets)
            {
                if (homePlanet.AssignedTeam == team)
                {
                    return homePlanet;
                }
            }
            return null;
        }

        [ClientRpc]
        private void UpgradeShipClientRpc(ulong shipNetworkId, int newLevel, int previousBranchIndex, int shipIndex)
        {
            if (upgradeTree == null) return;
            int previousLevel = newLevel - 1;
            var available = upgradeTree.GetAvailableUpgrades(previousLevel, previousBranchIndex);
            if (shipIndex < 0 || shipIndex >= available.Count || available[shipIndex].shipData == null) return;
            NetworkObject netObj = GetNetworkObject(shipNetworkId);
            if (netObj == null) return;
            Starship ship = netObj.GetComponent<Starship>();
            if (ship != null)
                ship.SetShipData(available[shipIndex].shipData);
        }

        [ServerRpc(RequireOwnership = false)]
        public void UpgradePlanetServerRpc(ulong planetNetworkId, PlanetUpgradeType upgradeType)
        {
            NetworkObject planetNetObj = GetNetworkObject(planetNetworkId);
            if (planetNetObj == null) return;

            Planet planet = planetNetObj.GetComponent<Planet>();
            if (planet == null) return;

            // Apply planet upgrade
            switch (upgradeType)
            {
                case PlanetUpgradeType.MaxPopulation:
                    // Increase max population
                    break;
                case PlanetUpgradeType.GrowthRate:
                    // Increase growth rate
                    break;
            }
        }

        public enum PlanetUpgradeType
        {
            MaxPopulation,
            GrowthRate
        }
    }
}
