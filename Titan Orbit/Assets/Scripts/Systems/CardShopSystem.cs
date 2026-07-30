using System;
using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Entities;
using TitanOrbit.Game;
using TitanOrbit.UI;
using UnityEngine;

namespace TitanOrbit.Systems
{
    /// <summary>Legacy card/ship shop for OrbitStationUI; purchases delegate to ECS RPCs.</summary>
    public class CardShopSystem : MonoBehaviour
    {
        public static CardShopSystem Instance { get; private set; }

        public bool IsSpawned => true;

        public static event Action ClientSpinOfferReceived;
        public static event Action ClientSpinOfferConsumed;

        /// <summary>Raises <see cref="ClientSpinOfferReceived"/> for ECS / Local Host spin results.</summary>
        public static void RaiseClientSpinOfferReceived() => ClientSpinOfferReceived?.Invoke();

        /// <summary>Raises <see cref="ClientSpinOfferConsumed"/> after take-card clears the offer.</summary>
        public static void RaiseClientSpinOfferConsumed() => ClientSpinOfferConsumed?.Invoke();

        PlanetShipFamilyConfig _planetShipFamilyConfig;

        void Awake()
        {
            // --- Unity lifecycle ---
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            // [UNITY] Do not Resources.Load the family config here. Loading pulls every chassis
            // prefab reference and spam-logs "referenced script (Unknown)" for any missing
            // MonoBehaviour on those prefabs — that Console flood hurts Editor frame time.
            // Config is resolved lazily via ShipStatApplyLogic (same asset, one shared cache).
        }

        void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        /// <summary>
        /// Shared PlanetShipFamilyConfig — same Resources asset ShipStatApplyLogic uses for motor stats.
        /// </summary>
        PlanetShipFamilyConfig Config
        {
            get
            {
                if (_planetShipFamilyConfig != null)
                    return _planetShipFamilyConfig;

                // [TITAN-ORBIT] Prefer the ECS apply cache so shop UI and motor MaxSpeed stay aligned.
                _planetShipFamilyConfig = ShipStatApplyLogic.Config;
                if (_planetShipFamilyConfig != null)
                    return _planetShipFamilyConfig;

                _planetShipFamilyConfig = Resources.Load<PlanetShipFamilyConfig>("PlanetShipFamilyConfig");
                return _planetShipFamilyConfig;
            }
        }

        public ShipFamilyDefinition GetShipFamilyForShip(Starship ship)
        {
            // --- Compute value ---
            if (Config == null || ship == null)
                return null;
            string cid = ship.CurrentChassisId;
            if (string.IsNullOrEmpty(cid))
                cid = GetStarterChassisId();
            return Config.GetShipFamilyDefinitionForChassisId(cid);
        }

        public string GetStarterChassisId()
        {
            // --- Compute value ---
            if (Config?.families != null && Config.families.Count > 0)
            {
                string id = Config.GetChassisIdForPlanetAndIndex(0, 0);
                if (!string.IsNullOrEmpty(id))
                    return id;
            }

            return "AstroEagle_01";
        }

        public string GetChassisIdForUpgradeLadderSlot(Starship ship, int storePlanetId, int level, int branchIndex)
        {
            // --- Compute value ---
            if (Config == null || ship == null)
                return null;

            ResolveStorePlanetFamily(storePlanetId, ship, out bool isHomePlanet, out int configIndex);
            return Config.GetChassisIdForLadderSlot(storePlanetId, level, branchIndex, isHomePlanet, configIndex);
        }

        public ShipFamilyDefinition GetShipFamilyForStorePlanet(int storePlanetId, Starship ship = null)
        {
            // --- Compute value ---
            if (Config == null || storePlanetId <= 0)
                return null;

            ResolveStorePlanetFamily(storePlanetId, ship, out bool isHomePlanet, out int configIndex);
            return Config.GetFamilyForPlanet(storePlanetId, isHomePlanet, configIndex)?.shipFamilyDefinition;
        }

