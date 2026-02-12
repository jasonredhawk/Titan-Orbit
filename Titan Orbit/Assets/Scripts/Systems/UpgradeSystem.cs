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

        [ServerRpc(RequireOwnership = false)]
        public void UpgradeShipServerRpc(ulong shipNetworkId, int targetLevel, ShipFocusType targetFocus, int shipIndex)
        {
            NetworkObject shipNetObj = GetNetworkObject(shipNetworkId);
            if (shipNetObj == null) return;

            Starship ship = shipNetObj.GetComponent<Starship>();
            if (ship == null) return;

            // Check if player has enough gems
            float gemCost = upgradeTree.GetGemCostForLevel(targetLevel);
            if (ship.CurrentGems < gemCost) return;

            // Check home planet level restriction
            HomePlanet homePlanet = GetHomePlanetForTeam(ship.ShipTeam);
            if (homePlanet != null)
            {
                int maxShipLevel = homePlanet.MaxShipLevel;
                if (targetLevel > maxShipLevel) return;
            }

            // Get available upgrades
            var availableUpgrades = upgradeTree.GetAvailableUpgrades(ship.ShipLevel, ship.FocusType);
            if (shipIndex < 0 || shipIndex >= availableUpgrades.Count) return;

            ShipUpgradeNode upgradeNode = availableUpgrades[shipIndex];
            
            // Apply upgrade
            ship.RemoveGemsServerRpc(gemCost);
            ApplyShipUpgrade(ship, upgradeNode, targetLevel);

            UpgradeShipClientRpc(shipNetworkId, targetLevel, targetFocus);
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
        private void UpgradeShipClientRpc(ulong shipNetworkId, int newLevel, ShipFocusType newFocus)
        {
            Debug.Log($"Ship upgraded to level {newLevel} with focus {newFocus}");
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
