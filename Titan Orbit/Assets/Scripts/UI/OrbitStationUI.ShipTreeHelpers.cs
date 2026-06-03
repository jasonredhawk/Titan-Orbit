using System;
using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.Entities;
using TitanOrbit.Systems;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.UI
{
    /// <summary>Ship upgrade tree binding helpers (kept out of main OrbitStationUI for clarity).</summary>
    public partial class OrbitStationUI
    {
        private void EnsureShipUpgradeTreeInstance(Transform parent)
        {
            if (shipUpgradeTree != null)
                return;

            if (shipUpgradeTreePrefab == null)
                shipUpgradeTreePrefab = Resources.Load<ShipUpgradeTreeUI>("ShipUpgradeTree");

            if (shipUpgradeTreePrefab == null)
            {
                Debug.LogError("OrbitStationUI: Assign Ship Upgrade Tree Prefab or run Titan Orbit / UI / Create Ship Upgrade Tree Prefab.");
                return;
            }

            shipUpgradeTree = Instantiate(shipUpgradeTreePrefab, parent);
            shipUpgradeTree.BindStation(this);
            shipUpgradeTree.Clear();
            shipTreeHintText = shipUpgradeTree.Hint;
            shipTreeCenterRow = shipUpgradeTree.CenterRow;
            shipTreeCanvas = shipUpgradeTree.NodesCanvas;

            var treeLe = shipUpgradeTree.GetComponent<LayoutElement>();
            if (treeLe == null)
                treeLe = shipUpgradeTree.gameObject.AddComponent<LayoutElement>();
            treeLe.flexibleWidth = 1f;
            treeLe.flexibleHeight = 1f;
            treeLe.minHeight = 280f;

            ApplyShipUpgradeTreeContainerLayout();
        }

        private void ApplyShipUpgradeTreeContainerLayout()
        {
            if (shipUpgradeTree == null)
                return;

            bool fillContainer = _moonDockLayoutActive && _moonDockShipTreeHorizontal;

            var treeRt = (RectTransform)shipUpgradeTree.transform;
            var treeLe = shipUpgradeTree.GetComponent<LayoutElement>();

            if (fillContainer)
            {
                treeRt.anchorMin = Vector2.zero;
                treeRt.anchorMax = Vector2.one;
                treeRt.pivot = new Vector2(0.5f, 0.5f);
                treeRt.offsetMin = new Vector2(4f, 4f);
                treeRt.offsetMax = new Vector2(-4f, -4f);
                treeRt.anchoredPosition = Vector2.zero;
                treeRt.sizeDelta = Vector2.zero;
                if (treeLe != null)
                {
                    treeLe.flexibleWidth = 1f;
                    treeLe.flexibleHeight = 1f;
                    treeLe.minWidth = 160f;
                    treeLe.minHeight = 280f;
                }

                if (shipsTabContent != null)
                {
                    var shipsRt = shipsTabContent.GetComponent<RectTransform>();
                    shipsRt.anchorMin = Vector2.zero;
                    shipsRt.anchorMax = Vector2.one;
                    shipsRt.pivot = new Vector2(0.5f, 0.5f);
                    shipsRt.offsetMin = Vector2.zero;
                    shipsRt.offsetMax = Vector2.zero;
                    shipsRt.sizeDelta = Vector2.zero;
                    var shipsTabLe = shipsTabContent.GetComponent<LayoutElement>();
                    if (shipsTabLe != null)
                    {
                        shipsTabLe.flexibleWidth = 1f;
                        shipsTabLe.flexibleHeight = 1f;
                        shipsTabLe.minHeight = 280f;
                    }
                }
            }
            else
            {
                treeRt.anchorMin = new Vector2(0f, 1f);
                treeRt.anchorMax = new Vector2(1f, 1f);
                treeRt.pivot = new Vector2(0.5f, 1f);
                treeRt.anchoredPosition = new Vector2(0f, -72f);
                treeRt.sizeDelta = new Vector2(-24f, 520f);
                if (treeLe != null)
                {
                    treeLe.flexibleWidth = 1f;
                    treeLe.flexibleHeight = 0f;
                    treeLe.minHeight = 420f;
                    treeLe.preferredHeight = 520f;
                }
            }

            var treeVlg = treeRt.GetComponent<VerticalLayoutGroup>();
            if (treeVlg != null)
            {
                treeVlg.childControlHeight = true;
                treeVlg.childForceExpandHeight = fillContainer;
            }
        }

        internal void RefreshShipTreeVisualStateOnly()
        {
            if (shipUpgradeTree == null)
                return;

            if (!IsTreeDataAvailable())
            {
                if (shipUpgradeTree.Hint != null)
                    shipUpgradeTree.Hint.text = "Upgrade tree unavailable.";
                return;
            }

            shipUpgradeTree.RefreshVisualState();
        }

        /// <summary>Called after a ship purchase/swap so tree highlights and labels match the new hull.</summary>
        public void RefreshShipTreeAfterShipChange()
        {
            RefreshShipTreeVisualStateOnly();
            RefreshShipsTab(scrollToActiveShipNode: false);
        }

        internal bool IsTreeDataAvailable()
        {
            return UpgradeSystem.Instance != null
                && UpgradeSystem.Instance.UpgradeTree != null
                && currentShip != null
                && GetShipUpgradeStorePlanet() != null
                && CardShopSystem.Instance != null;
        }

        internal float GetShipTreeLayoutBasisWidthPublic() => GetShipTreeLayoutBasisWidth();

        internal bool TryGetPlayerUpgradePathEdges(out HashSet<(int fL, int fB, int tL, int tB)> edges)
        {
            edges = new HashSet<(int, int, int, int)>();
            if (currentShip == null) return false;
            int L = currentShip.ShipLevel;
            int B = currentShip.BranchIndex;
            if (L < 1) return false;
            while (L > 1)
            {
                int prevL = L - 1;
                int parentB = -1;
                int countP = UpgradeTree.GetShipCountForLevel(prevL);
                for (int p = 0; p < countP; p++)
                {
                    if (UpgradeTree.IsValidUpgradeStep(prevL, p, L, B))
                    {
                        parentB = p;
                        break;
                    }
                }

                if (parentB < 0)
                {
                    edges.Clear();
                    return false;
                }

                edges.Add((prevL, parentB, L, B));
                L = prevL;
                B = parentB;
            }

            return true;
        }

        internal void RefreshShipUpgradeTreeNodeStates(IReadOnlyList<ShipUpgradeTreeNodeUI> nodes, float maxPower)
        {
            UpdateShipTreeHintText();
            if (nodes == null || nodes.Count == 0)
                return;

            for (int i = 0; i < nodes.Count; i++)
                PopulateTreeNode(nodes[i], maxPower);
        }

        private static bool IsDebugFreeShipUpgradeTree() =>
            GameManager.Instance != null && GameManager.Instance.DebugFreeShipUpgradeTree;

        internal void PopulateTreeNode(ShipUpgradeTreeNodeUI view, float maxPower)
        {
            if (view == null || currentShip == null)
                return;

            UpgradeTree tree = UpgradeSystem.Instance != null ? UpgradeSystem.Instance.UpgradeTree : null;
            Planet storePlanet = GetShipUpgradeStorePlanet();
            if (tree == null || storePlanet == null || CardShopSystem.Instance == null)
                return;

            if (view.IsCurrentShipDisplay)
            {
                PopulateCurrentShipDisplayNode(view, maxPower);
                return;
            }

            if (IsDebugFreeShipUpgradeTree())
            {
                PopulateTreeNodeDebug(view, maxPower);
                return;
            }

            int homeLevel = currentHomePlanet != null ? Mathf.Max(1, currentHomePlanet.HomePlanetLevel) : 1;
            int currentLevel = currentShip.ShipLevel;
            int currentBranch = currentShip.BranchIndex;
            int nextLevel = currentLevel + 1;
            bool canBuyAny = CardShopSystem.Instance.CanPurchaseShipLevelUpgrade(currentShip, storePlanet, out _, out _, out _);
            int storePlanetLevel = Mathf.Max(1, storePlanet.PlanetLevel);

            bool isCurrent = view.Level == currentLevel && view.BranchIndex == currentBranch;
            bool tierBlockedByHome = view.Level > homeLevel;
            bool tierBlockedByStore = view.Level > storePlanetLevel;
            bool tierBlocked = tierBlockedByHome || tierBlockedByStore;
            bool isNextChoice = false;
            if (view.Level == nextLevel)
            {
                UpgradeTree.GetNextLevelBranchTargets(currentLevel, currentBranch, _shipTreeNextTargets);
                isNextChoice = _shipTreeNextTargets.Contains(view.BranchIndex);
            }

            bool ladderOk = !string.IsNullOrEmpty(
                CardShopSystem.Instance.GetChassisIdForUpgradeLadderSlot(currentShip, storePlanet.PlanetId, view.Level, view.BranchIndex));
            bool canApplyPurchase = ladderOk || (view.Node != null && view.Node.shipData != null);
            bool canSwapHull = isCurrent && !tierBlockedByHome
                && CardShopSystem.Instance.CanSwapShipAtSameTreeSlot(currentShip, storePlanet, view.Level, view.BranchIndex, out _);
            int nodeCost = isNextChoice
                ? CardShopSystem.Instance.GetPurchaseGemCostForUpgradeSlot(
                    currentShip, storePlanet.PlanetId, nextLevel, view.BranchIndex)
                : 0;
            bool canPurchase = isNextChoice && canBuyAny && contributedGems >= nodeCost && !tierBlocked && canApplyPurchase;

            string slotChassisId = CardShopSystem.Instance.GetChassisIdForUpgradeLadderSlot(
                currentShip, storePlanet.PlanetId, currentLevel, currentBranch);
            bool storePlanetLevelBlocksSwap = !string.IsNullOrEmpty(slotChassisId)
                && !string.Equals(slotChassisId, currentShip.CurrentChassisId, StringComparison.OrdinalIgnoreCase)
                && storePlanetLevel < currentLevel;

            view.SetInteractable(canSwapHull || canPurchase);
            view.EnsureStableButtonRendering();
            if (canSwapHull) view.SetButtonBackgroundColor(new Color(0.28f, 0.68f, 0.82f, 0.98f));
            else if (isCurrent) view.SetButtonBackgroundColor(new Color(0.26f, 0.62f, 0.36f, 0.98f));
            else if (tierBlocked) view.SetButtonBackgroundColor(new Color(0.1f, 0.11f, 0.14f, 0.92f));
            else if (isNextChoice) view.SetButtonBackgroundColor(new Color(0.25f, 0.48f, 0.78f, 0.98f));
            else view.SetButtonBackgroundColor(new Color(0.19f, 0.23f, 0.31f, 0.94f));

            Sprite sp = ResolveShipTreePreviewSprite(view.Level, view.BranchIndex);
            view.SetPreview(sp);

            if (view.UsesMoonHorizontalLayout)
                view.SetLevelLabel(view.Level == 1 ? "Lv 1" : $"Lv {view.Level}");
            else
                view.SetLevelLabel(view.Level == 1 ? "1" : view.Level.ToString());

            view.SetShipName(GetShipDisplayName(view.Node, view.Level, view.BranchIndex));

            if (canSwapHull)
                view.SetPrice("Free");
            else if (isCurrent && storePlanetLevelBlocksSwap)
                view.SetPrice($"Planet Lv {currentLevel}+");
            else if (isNextChoice && tierBlockedByStore && !tierBlockedByHome)
                view.SetPrice($"Planet Lv {view.Level}+");
            else if (view.Level == 1)
                view.SetPrice("—");
            else
                view.SetPrice($"{CardShopSystem.Instance.GetPurchaseGemCostForUpgradeSlot(currentShip, storePlanet.PlanetId, view.Level, view.BranchIndex)}g");

            view.ApplyPowerBreakdown(GetPowerBreakdownForTreeNode(view.Level, view.BranchIndex), maxPower);
        }

        private void PopulateTreeNodeDebug(ShipUpgradeTreeNodeUI view, float maxPower)
        {
            int currentLevel = currentShip.ShipLevel;
            int currentBranch = currentShip.BranchIndex;
            bool isCurrent = view.Level == currentLevel && view.BranchIndex == currentBranch;
            int nodeLevel = view.Level;
            int nodeBranch = view.BranchIndex;

            view.SetPriceClickHandler(() => OnUpgradeTreeNodeClicked(nodeLevel, nodeBranch));
            view.SetInteractable(true);
            view.EnsureStableButtonRendering();
            view.SetInteractable(true);
            view.SetButtonBackgroundColor(isCurrent
                ? new Color(0.26f, 0.62f, 0.36f, 0.98f)
                : new Color(0.28f, 0.68f, 0.82f, 0.98f));

            view.SetPreview(ResolveShipTreePreviewSprite(view.Level, view.BranchIndex));

            if (view.UsesMoonHorizontalLayout)
                view.SetLevelLabel(view.Level == 1 ? "Lv 1" : $"Lv {view.Level}");
            else
                view.SetLevelLabel(view.Level == 1 ? "1" : view.Level.ToString());

            view.SetShipName(GetShipDisplayName(view.Node, view.Level, view.BranchIndex));

            view.SetPrice("Free");
            view.ApplyPowerBreakdown(GetPowerBreakdownForTreeNode(view.Level, view.BranchIndex), maxPower);
        }

        private void PopulateCurrentShipDisplayNode(ShipUpgradeTreeNodeUI view, float maxPower)
        {
            Planet storePlanet = GetShipUpgradeStorePlanet();
            int currentLevel = currentShip.ShipLevel;
            int currentBranch = currentShip.BranchIndex;
            bool canSwapHull = CardShopSystem.Instance.CanSwapShipAtSameTreeSlot(
                currentShip, storePlanet, currentLevel, currentBranch, out _);

            view.SetInteractable(canSwapHull);
            view.EnsureStableButtonRendering();
            if (canSwapHull)
                view.SetButtonBackgroundColor(new Color(0.28f, 0.68f, 0.82f, 0.98f));
            else
                view.SetButtonBackgroundColor(new Color(0.26f, 0.62f, 0.36f, 0.98f));

            view.SetPreview(ResolveCurrentShipPreviewSprite());
            if (view.UsesMoonHorizontalLayout)
                view.SetLevelLabel("You");
            else
                view.SetLevelLabel($"Lv {currentLevel}");
            view.SetShipName(GetCurrentShipDisplayName());
            view.SetPrice(canSwapHull ? "Free" : "—");
            view.ApplyPowerBreakdown(GetCurrentShipPowerBreakdown(), maxPower);
        }

        private void UpdateShipTreeHintText()
        {
            if (shipUpgradeTree == null)
                return;

            shipUpgradeTree.EnsurePanelHeader();
            if (shipUpgradeTree.Title != null)
                shipUpgradeTree.Title.text = ShipUpgradeTreeUI.PanelTitleText;

            if (shipUpgradeTree.Hint == null || currentShip == null)
                return;

            UpgradeTree tree = UpgradeSystem.Instance != null ? UpgradeSystem.Instance.UpgradeTree : null;
            Planet storePlanet = GetShipUpgradeStorePlanet();
            if (tree == null || storePlanet == null || CardShopSystem.Instance == null)
            {
                shipUpgradeTree.Hint.text = "Upgrade tree unavailable.";
                return;
            }

            int homeLevel = currentHomePlanet != null ? Mathf.Max(1, currentHomePlanet.HomePlanetLevel) : 1;
            int currentLevel = currentShip.ShipLevel;
            int currentBranch = currentShip.BranchIndex;
            int nextLevel = currentLevel + 1;
            bool canSwapHullAtCurrentSlot = CardShopSystem.Instance.CanSwapShipAtSameTreeSlot(
                currentShip, storePlanet, currentLevel, currentBranch, out _);
            string slotChassisId = CardShopSystem.Instance.GetChassisIdForUpgradeLadderSlot(
                currentShip, storePlanet.PlanetId, currentLevel, currentBranch);
            bool hasAlternateHullAtSlot = !string.IsNullOrEmpty(slotChassisId)
                && !string.Equals(slotChassisId, currentShip.CurrentChassisId, StringComparison.OrdinalIgnoreCase);
            int storePlanetLevel = Mathf.Max(1, storePlanet.PlanetLevel);
            bool storePlanetLevelBlocksSwap = hasAlternateHullAtSlot && storePlanetLevel < currentLevel;
            bool homeAllowsNextUpgrade = currentLevel < 7 && homeLevel >= nextLevel;
            bool upgradeBlockedByStoreLevel = homeAllowsNextUpgrade && nextLevel > storePlanetLevel;

            if (IsDebugFreeShipUpgradeTree())
                shipUpgradeTree.Hint.text = "Debug: click any ship to try it for free (all tiers unlocked).";
            else if (canSwapHullAtCurrentSlot)
                shipUpgradeTree.Hint.text = "Click your ship to swap to this moon's hull at your tier (free).";
            else if (storePlanetLevelBlocksSwap)
                shipUpgradeTree.Hint.text = $"This planet must reach level {currentLevel} to swap your level {currentLevel} ship.";
            else if (upgradeBlockedByStoreLevel)
                shipUpgradeTree.Hint.text = $"This planet must reach level {nextLevel} to purchase a level {nextLevel} ship.";
            else if (nextLevel <= 7 && homeLevel < nextLevel)
                shipUpgradeTree.Hint.text = $"Locked — raise home planet to level {nextLevel}.";
            else
                shipUpgradeTree.Hint.text = ShipUpgradeTreeUI.PanelDefaultSubtitle;
        }

        internal ShipFamilyPowerScoreBreakdown GetPowerBreakdownForTreeNode(int level, int branchIndex)
        {
            Planet storePlanet = GetShipUpgradeStorePlanet();
            if (currentShip == null || storePlanet == null || CardShopSystem.Instance == null)
                return default;
            return CardShopSystem.Instance.GetPowerScoreBreakdownForUpgradeSlot(
                currentShip, storePlanet.PlanetId, level, branchIndex);
        }

        internal ShipFamilyPowerScoreBreakdown GetCurrentShipPowerBreakdown()
        {
            if (currentShip == null || CardShopSystem.Instance == null)
                return default;
            return CardShopSystem.Instance.GetPowerScoreBreakdownForChassisId(currentShip.CurrentChassisId);
        }

        internal void OnCurrentShipDisplayNodeClicked()
        {
            if (currentShip == null)
                return;
            OnUpgradeTreeNodeClicked(currentShip.ShipLevel, currentShip.BranchIndex);
        }

        internal void OnUpgradeTreeNodeClicked(int nodeLevel, int targetBranchIndex)
        {
            Planet storePlanet = GetShipUpgradeStorePlanet();
            if (currentShip == null || storePlanet == null || CardShopSystem.Instance == null) return;
            var planetNo = storePlanet.GetComponent<Unity.Netcode.NetworkObject>();
            if (planetNo == null || !planetNo.IsSpawned) return;

            if (IsDebugFreeShipUpgradeTree())
            {
                if (nodeLevel == currentShip.ShipLevel && targetBranchIndex == currentShip.BranchIndex)
                    return;

                if (!CardShopSystem.Instance.IsSpawned)
                {
                    Debug.LogWarning("OrbitStationUI: CardShopSystem is not spawned — start a networked game (host) to change ships.");
                    return;
                }

                CardShopSystem.Instance.RequestDebugSelectUpgradeTreeNode(
                    planetNo.NetworkObjectId, currentShip.NetworkObjectId, nodeLevel, targetBranchIndex);
                return;
            }

            if (nodeLevel == currentShip.ShipLevel + 1)
            {
                CardShopSystem.Instance.PurchaseShipLevelUpgradeServerRpc(planetNo.NetworkObjectId, currentShip.NetworkObjectId, targetBranchIndex);
                pendingGemsRequest = true;
                if (HomePlanetStoreSystem.Instance != null) HomePlanetStoreSystem.Instance.RequestContributedGemsServerRpc();
                return;
            }

            if (nodeLevel == currentShip.ShipLevel && targetBranchIndex == currentShip.BranchIndex)
            {
                if (!CardShopSystem.Instance.CanSwapShipAtSameTreeSlot(currentShip, storePlanet, nodeLevel, targetBranchIndex, out _))
                    return;
                CardShopSystem.Instance.SwapShipAtSameTreeSlotServerRpc(planetNo.NetworkObjectId, currentShip.NetworkObjectId, nodeLevel, targetBranchIndex);
            }
        }
    }
}