        static void ResolveStorePlanetFamily(int storePlanetId, Starship ship, out bool isHomePlanet, out int configIndex)
        {
            // --- Resolve value ---
            isHomePlanet = false;
            configIndex = -1;

            if (EcsGameBridge.TryGetPlanetStateByPlanetId(storePlanetId, out PlanetState state))
            {
                isHomePlanet = state.IsHomePlanet;
                if (state.IsHomePlanet)
                    configIndex = PlanetShipFamilyAssignment.HomeFamilyConfigIndex;
                else if (state.ShipFamilyConfigIndex > 0)
                    configIndex = state.ShipFamilyConfigIndex;
                return;
            }

            if (ship != null)
            {
                foreach (var home in HomePlanet.AllHomePlanets)
                {
                    if (home != null && home.PlanetId == storePlanetId && home.AssignedTeam == ship.ShipTeam)
                    {
                        isHomePlanet = true;
                        configIndex = PlanetShipFamilyAssignment.HomeFamilyConfigIndex;
                        return;
                    }
                }
            }
        }

        public ShipChassisDefinition GetChassisDefinitionByChassisId(string chassisId)
        {
            return Config != null ? Config.GetChassisByChassisId(chassisId) : null;
        }

        public Sprite GetMenuPreviewSpriteForChassisId(string chassisId, TeamManager.Team team = TeamManager.Team.None)
        {
            return Config != null ? Config.GetMenuPreviewSpriteForChassisId(chassisId, team) : null;
        }

        public string GetUpgradeTreeShipNameForChassisId(string chassisId)
        {
            return Config != null ? Config.GetUpgradeTreeShipNameForChassisId(chassisId) : null;
        }

        public ShipFamilyPowerScoreBreakdown GetPowerScoreBreakdownForChassisId(string chassisId)
        {
            return Config != null ? Config.GetPowerScoreBreakdownForChassisId(chassisId) : default;
        }

        public int GetPurchaseGemCostForChassisId(string chassisId, int shipLevel)
        {
            return Config != null ? Config.GetPurchaseGemCostForChassisId(chassisId, shipLevel) : 0;
        }

        public ShipFamilyPowerScoreBreakdown GetPowerScoreBreakdownForUpgradeSlot(
            Starship ship, int storePlanetId, int level, int branchIndex)
        {
            string cid = GetChassisIdForUpgradeLadderSlot(ship, storePlanetId, level, branchIndex);
            return string.IsNullOrEmpty(cid) ? default : GetPowerScoreBreakdownForChassisId(cid);
        }

        public string GetUpgradeTreeShipNameForUpgradeSlot(Starship ship, int storePlanetId, int level, int branchIndex)
        {
            string cid = GetChassisIdForUpgradeLadderSlot(ship, storePlanetId, level, branchIndex);
            return string.IsNullOrEmpty(cid) ? null : GetUpgradeTreeShipNameForChassisId(cid);
        }

        public Sprite GetMenuPreviewSpriteForUpgradeSlot(
            Starship ship, int storePlanetId, int level, int branchIndex, TeamManager.Team team = TeamManager.Team.None)
        {
            string cid = GetChassisIdForUpgradeLadderSlot(ship, storePlanetId, level, branchIndex);
            return string.IsNullOrEmpty(cid) ? null : GetMenuPreviewSpriteForChassisId(cid, team);
        }

        public int GetPurchaseGemCostForUpgradeSlot(Starship ship, int storePlanetId, int level, int branchIndex)
        {
            string cid = GetChassisIdForUpgradeLadderSlot(ship, storePlanetId, level, branchIndex);
            return string.IsNullOrEmpty(cid) ? 0 : GetPurchaseGemCostForChassisId(cid, level);
        }

