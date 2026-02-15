using UnityEngine;
using UnityEngine.UI;
using TMPro;
using TitanOrbit.Entities;
using TitanOrbit.Data;
using TitanOrbit.Systems;

namespace TitanOrbit.UI
{
    /// <summary>
    /// UI for ship upgrade selection and navigation
    /// </summary>
    public class ShipUpgradeUI : MonoBehaviour
    {
        [Header("UI References")]
        [SerializeField] private GameObject upgradePanel;
        [SerializeField] private Button[] shipOptionButtons = new Button[2];
        [SerializeField] private TextMeshProUGUI[] shipOptionNames = new TextMeshProUGUI[2];
        [SerializeField] private TextMeshProUGUI[] shipOptionStats = new TextMeshProUGUI[2];
        [SerializeField] private TextMeshProUGUI gemCostText;
        [SerializeField] private Button upgradeButton;

        [Header("References")]
        [SerializeField] private UpgradeTree upgradeTree;

        private Starship currentShip;
        private int selectedShipIndex = -1;

        public void ShowUpgradeMenu(Starship ship)
        {
            currentShip = ship;
            upgradePanel.SetActive(true);
            UpdateUpgradeOptions();
        }

        public void HideUpgradeMenu()
        {
            upgradePanel.SetActive(false);
        }

        private void UpdateUpgradeOptions()
        {
            if (currentShip == null || upgradeTree == null) return;

            int nextLevel = currentShip.ShipLevel + 1;
            var availableUpgrades = upgradeTree.GetAvailableUpgrades(currentShip.ShipLevel, currentShip.BranchIndex);

            // Update gem cost
            float gemCost = upgradeTree.GetGemCostForLevel(nextLevel);
            if (gemCostText != null)
            {
                gemCostText.text = $"Cost: {gemCost:F0} Gems";
            }

            // Update ship options
            for (int i = 0; i < shipOptionButtons.Length; i++)
            {
                if (i < availableUpgrades.Count)
                {
                    var upgradeNode = availableUpgrades[i];
                    
                    if (shipOptionButtons[i] != null)
                    {
                        shipOptionButtons[i].gameObject.SetActive(true);
                        int index = i; // Capture for lambda
                        shipOptionButtons[i].onClick.RemoveAllListeners();
                        shipOptionButtons[i].onClick.AddListener(() => SelectShipOption(index));
                    }

                    if (shipOptionNames[i] != null)
                    {
                        shipOptionNames[i].text = upgradeNode.shipName;
                    }

                    if (shipOptionStats[i] != null)
                    {
                        shipOptionStats[i].text = $"Focus: {upgradeNode.focusType}\n" +
                                                  $"Speed: {upgradeNode.movementSpeedMultiplier:F1}x\n" +
                                                  $"Power: {upgradeNode.firePowerMultiplier:F1}x";
                    }
                }
                else
                {
                    if (shipOptionButtons[i] != null)
                    {
                        shipOptionButtons[i].gameObject.SetActive(false);
                    }
                }
            }

            // Update upgrade button
            if (upgradeButton != null)
            {
                upgradeButton.interactable = selectedShipIndex >= 0 && 
                                            currentShip.CurrentGems >= gemCost;
            }
        }

        private void SelectShipOption(int index)
        {
            selectedShipIndex = index;
            UpdateUpgradeOptions();
        }

        public void OnUpgradeButtonClicked()
        {
            if (currentShip == null || selectedShipIndex < 0) return;
            if (UpgradeSystem.Instance == null) return;

            int nextLevel = currentShip.ShipLevel + 1;
            var availableUpgrades = upgradeTree.GetAvailableUpgrades(currentShip.ShipLevel, currentShip.BranchIndex);
            
            if (selectedShipIndex < availableUpgrades.Count)
            {
                var upgradeNode = availableUpgrades[selectedShipIndex];
                UpgradeSystem.Instance.UpgradeShipServerRpc(
                    currentShip.NetworkObjectId,
                    nextLevel,
                    upgradeNode.focusType,
                    selectedShipIndex
                );
            }

            HideUpgradeMenu();
        }
    }
}
