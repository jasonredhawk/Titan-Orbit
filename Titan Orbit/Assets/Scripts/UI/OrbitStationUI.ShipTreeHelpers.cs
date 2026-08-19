using System;
using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Entities;
using TitanOrbit.Game;
using TitanOrbit.Systems;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Partial <see cref="OrbitStationUI"/> — ship upgrade tree instantiation, layout, node population,
    /// purchase/swap clicks, and hint text. Kept in a separate file so the main station UI stays readable.
    /// </summary>
    public partial class OrbitStationUI
    {
        /// <summary>Instantiates tree prefab under ships tab and wires layout element + host binding.</summary>
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
            RefreshSidebar();
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

        /// <summary>Walks upgrade path from current ship back to level 1 for green connector highlighting.</summary>
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

        internal void RefreshShipUpgradeTreeNodeStates(IReadOnlyList<ShipUpgradeTreeNodeUI> nodes, ShipPowerBarStatMaxes maxes)
        {
            UpdateShipTreeHintText();

            // [HYBRID] Names for unique MEGA occupancy chips — refresh once per tree paint.
            EcsGameBridge.RefreshPlayerDisplayNameCache();

            if (nodes == null || nodes.Count == 0)
                return;

            for (int i = 0; i < nodes.Count; i++)
                PopulateTreeNode(nodes[i], maxes);
        }

        private static bool IsDebugFreeShipUpgradeTree() =>
            GameManager.IsDebugFreeShipUpgradeTreeActive;

        /// <summary>
        /// Colors, prices, interactable state, and power bars for every tree node (and current-ship display).
        /// Called from <see cref="ShipUpgradeTreeUI.RefreshVisualState"/>.
        /// </summary>
        internal void PopulateTreeNode(ShipUpgradeTreeNodeUI view, ShipPowerBarStatMaxes maxes)
        {
            if (view == null || currentShip == null)
                return;

            UpgradeTree tree = UpgradeSystem.Instance != null ? UpgradeSystem.Instance.UpgradeTree : null;
            Planet storePlanet = GetShipUpgradeStorePlanet();
            if (tree == null || storePlanet == null || CardShopSystem.Instance == null)
                return;

            if (view.IsCurrentShipDisplay)
            {
                PopulateCurrentShipDisplayNode(view, maxes);
                return;
            }

            if (IsDebugFreeShipUpgradeTree())
            {
                PopulateTreeNodeDebug(view, maxes);
                return;
            }

            // --- Unlock / purchase eligibility ---
            int homeLevel = currentHomePlanet != null ? Mathf.Max(1, currentHomePlanet.HomePlanetLevel) : 1;
            int currentLevel = currentShip.ShipLevel;
            int currentBranch = currentShip.BranchIndex;
            int nextLevel = currentLevel + 1;
            bool canBuyAny = CardShopSystem.Instance.CanPurchaseShipLevelUpgrade(currentShip, storePlanet, out _, out _, out _);
            int storePlanetLevel = Mathf.Max(1, storePlanet.PlanetLevel);

            bool isCurrent = view.Level == currentLevel && view.BranchIndex == currentBranch;
            bool megaOccupied = false;
            int megaOccupiedBy = 0;
            bool megaUnlockBlocked = false;
            bool megaUnarmed = false;
            if (view.Level == 7)
            {
                if (MoonOrbitStationUI.TryResolveMegaOccupancy(
                        storePlanet.PlanetId, view.BranchIndex, out megaOccupiedBy, out megaUnarmed))
                    megaOccupied = megaOccupiedBy != 0;
                if (!EcsGameBridge.TryGetPlanetGemMoonStateByPlanetId(storePlanet.PlanetId, out var moon)
                    || !MegaShipPlanetLogic.IsMegaPurchaseUnlocked(
                        storePlanet.PlanetLevel, moon.CurrentMoonGems, moon.MaxMoonGems))
                    megaUnlockBlocked = true;
            }

            bool tierBlockedByHome = view.Level < 7 && view.Level > homeLevel;
            bool tierBlockedByStore = view.Level < 7 && view.Level > storePlanetLevel;
            bool tierBlocked = tierBlockedByHome || tierBlockedByStore || megaUnlockBlocked;
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
            bool canPurchase = isNextChoice && canBuyAny && contributedGems >= nodeCost && !tierBlocked
                && canApplyPurchase && !megaOccupied && !megaUnarmed;
            bool clickable = !megaOccupied && (canSwapHull || canPurchase);

            string slotChassisId = CardShopSystem.Instance.GetChassisIdForUpgradeLadderSlot(
                currentShip, storePlanet.PlanetId, currentLevel, currentBranch);
            bool storePlanetLevelBlocksSwap = !string.IsNullOrEmpty(slotChassisId)
                && !string.Equals(slotChassisId, currentShip.CurrentChassisId, StringComparison.OrdinalIgnoreCase)
                && storePlanetLevel < currentLevel;

            view.SetInteractable(clickable);
            view.EnsureStableButtonRendering();
            if (canSwapHull) view.SetButtonBackgroundColor(new Color(0.28f, 0.68f, 0.82f, 0.98f));
            else if (isCurrent) view.SetButtonBackgroundColor(new Color(0.26f, 0.62f, 0.36f, 0.98f));
            else if (tierBlocked) view.SetButtonBackgroundColor(new Color(0.1f, 0.11f, 0.14f, 0.92f));
            else if (isNextChoice) view.SetButtonBackgroundColor(new Color(0.25f, 0.48f, 0.78f, 0.98f));
            else view.SetButtonBackgroundColor(new Color(0.19f, 0.23f, 0.31f, 0.94f));

            Sprite sp = ResolveShipTreePreviewSprite(view.Level, view.BranchIndex);
            view.SetPreview(sp);

            view.SetLevelLabel(ShipUpgradeTreeNodeUI.FormatTreeLevelCaption(view.Level, view.UsesMoonHorizontalLayout));
            if (view.Level == 7)
                view.ApplyMegaShipCardStyle(isCurrent, canPurchase, megaOccupied, tierBlocked);
            else
                view.ClearMegaShipCardStyle();

            view.SetShipName(GetShipDisplayName(view.Node, view.Level, view.BranchIndex));

            if (megaOccupied)
            {
                view.SetPrice(MoonOrbitStationUI.FormatMegaOwnerPriceLabel(megaOccupiedBy));
                view.SetOwnedOccupantStyle();
            }
            else if (megaUnarmed)
                view.SetPrice("NO WEAPONS");
            else if (megaUnlockBlocked)
                view.SetPrice("MOON FULL");
            else if (canSwapHull)
                view.SetPrice("Free");
            else if (isCurrent && storePlanetLevelBlocksSwap)
                view.SetPrice($"Planet Lv {currentLevel}+");
            else if (isNextChoice && tierBlockedByStore && !tierBlockedByHome)
                view.SetPrice($"Planet Lv {view.Level}+");
            else if (view.Level == 1)
                view.SetPrice("—");
            else
                view.SetPrice($"{CardShopSystem.Instance.GetPurchaseGemCostForUpgradeSlot(currentShip, storePlanet.PlanetId, view.Level, view.BranchIndex)}g");

            // --- Power bar (regular vs MEGA pool) ---
            // [TITAN-ORBIT] Regular hulls fill against every family's L1–L6 chassis.
            // MEGA hulls fill against the armed MEGA catalog only. Mixing those
            // ceilings would shrink regular bars and flatten every MEGA bar to full.
            view.ApplyPowerBreakdown(
                GetPowerBreakdownForTreeNode(view.Level, view.BranchIndex),
                ShipFamilyPowerBarNorm.ResolveForTreeLevel(view.Level, maxes));
        }

        private void PopulateTreeNodeDebug(ShipUpgradeTreeNodeUI view, ShipPowerBarStatMaxes maxes)
        {
            int currentLevel = currentShip.ShipLevel;
            int currentBranch = currentShip.BranchIndex;
            bool isCurrent = view.Level == currentLevel && view.BranchIndex == currentBranch;
            int nodeLevel = view.Level;
            int nodeBranch = view.BranchIndex;
            Planet storePlanet = GetShipUpgradeStorePlanet();

            // --- Unique MEGA occupancy (debug still honors uniqueness) ---
            bool megaOccupied = false;
            int megaOccupiedBy = 0;
            if (nodeLevel == 7
                && storePlanet != null
                && MoonOrbitStationUI.TryResolveMegaOccupancy(
                    storePlanet.PlanetId, nodeBranch, out megaOccupiedBy, out _)
                && megaOccupiedBy != 0)
                megaOccupied = true;

            bool clickable = !megaOccupied;
            view.SetInteractable(clickable);
            view.EnsureStableButtonRendering();
            view.SetInteractable(clickable);
            view.SetButtonBackgroundColor(megaOccupied
                ? new Color(0.15f, 0.16f, 0.18f, 0.92f)
                : isCurrent
                    ? new Color(0.26f, 0.62f, 0.36f, 0.98f)
                    : new Color(0.28f, 0.68f, 0.82f, 0.98f));

            view.SetPreview(ResolveShipTreePreviewSprite(view.Level, view.BranchIndex));

            view.SetLevelLabel(ShipUpgradeTreeNodeUI.FormatTreeLevelCaption(view.Level, view.UsesMoonHorizontalLayout));
            if (view.Level == 7)
                view.ApplyMegaShipCardStyle(isCurrent, clickable && !isCurrent, megaOccupied, false);
            else
                view.ClearMegaShipCardStyle();

            view.SetShipName(GetShipDisplayName(view.Node, view.Level, view.BranchIndex));

            if (megaOccupied)
            {
                view.SetPrice(MoonOrbitStationUI.FormatMegaOwnerPriceLabel(megaOccupiedBy));
                view.SetOwnedOccupantStyle();
            }
            else
                view.SetPrice("Free");

            view.ApplyPowerBreakdown(
                GetPowerBreakdownForTreeNode(view.Level, view.BranchIndex),
                ShipFamilyPowerBarNorm.ResolveForTreeLevel(view.Level, maxes));
            view.SetPriceClickHandler(clickable ? () => OnUpgradeTreeNodeClicked(nodeLevel, nodeBranch) : null);
            if (megaOccupied)
                view.SetOwnedOccupantStyle();
        }

        private void PopulateCurrentShipDisplayNode(ShipUpgradeTreeNodeUI view, ShipPowerBarStatMaxes maxes)
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
            // Sidebar hero hides level ("You") and shows the hull name on top of the ship art.
            if (view.UsesSidebarHeroLayout)
                view.SetLevelLabel(string.Empty);
            else if (view.UsesMoonHorizontalLayout)
                view.SetLevelLabel("You");
            else
                view.SetLevelLabel($"Lv {currentLevel}");
            view.SetShipName(GetCurrentShipDisplayName());
            view.SetPrice(canSwapHull ? "Free" : "—");
            // Sidebar "You" card uses the same pool as the tree node for this hull.
            view.ApplyPowerBreakdown(
                GetCurrentShipPowerBreakdown(),
                ShipFamilyPowerBarNorm.ResolveForTreeLevel(currentLevel, maxes, currentShip.CurrentChassisId));
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
                shipUpgradeTree.Hint.text = "Debug: click any ship for free. Claimed MEGAs stay with their owner.";
            else if (canSwapHullAtCurrentSlot)
                shipUpgradeTree.Hint.text = "Click your ship in the left panel to swap to this moon's hull at your tier (free).";
            else if (storePlanetLevelBlocksSwap)
                shipUpgradeTree.Hint.text = $"This planet must reach level {currentLevel} to swap your level {currentLevel} ship.";
            else if (upgradeBlockedByStoreLevel)
                shipUpgradeTree.Hint.text = $"This planet must reach level {nextLevel} to purchase a level {nextLevel} ship.";
            else if (nextLevel == 7)
                shipUpgradeTree.Hint.text = "MEGA — planet level 6 and a full gem moon unlock these hulls. Each unique hull is in service on one ship at a time.";
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
            return CardShopSystem.Instance.GetPowerScoreBreakdownForUpgradeSlotAtShipLevel(
                currentShip, storePlanet.PlanetId, level, branchIndex);
        }

        internal ShipFamilyPowerScoreBreakdown GetCurrentShipPowerBreakdown()
        {
            if (currentShip == null || CardShopSystem.Instance == null)
                return default;
            return CardShopSystem.Instance.GetPowerScoreBreakdownForChassisIdAtShipLevel(
                currentShip.CurrentChassisId, currentShip.ShipLevel);
        }

        internal void OnCurrentShipDisplayNodeClicked()
        {
            if (currentShip == null)
                return;
            OnUpgradeTreeNodeClicked(currentShip.ShipLevel, currentShip.BranchIndex);
        }

        /// <summary>
        /// Handles upgrade purchase, same-tier hull swap, or debug-free selection. Routes to ECS RPC or legacy Netcode RPC.
        /// </summary>
        internal void OnUpgradeTreeNodeClicked(int nodeLevel, int targetBranchIndex)
        {
            Planet storePlanet = GetShipUpgradeStorePlanet();
            if (currentShip == null || storePlanet == null || CardShopSystem.Instance == null) return;

            var shipNo = currentShip.GetComponent<Unity.Netcode.NetworkObject>();
            var planetNo = storePlanet.GetComponent<Unity.Netcode.NetworkObject>();
            bool ecsPath = OrbitStationEcsContext.UseEcsStoreRpc
                || shipNo == null
                || planetNo == null
                || !shipNo.IsSpawned
                || !planetNo.IsSpawned;

            if (ecsPath)
            {
                if (IsDebugFreeShipUpgradeTree())
                {
                    if (nodeLevel == currentShip.ShipLevel && targetBranchIndex == currentShip.BranchIndex)
                        return;

                    int storePlanetId = OrbitStationEcsContext.StorePlanetId;
                    if (storePlanetId <= 0)
                        storePlanetId = _ecsStorePlanetId;

                    MoonOrbitRpcClient.PurchaseShipUpgrade(storePlanetId, nodeLevel, targetBranchIndex);
                    // Optimistic UI update — ClientWorld ghost may lag the Local Host server apply.
                    if (currentShip != null)
                    {
                        currentShip.ShipLevel = nodeLevel;
                        currentShip.BranchIndex = targetBranchIndex;
                    }
                    RefreshShipTreeAfterShipChange();
                    return;
                }

                if (nodeLevel == currentShip.ShipLevel + 1)
                {
                    int storePlanetId = OrbitStationEcsContext.StorePlanetId;
                    if (storePlanetId <= 0)
                        storePlanetId = _ecsStorePlanetId;

                    MoonOrbitRpcClient.PurchaseShipUpgrade(storePlanetId, nodeLevel, targetBranchIndex);
                    pendingGemsRequest = true;
                    if (HomePlanetStoreSystem.Instance != null)
                        HomePlanetStoreSystem.Instance.RequestContributedGemsServerRpc();
                    return;
                }

                if (nodeLevel == currentShip.ShipLevel && targetBranchIndex == currentShip.BranchIndex)
                {
                    if (!CardShopSystem.Instance.CanSwapShipAtSameTreeSlot(
                            currentShip, storePlanet, nodeLevel, targetBranchIndex, out _))
                        return;
                    MoonOrbitRpcClient.PurchaseShipUpgrade(
                        OrbitStationEcsContext.StorePlanetId, nodeLevel, targetBranchIndex);
                }

                return;
            }

            if (shipNo == null || !shipNo.IsSpawned) return;
            if (planetNo == null || !planetNo.IsSpawned) return;

            if (IsDebugFreeShipUpgradeTree())
            {
                if (nodeLevel == currentShip.ShipLevel && targetBranchIndex == currentShip.BranchIndex)
                    return;

                var nm = Unity.Netcode.NetworkManager.Singleton;
                bool shopReady = CardShopSystem.Instance.IsSpawned
                    || (nm != null && nm.IsServer && nm.IsListening);
                if (!shopReady)
                {
                    Debug.LogWarning("OrbitStationUI: CardShopSystem is not ready — start a networked game (host) to change ships.");
                    return;
                }

                CardShopSystem.Instance.RequestDebugSelectUpgradeTreeNode(
                    planetNo.NetworkObjectId, shipNo.NetworkObjectId, nodeLevel, targetBranchIndex);

                if (nm != null && nm.IsServer)
                    RefreshShipTreeAfterShipChange();
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