        public bool CanPurchaseShipLevelUpgrade(Starship ship, Planet storePlanet, out int nextLevel, out float cost, out string chassisId)
        {
            // --- CanPurchaseShipLevelUpgrade ---
            nextLevel = 0;
            cost = 0f;
            chassisId = null;
            if (ship == null || storePlanet == null)
                return false;
            if (ship.ShipLevel >= 7)
                return false;

            HomePlanet homePlanet = FindHomePlanetForTeam(ship.ShipTeam);
            if (homePlanet == null)
                return false;

            int homeLevel = homePlanet.HomePlanetLevel;
            nextLevel = ship.ShipLevel + 1;
            if (nextLevel > homeLevel)
                return false;

            bool isHome = storePlanet is HomePlanet hp && hp.AssignedTeam == ship.ShipTeam;
            bool isCaptured = !isHome && storePlanet.TeamOwnership == ship.ShipTeam;
            if (!isHome && !isCaptured)
                return false;

            if (nextLevel > storePlanet.PlanetLevel)
                return false;

            int storePlanetId = storePlanet.PlanetId;
            var targets = new List<int>(4);
            UpgradeTree.GetNextLevelBranchTargets(ship.ShipLevel, ship.BranchIndex, targets);
            for (int i = 0; i < targets.Count; i++)
            {
                chassisId = GetChassisIdForUpgradeLadderSlot(ship, storePlanetId, nextLevel, targets[i]);
                if (!string.IsNullOrEmpty(chassisId))
                    break;
            }

            if (string.IsNullOrEmpty(chassisId))
                return false;

            cost = GetPurchaseGemCostForChassisId(chassisId, nextLevel);
            return true;
        }

        public bool CanSwapShipAtSameTreeSlot(
            Starship ship, Planet storePlanet, int targetLevel, int targetBranchIndex, out string chassisId)
        {
            chassisId = null;
            if (ship == null || storePlanet == null)
                return false;
            if (targetLevel != ship.ShipLevel || targetBranchIndex != ship.BranchIndex)
                return false;

            HomePlanet homePlanet = FindHomePlanetForTeam(ship.ShipTeam);
            if (homePlanet == null || targetLevel > homePlanet.HomePlanetLevel)
                return false;

            bool isHome = storePlanet is HomePlanet hp && hp.AssignedTeam == ship.ShipTeam;
            bool isCaptured = !isHome && storePlanet.TeamOwnership == ship.ShipTeam;
            if (!isHome && !isCaptured)
                return false;

            if (storePlanet.PlanetLevel < targetLevel)
                return false;

            chassisId = GetChassisIdForUpgradeLadderSlot(ship, storePlanet.PlanetId, targetLevel, targetBranchIndex);
            if (string.IsNullOrEmpty(chassisId))
                return false;

            string current = ship.CurrentChassisId;
            return string.IsNullOrEmpty(current)
                || !string.Equals(chassisId, current, StringComparison.OrdinalIgnoreCase);
        }

        public void PurchaseShipLevelUpgradeServerRpc(ulong planetNetworkId, ulong shipNetworkId, int targetBranchIndex)
        {
            // --- PurchaseShipLevelUpgradeServerRpc ---
            int storePlanetId = OrbitStationEcsContext.StorePlanetId;
            if (storePlanetId <= 0)
                return;

            int targetLevel = OrbitStationEcsContext.ShipLevel + 1;
            MoonOrbitRpcClient.PurchaseShipUpgrade(storePlanetId, targetLevel, targetBranchIndex);
            MoonOrbitRpcClient.RequestContributedGems(OrbitStationEcsContext.HomePlanetId);
        }

        public void SwapShipAtSameTreeSlotServerRpc(
            ulong planetNetworkId, ulong shipNetworkId, int targetLevel, int targetBranchIndex)
        {
            int storePlanetId = OrbitStationEcsContext.StorePlanetId;
            if (storePlanetId <= 0)
                return;
            MoonOrbitRpcClient.PurchaseShipUpgrade(storePlanetId, targetLevel, targetBranchIndex);
            MoonOrbitRpcClient.RequestContributedGems(OrbitStationEcsContext.HomePlanetId);
        }

        public void RequestDebugSelectUpgradeTreeNode(ulong planetNetworkId, ulong shipNetworkId, int nodeLevel, int targetBranchIndex)
        {
            PurchaseShipLevelUpgradeServerRpc(planetNetworkId, shipNetworkId, targetBranchIndex);
        }

        public static int GetSpinCardTier(int shipLevel, int homePlanetLevel)
        {
            // --- Compute value ---
            int s = Mathf.Max(1, shipLevel);
            int h = Mathf.Max(1, homePlanetLevel);
            return Mathf.Min(s, h);
        }

        public float GetCardSpinCost(int spinCardTier)
        {
            int gemLevel = Mathf.Clamp(Mathf.Max(1, spinCardTier), 1, 24);
            return Mathf.Max(15f, 20f * gemLevel * gemLevel);
        }

        public int GetCardPoolCountForSpin(
            Starship ship, int spinCardTier, int homePlanetLevel, bool isHomeStore, int storePlanetId, TeamManager.Team team)
        {
            return GetCardPoolForSpin(ship, spinCardTier, homePlanetLevel, isHomeStore, storePlanetId, team).Count;
        }

        public List<CardData> GetCardPoolForSpin(
            Starship ship, int spinCardTier, int homePlanetLevel, bool isHomeStore, int storePlanetId, TeamManager.Team team)
        {
            var pool = new List<CardData>();
            var family = GetShipFamilyForShip(ship);
            if (family == null)
                return pool;

            int tier = Mathf.Max(1, spinCardTier);
            foreach (var card in family.GetUpgradeCards())
            {
                if (card == null || Mathf.Max(1, card.cardLevel) != tier)
                    continue;
                if (homePlanetLevel < card.minHomePlanetLevel)
                    continue;
                pool.Add(card);
            }

            return pool;
        }

        public string GetClientSpinOfferCardId(int index) => MoonOrbitClientState.GetSpinOfferCardId(index);

        public CardData GetCardByIdForShip(Starship ship, string cardId)
        {
            // --- GetCardByIdForShip ---
            if (string.IsNullOrEmpty(cardId))
                return null;

            var family = GetShipFamilyForShip(ship);
            if (family != null)
            {
                var fromFamily = ShipStatApplyLogic.FindCardInFamily(family, cardId);
                if (fromFamily != null)
                    return fromFamily;
            }

            return ShipStatApplyLogic.FindCardAnywhere(cardId);
        }

        public void CardSpinServerRpc(ulong planetNetworkId, ulong shipNetworkId)
        {
            // --- CardSpinServerRpc ---
            int storePlanetId = OrbitStationEcsContext.StorePlanetId;
            if (storePlanetId <= 0)
                return;

            MoonOrbitRpcClient.CardSpin(storePlanetId);
            MoonOrbitRpcClient.RequestContributedGems(OrbitStationEcsContext.HomePlanetId);
        }

        public void PurchaseCardServerRpc(ulong planetNetworkId, ulong shipNetworkId, string cardId)
        {
            // --- PurchaseCardServerRpc ---
            int storePlanetId = OrbitStationEcsContext.StorePlanetId;
            if (storePlanetId <= 0 || string.IsNullOrEmpty(cardId))
                return;

            MoonOrbitRpcClient.TakeSpinCard(storePlanetId, cardId);
        }

        public void PurchaseChassisServerRpc(ulong planetNetworkId, ulong shipNetworkId, string chassisId, int tierLevel)
        {
            Debug.LogWarning("[CardShopSystem] Chassis purchase via legacy RPC is not wired to ECS yet.");
        }

        static HomePlanet FindHomePlanetForTeam(TeamManager.Team team)
        {
            // --- FindHomePlanetForTeam ---
            foreach (var home in HomePlanet.AllHomePlanets)
            {
                if (home != null && home.AssignedTeam == team)
                    return home;
            }

            return null;
        }
    }
}
