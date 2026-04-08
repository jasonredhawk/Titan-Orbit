using System;
using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Data;
using TitanOrbit.Entities;
using TitanOrbit.Core;

namespace TitanOrbit.Systems
{
    /// <summary>
    /// Planet-aware shop system for purchasing ships (chassis) and upgrade cards.
    /// Home planets can sell the full unlocked collection; captured planets sell their unique family.
    /// Uses contributed gems (same currency as the existing HomePlanetStoreSystem).
    /// </summary>
    public class CardShopSystem : NetworkBehaviour
    {
        public static CardShopSystem Instance { get; private set; }

        [Header("Data")]
        [Tooltip("Planet-to-ship-family mapping. Prefabs, unlock tiers, and upgrade card decks come from each entry's ShipFamilyDefinition.")]
        [SerializeField] private PlanetShipFamilyConfig planetShipFamilyConfig;

        /// <summary>Server-only: last spin offer per ship (NetworkObject id). Client uses <see cref="ClientSpinOfferCardIds"/>.</summary>
        private readonly Dictionary<ulong, PendingCardSpin> _pendingCardSpins = new Dictionary<ulong, PendingCardSpin>();

        private sealed class PendingCardSpin
        {
            public string[] CardIds = new string[3];
        }

        /// <summary>Last card IDs received from a spin (client). Empty strings mean no offer in that slot.</summary>
        private readonly string[] _clientSpinOfferCardIds = new string[3];

        /// <summary>Fires on the purchasing client after a spin card is equipped — offer is cleared so the UI can show empty slots until the next spin.</summary>
        public static event Action ClientSpinOfferConsumed;

        private void Awake()
        {
            if (Instance == null)
            {
                Instance = this;
            }
            else             if (Instance != this)
            {
                Destroy(gameObject);
            }
        }

        /// <summary>Ship family for upgrade cards: from <see cref="Starship.CurrentChassisId"/> prefix, else starter chassis.</summary>
        private ShipFamilyDefinition TryResolveFamilyForShip(Starship ship)
        {
            if (planetShipFamilyConfig == null) return null;
            string cid = ship != null ? ship.CurrentChassisId : null;
            if (string.IsNullOrEmpty(cid))
                cid = GetStarterChassisId();
            return planetShipFamilyConfig.GetShipFamilyDefinitionForChassisId(cid);
        }

        #region Query helpers (server-side)

        /// <summary>
        /// Returns the list of chassis that are unlocked for the given home planet level.
        /// When planetShipFamilyConfig is set, use GetUnlockedChassisEntriesForHomeLevel with store context for planet-specific ships.
        /// </summary>
        public List<ShipChassisDefinition> GetUnlockedChassisForHomeLevel(int homePlanetLevel)
        {
            if (planetShipFamilyConfig == null) return new List<ShipChassisDefinition>();
            return planetShipFamilyConfig.GetUnlockedChassis(homePlanetLevel);
        }

        /// <summary>Returns chassis definitions for the store (home or captured planet). Use when planetShipFamilyConfig is set.</summary>
        public List<ShipChassisDefinition> GetUnlockedChassisForStore(int homePlanetLevel, bool isHomeStore, int storePlanetId)
        {
            var entries = GetUnlockedChassisEntriesForHomeLevel(homePlanetLevel, isHomeStore, storePlanetId);
            var result = new List<ShipChassisDefinition>();
            foreach (var e in entries)
            {
                if (e?.chassis != null) result.Add(e.chassis);
            }
            return result;
        }

        /// <summary>
        /// Returns unlock entries (chassis + tier + cost) for the given home planet level and store planet.
        /// Uses PlanetShipFamilyConfig for each planet's ShipFamilyDefinition upgrade tree.
        /// </summary>
        public List<ShipUnlockEntry> GetUnlockedChassisEntriesForHomeLevel(int homePlanetLevel, bool isHomeStore, int storePlanetId)
        {
            if (planetShipFamilyConfig == null) return new List<ShipUnlockEntry>();
            return planetShipFamilyConfig.GetUnlockedEntriesForPlanet(homePlanetLevel, storePlanetId);
        }

        /// <summary>Returns the chassis at the given index for the home planet (planet 0). Returns null if config missing or index invalid.</summary>
        public ShipChassisDefinition GetChassisByIndex(int index)
        {
            return planetShipFamilyConfig != null ? planetShipFamilyConfig.GetChassisByIndex(0, index) : null;
        }

        /// <summary>Returns the ship prefab for the given chassis index. Resolved from PlanetShipFamilyConfig upgrade tree (chassis.basePrefab or GetPrefabForChassisId).</summary>
        public GameObject GetShipPrefabForChassisIndex(int index)
        {
            if (index < 0 || planetShipFamilyConfig == null) return null;
            ShipChassisDefinition chassis = GetChassisByIndex(index);
            if (chassis == null) return null;
            if (chassis.basePrefab != null) return chassis.basePrefab;
            return planetShipFamilyConfig.GetPrefabForChassisId(chassis.chassisId);
        }

        /// <summary>Returns the ship prefab for the given chassis ID. Resolved from PlanetShipFamilyConfig upgrade trees.</summary>
        public GameObject GetShipPrefabForChassisId(string chassisId)
        {
            if (string.IsNullOrEmpty(chassisId) || planetShipFamilyConfig == null) return null;
            GameObject prefab = planetShipFamilyConfig.GetPrefabForChassisId(chassisId);
            if (prefab != null) return prefab;
            int index = planetShipFamilyConfig.GetIndexForChassisId(chassisId);
            return GetShipPrefabForChassisIndex(index);
        }

        /// <summary>Returns the starter chassis ID (home planet's first ship). From PlanetShipFamilyConfig, else AstroEagle_01.</summary>
        public string GetStarterChassisId()
        {
            if (planetShipFamilyConfig != null && planetShipFamilyConfig.families != null && planetShipFamilyConfig.families.Count > 0)
            {
                string id = planetShipFamilyConfig.GetChassisIdForPlanetAndIndex(0, 0);
                if (!string.IsNullOrEmpty(id)) return id;
            }
            return "AstroEagle_01";
        }

        /// <summary>Chassis ID at (level, branch) from the ship family's <c>upgradeTree</c> ladder (matches orbit upgrade tree layout).</summary>
        public string GetChassisIdForUpgradeLadderSlot(Starship ship, int storePlanetId, int level, int branchIndex)
        {
            if (planetShipFamilyConfig == null || ship == null) return null;
            string cid = ship.CurrentChassisId;
            if (!string.IsNullOrEmpty(cid))
                return planetShipFamilyConfig.GetChassisIdForLadderSlotForShip(cid, storePlanetId, level, branchIndex);
            return planetShipFamilyConfig.GetChassisIdForLadderSlot(storePlanetId, level, branchIndex);
        }

        /// <summary>Resolves a runtime chassis definition for display / naming.</summary>
        public ShipChassisDefinition GetChassisDefinitionByChassisId(string chassisId)
        {
            return planetShipFamilyConfig != null ? planetShipFamilyConfig.GetChassisByChassisId(chassisId) : null;
        }

        /// <summary>2D menu thumbnail for a chassis. Prefers team-specific previews when available.</summary>
        public Sprite GetMenuPreviewSpriteForChassisId(string chassisId, TeamManager.Team team = TeamManager.Team.None)
        {
            return planetShipFamilyConfig != null ? planetShipFamilyConfig.GetMenuPreviewSpriteForChassisId(chassisId, team) : null;
        }

        /// <summary>Player-facing upgrade tree name from <see cref="ShipFamilyChassisTierEntry.upgradeTreeShipName"/>, or null if unset.</summary>
        public string GetUpgradeTreeShipNameForChassisId(string chassisId)
        {
            return planetShipFamilyConfig != null ? planetShipFamilyConfig.GetUpgradeTreeShipNameForChassisId(chassisId) : null;
        }

        /// <summary>Heuristic power breakdown for a chassis tier (editor-built upgrade tree).</summary>
        public ShipFamilyPowerScoreBreakdown GetPowerScoreBreakdownForChassisId(string chassisId)
        {
            return planetShipFamilyConfig != null ? planetShipFamilyConfig.GetPowerScoreBreakdownForChassisId(chassisId) : default;
        }

        /// <summary>Power breakdown for the ship that would be unlocked at the given tree slot.</summary>
        public ShipFamilyPowerScoreBreakdown GetPowerScoreBreakdownForUpgradeSlot(Starship ship, int storePlanetId, int level, int branchIndex)
        {
            string cid = GetChassisIdForUpgradeLadderSlot(ship, storePlanetId, level, branchIndex);
            if (string.IsNullOrEmpty(cid)) return default;
            return GetPowerScoreBreakdownForChassisId(cid);
        }

        /// <summary>Upgrade tree display name for a ladder slot (resolves chassis, then tier name).</summary>
        public string GetUpgradeTreeShipNameForUpgradeSlot(Starship ship, int storePlanetId, int level, int branchIndex)
        {
            string cid = GetChassisIdForUpgradeLadderSlot(ship, storePlanetId, level, branchIndex);
            if (string.IsNullOrEmpty(cid)) return null;
            return GetUpgradeTreeShipNameForChassisId(cid);
        }

        /// <summary>Menu thumbnail for an upgrade-tree slot (resolves chassis from ladder, then sprite). Prefers team-specific previews when available.</summary>
        public Sprite GetMenuPreviewSpriteForUpgradeSlot(Starship ship, int storePlanetId, int level, int branchIndex, TeamManager.Team team = TeamManager.Team.None)
        {
            string cid = GetChassisIdForUpgradeLadderSlot(ship, storePlanetId, level, branchIndex);
            if (string.IsNullOrEmpty(cid)) return null;
            return GetMenuPreviewSpriteForChassisId(cid, team);
        }

        /// <summary>Returns true if the ship can purchase a level upgrade via UpgradeTree and/or family upgrade tree chassis entries.</summary>
        public bool CanPurchaseShipLevelUpgrade(Starship ship, Planet storePlanet, out int nextLevel, out float cost, out string chassisId)
        {
            nextLevel = 0;
            cost = 0f;
            chassisId = null;
            if (ship == null || storePlanet == null) return false;
            if (ship.ShipLevel >= 7) return false;

            HomePlanet homePlanet = GetHomePlanetForTeam(ship.ShipTeam);
            if (homePlanet == null) return false;
            int homeLevel = homePlanet.HomePlanetLevel;
            nextLevel = ship.ShipLevel + 1;
            if (nextLevel > homeLevel) return false; // Planet level gates ship level

            bool isHome = storePlanet is HomePlanet hp && hp.AssignedTeam == ship.ShipTeam;
            bool isCaptured = !isHome && storePlanet.TeamOwnership == ship.ShipTeam;
            if (!isHome && !isCaptured) return false;

            UpgradeTree tree = UpgradeSystem.Instance != null ? UpgradeSystem.Instance.UpgradeTree : null;
            if (tree == null) return false;

            int storePlanetId = storePlanet.PlanetId;
            var available = tree.GetAvailableUpgrades(ship.ShipLevel, ship.BranchIndex);
            bool hasPath = available != null && available.Count > 0;
            if (!hasPath)
            {
                var targets = new List<int>(4);
                UpgradeTree.GetNextLevelBranchTargets(ship.ShipLevel, ship.BranchIndex, targets);
                for (int t = 0; t < targets.Count; t++)
                {
                    int j = targets[t];
                    chassisId = GetChassisIdForUpgradeLadderSlot(ship, storePlanetId, nextLevel, j);
                    if (!string.IsNullOrEmpty(chassisId))
                    {
                        hasPath = true;
                        break;
                    }
                }
                if (!hasPath) return false;
            }

            cost = tree.GetGemCostForLevel(nextLevel);
            return cost > 0f;
        }

        /// <summary>
        /// Returns all cards that are allowed at the given home planet level and that match the origin planet filter.
        /// If originPlanetId is 0, returns global/home cards; if positive, returns cards bound to that planet.
        /// Pool comes from the <paramref name="ship"/>'s <see cref="ShipFamilyDefinition"/> upgrade card deck.
        /// </summary>
        public List<CardData> GetAvailableCardsForPlanet(Starship ship, int homePlanetLevel, int originPlanetIdFilter)
        {
            var result = new List<CardData>();
            var family = TryResolveFamilyForShip(ship);
            if (family == null) return result;

            foreach (var card in family.GetUpgradeCards())
            {
                if (card == null) continue;
                if (homePlanetLevel < card.minHomePlanetLevel) continue;

                // originPlanetIdFilter == 0 => include global/home cards (originPlanetId <= 0)
                // originPlanetIdFilter > 0  => include cards whose originPlanetId matches that planet
                if (originPlanetIdFilter > 0)
                {
                    if (card.originPlanetId != originPlanetIdFilter) continue;
                }
                else
                {
                    if (card.originPlanetId > 0) continue;
                }

                result.Add(card);
            }

            return result;
        }

        private static HashSet<int> _cachedOwnedPlanetIds = new HashSet<int>();
        private static TeamManager.Team _cachedOwnedPlanetTeam = TeamManager.Team.None;
        private static float _lastOwnedPlanetsRefresh = -999f;
        private const float OwnedPlanetsCacheInterval = 1.5f;

        /// <summary>
        /// Returns all cards that should appear in the home planet store for the given team:
        /// - Global/home cards (originPlanetId &lt;= 0)
        /// - Plus cards whose originPlanetId matches any planet currently owned by that team.
        /// Pool comes from the <paramref name="ship"/>'s ship family deck.
        /// </summary>
        public List<CardData> GetAvailableCardsForHomeStore(Starship ship, int homePlanetLevel, TeamManager.Team team)
        {
            var result = new List<CardData>();
            var family = TryResolveFamilyForShip(ship);
            if (family == null) return result;

            if (team != TeamManager.Team.None && (team != _cachedOwnedPlanetTeam || Time.time - _lastOwnedPlanetsRefresh >= OwnedPlanetsCacheInterval))
            {
                _cachedOwnedPlanetTeam = team;
                _lastOwnedPlanetsRefresh = Time.time;
                _cachedOwnedPlanetIds.Clear();
                foreach (var planet in Planet.AllPlanets)
                {
                    if (planet == null) continue;
                    if (planet.TeamOwnership == team && planet.PlanetId > 0)
                        _cachedOwnedPlanetIds.Add(planet.PlanetId);
                }
            }
            var ownedPlanetIds = team != TeamManager.Team.None ? _cachedOwnedPlanetIds : new HashSet<int>();

            foreach (var card in family.GetUpgradeCards())
            {
                if (card == null) continue;
                if (homePlanetLevel < card.minHomePlanetLevel) continue;

                if (card.originPlanetId <= 0 || ownedPlanetIds.Contains(card.originPlanetId))
                    result.Add(card);
            }

            return result;
        }

        /// <summary>
        /// Card tier for a spin: matches the ship’s level, capped by home planet level (so a level 1 ship always draws level 1 cards).
        /// </summary>
        public static int GetSpinCardTier(int shipLevel, int homePlanetLevel)
        {
            int s = Mathf.Max(1, shipLevel);
            int h = Mathf.Max(1, homePlanetLevel);
            return Mathf.Min(s, h);
        }

        /// <summary>Gem cost for one spin at this card tier (matches upgrade-tree tier pricing).</summary>
        public float GetCardSpinCost(int spinCardTier)
        {
            int t = Mathf.Max(1, spinCardTier);
            int gemLevel = Mathf.Clamp(t + 1, 2, 24);
            if (UpgradeSystem.Instance != null && UpgradeSystem.Instance.UpgradeTree != null)
                return Mathf.Max(15f, UpgradeSystem.Instance.UpgradeTree.GetGemCostForLevel(gemLevel));
            float n = gemLevel - 1f;
            return Mathf.Max(15f, 20f * n * n);
        }

        /// <summary>
        /// Cards for a spin: store availability uses <paramref name="homePlanetLevel"/>; drawn cards match <paramref name="spinCardTier"/>.
        /// Deck is the <paramref name="ship"/>'s family deck.
        /// </summary>
        public List<CardData> GetCardPoolForSpin(Starship ship, int spinCardTier, int homePlanetLevel, bool isHomeStore, int storePlanetId, TeamManager.Team team)
        {
            var pool = new List<CardData>();
            int tier = Mathf.Max(1, spinCardTier);
            int home = Mathf.Max(1, homePlanetLevel);
            List<CardData> baseList = isHomeStore
                ? GetAvailableCardsForHomeStore(ship, home, team)
                : GetAvailableCardsForPlanet(ship, home, storePlanetId);
            for (int i = 0; i < baseList.Count; i++)
            {
                CardData c = baseList[i];
                if (c == null) continue;
                if (Mathf.Max(1, c.cardLevel) != tier) continue;
                pool.Add(c);
            }
            return pool;
        }

        /// <summary>Count of cards in the spin pool without allocating a new list.</summary>
        public int GetCardPoolCountForSpin(Starship ship, int spinCardTier, int homePlanetLevel, bool isHomeStore, int storePlanetId, TeamManager.Team team)
        {
            int tier = Mathf.Max(1, spinCardTier);
            int home = Mathf.Max(1, homePlanetLevel);
            List<CardData> baseList = isHomeStore
                ? GetAvailableCardsForHomeStore(ship, home, team)
                : GetAvailableCardsForPlanet(ship, home, storePlanetId);
            int n = 0;
            for (int i = 0; i < baseList.Count; i++)
            {
                CardData c = baseList[i];
                if (c == null) continue;
                if (Mathf.Max(1, c.cardLevel) != tier) continue;
                n++;
            }
            return n;
        }

        public string GetClientSpinOfferCardId(int index)
        {
            if ((uint)index >= 3u) return null;
            string s = _clientSpinOfferCardIds[index];
            return string.IsNullOrEmpty(s) ? null : s;
        }

        private static int GetRarityWeight(int rarity)
        {
            // Balanced rarity drop rates (applied at rarity-selection stage):
            // Common 50%, Uncommon 27%, Rare 14%, Epic 7%, Legendary 2%.
            if (rarity <= 1) return 50;
            if (rarity == 2) return 27;
            if (rarity == 3) return 14;
            if (rarity == 4) return 7;
            return 2;
        }

        private static CardData PickOneWeighted(List<CardData> pool, System.Random rng)
        {
            if (pool == null || pool.Count == 0) return null;

            // Stage 1: roll rarity by target distribution.
            int totalRarityWeight = 0;
            for (int rarity = 1; rarity <= 5; rarity++)
                totalRarityWeight += GetRarityWeight(rarity);
            int rarityRoll = rng.Next(0, Mathf.Max(1, totalRarityWeight));
            int selectedRarity = 1;
            int rarityAcc = 0;
            for (int rarity = 1; rarity <= 5; rarity++)
            {
                rarityAcc += GetRarityWeight(rarity);
                if (rarityRoll < rarityAcc)
                {
                    selectedRarity = rarity;
                    break;
                }
            }

            // Stage 2: choose uniformly among cards in that rarity.
            int matches = 0;
            for (int i = 0; i < pool.Count; i++)
            {
                if ((int)pool[i].rarity == selectedRarity)
                    matches++;
            }
            if (matches > 0)
            {
                int pick = rng.Next(matches);
                int seen = 0;
                for (int i = 0; i < pool.Count; i++)
                {
                    if ((int)pool[i].rarity != selectedRarity) continue;
                    if (seen == pick) return pool[i];
                    seen++;
                }
            }

            // Fallback if this tier has no cards at selected rarity.
            return pool[rng.Next(pool.Count)];
        }

        /// <summary>Server: pay spin cost, roll three weighted random cards for this planet tier, store pending offer for <see cref="PurchaseCardServerRpc"/>.</summary>
        [ServerRpc(RequireOwnership = false)]
        public void CardSpinServerRpc(ulong planetNetworkId, ulong shipNetworkId, ServerRpcParams rpcParams = default)
        {
            ulong clientId = rpcParams.Receive.SenderClientId;

            NetworkObject planetNet = GetNetworkObject(planetNetworkId);
            Planet planet = planetNet != null ? planetNet.GetComponent<Planet>() : null;
            if (planet == null) return;

            NetworkObject shipNet = GetNetworkObject(shipNetworkId);
            Starship ship = shipNet != null ? shipNet.GetComponent<Starship>() : null;
            if (ship == null || ship.OwnerClientId != clientId) return;

            HomePlanet homePlanet = GetHomePlanetForTeam(ship.ShipTeam);
            if (homePlanet == null) return;
            int homeLevel = Mathf.Max(1, homePlanet.HomePlanetLevel);
            int spinTier = GetSpinCardTier(ship.ShipLevel, homeLevel);

            bool isHome = planet is HomePlanet hp && hp.AssignedTeam == ship.ShipTeam;
            bool isCapturedPlanet = !isHome && planet.TeamOwnership == ship.ShipTeam;
            if (!isHome && !isCapturedPlanet) return;

            int storePlanetId = planet.PlanetId;
            List<CardData> pool = GetCardPoolForSpin(ship, spinTier, homeLevel, isHome, storePlanetId, ship.ShipTeam);
            if (pool == null || pool.Count == 0)
            {
                NotifyCardSpinResultClientRpc(string.Empty, string.Empty, string.Empty, shipNetworkId, new ClientRpcParams
                {
                    Send = new ClientRpcSendParams { TargetClientIds = new[] { clientId } }
                });
                return;
            }

            float spinCost = GetCardSpinCost(spinTier);
            if (!homePlanet.TrySpendContributedGems(clientId, spinCost))
                return;

            var rng = new System.Random((int)(DateTime.UtcNow.Ticks ^ (long)shipNetworkId ^ (long)clientId));
            CardData p0 = PickOneWeighted(pool, rng);
            CardData p1 = PickOneWeighted(pool, rng);
            CardData p2 = PickOneWeighted(pool, rng);
            string a = p0 != null ? p0.cardId : string.Empty;
            string b = p1 != null ? p1.cardId : string.Empty;
            string c = p2 != null ? p2.cardId : string.Empty;

            if (!_pendingCardSpins.ContainsKey(shipNetworkId))
                _pendingCardSpins[shipNetworkId] = new PendingCardSpin();
            PendingCardSpin pend = _pendingCardSpins[shipNetworkId];
            pend.CardIds[0] = a;
            pend.CardIds[1] = b;
            pend.CardIds[2] = c;

            NotifyCardSpinResultClientRpc(a, b, c, shipNetworkId, new ClientRpcParams
            {
                Send = new ClientRpcSendParams { TargetClientIds = new[] { clientId } }
            });
        }

        [ClientRpc]
        private void NotifyCardSpinResultClientRpc(string a, string b, string c, ulong _, ClientRpcParams rpcParams = default)
        {
            _clientSpinOfferCardIds[0] = a ?? string.Empty;
            _clientSpinOfferCardIds[1] = b ?? string.Empty;
            _clientSpinOfferCardIds[2] = c ?? string.Empty;
        }

        #endregion

        #region Purchases

        /// <summary>
        /// Server: take one card from the current spin offer and add it to the ship (spin already paid for the pull).
        /// </summary>
        [ServerRpc(RequireOwnership = false)]
        public void PurchaseCardServerRpc(ulong planetNetworkId, ulong shipNetworkId, string cardId, ServerRpcParams rpcParams = default)
        {
            ulong clientId = rpcParams.Receive.SenderClientId;
            if (string.IsNullOrEmpty(cardId)) return;

            NetworkObject planetNet = GetNetworkObject(planetNetworkId);
            Planet planet = planetNet != null ? planetNet.GetComponent<Planet>() : null;
            if (planet == null) return;

            NetworkObject shipNet = GetNetworkObject(shipNetworkId);
            Starship ship = shipNet != null ? shipNet.GetComponent<Starship>() : null;
            if (ship == null || ship.OwnerClientId != clientId) return;

            HomePlanet homePlanet = GetHomePlanetForTeam(ship.ShipTeam);
            if (homePlanet == null) return;
            int homeLevel = homePlanet.HomePlanetLevel;

            CardData card = GetCardByIdForShip(ship, cardId);
            if (card == null) return;
            if (homeLevel < card.minHomePlanetLevel) return;

            if (!ship.HasEmptySlot) return;

            int cardLvl = Mathf.Max(1, card.cardLevel);
            if (cardLvl > ship.ShipLevel) return;

            int originPlanetId = card.originPlanetId;
            bool isHome = planet is HomePlanet hp && hp.AssignedTeam == ship.ShipTeam;
            bool isCapturedPlanet = !isHome && planet.TeamOwnership == ship.ShipTeam;

            if (isHome)
            {
                if (originPlanetId > 0 && !TeamOwnsPlanetId(ship.ShipTeam, originPlanetId))
                    return;
            }
            else if (isCapturedPlanet)
            {
                if (originPlanetId != planet.PlanetId)
                    return;
            }
            else
            {
                return;
            }

            if (!_pendingCardSpins.TryGetValue(shipNetworkId, out PendingCardSpin pend) || pend?.CardIds == null)
                return;
            bool inOffer = false;
            for (int i = 0; i < pend.CardIds.Length; i++)
            {
                if (!string.IsNullOrEmpty(pend.CardIds[i]) && pend.CardIds[i] == cardId)
                {
                    inOffer = true;
                    break;
                }
            }
            if (!inOffer) return;

            _pendingCardSpins.Remove(shipNetworkId);

            ship.AddCardFromServer(card);
            NotifyCardPurchasedClientRpc(cardId, shipNetworkId, new ClientRpcParams
            {
                Send = new ClientRpcSendParams { TargetClientIds = new[] { clientId } }
            });
        }

        /// <summary>
        /// Server: purchase a chassis (ship) at the given planet, using contributed gems.
        /// </summary>
        [ServerRpc(RequireOwnership = false)]
        public void PurchaseChassisServerRpc(ulong planetNetworkId, ulong shipNetworkId, string chassisId, int tierLevel, ServerRpcParams rpcParams = default)
        {
            ulong clientId = rpcParams.Receive.SenderClientId;
            if (string.IsNullOrEmpty(chassisId)) return;

            NetworkObject shipNet = GetNetworkObject(shipNetworkId);
            Starship ship = shipNet != null ? shipNet.GetComponent<Starship>() : null;
            if (ship == null || ship.OwnerClientId != clientId) return;

            HomePlanet homePlanet = GetHomePlanetForTeam(ship.ShipTeam);
            if (homePlanet == null) return;
            int homeLevel = homePlanet.HomePlanetLevel;

            NetworkObject planetNet = GetNetworkObject(planetNetworkId);
            Planet planet = planetNet != null ? planetNet.GetComponent<Planet>() : null;
            if (planet == null) return;

            bool isHome = planet is HomePlanet hp && hp.AssignedTeam == ship.ShipTeam;
            int storePlanetId = planet.PlanetId;

            ShipChassisDefinition chassis = FindChassisById(chassisId);
            int chassisIndex = planetShipFamilyConfig != null ? planetShipFamilyConfig.GetIndexForChassisIdForPlanet(chassisId, storePlanetId) : -1;
            if (chassisIndex < 0)
                chassisIndex = ParseChassisIndexFromId(chassisId);

            if (planetShipFamilyConfig != null)
            {
                if (chassisIndex < 0) return;
                if (homeLevel < (chassis?.minHomePlanetLevel ?? tierLevel)) return;
            }
            else
            {
                if (chassis == null) return;
                if (homeLevel < chassis.minHomePlanetLevel) return;
                bool isOriginPlanet = chassis.originPlanetId > 0 && storePlanetId == chassis.originPlanetId;
                if (!isHome && !isOriginPlanet) return;
            }

            bool canPurchase = isHome || (planet.TeamOwnership == ship.ShipTeam);
            if (!canPurchase) return;

            float baseCost = ShipUnlockTable.GetTierCost(tierLevel);
            float cost = Mathf.Max(baseCost, 20f);

            if (!homePlanet.TrySpendContributedGems(clientId, cost))
                return;

            if (chassis?.baseShipData != null)
            {
                ship.SetShipData(chassis.baseShipData);
            }
            else
            {
                ship.SetShipLevelFromTier(tierLevel);
                GameObject prefab = GetShipPrefabForChassisId(chassisId);
                if (prefab != null)
                    ship.ApplyShipVisualFromPrefab(prefab);
                else
                    Debug.LogWarning($"CardShopSystem: No prefab for chassis '{chassisId}'. Ensure PlanetShipFamilyConfig has an entry for this planet with a ShipFamilyDefinition whose Upgrade Tree has prefabs assigned.");
            }
            ship.SetCurrentChassisIndex(chassisIndex);
            ship.SetCurrentChassisId(chassisId);
            ship.ResetAttributesOnlyFromServer();
            ship.RefillCombatVitalsToMaxFromServer();

            NotifyChassisPurchasedClientRpc(chassisId, chassisIndex, shipNetworkId, new ClientRpcParams
            {
                Send = new ClientRpcSendParams { TargetClientIds = new[] { clientId } }
            });
        }

        /// <summary>
        /// Server: purchase a level upgrade for the current ship and selected target branch node in the UpgradeTree.
        /// Uses contributed gems.
        /// </summary>
        [ServerRpc(RequireOwnership = false)]
        public void PurchaseShipLevelUpgradeServerRpc(ulong planetNetworkId, ulong shipNetworkId, int targetBranchIndex, ServerRpcParams rpcParams = default)
        {
            ulong clientId = rpcParams.Receive.SenderClientId;

            NetworkObject shipNet = GetNetworkObject(shipNetworkId);
            Starship ship = shipNet != null ? shipNet.GetComponent<Starship>() : null;
            if (ship == null || ship.OwnerClientId != clientId) return;

            NetworkObject planetNet = GetNetworkObject(planetNetworkId);
            Planet planet = planetNet != null ? planetNet.GetComponent<Planet>() : null;
            if (planet == null) return;

            if (!CanPurchaseShipLevelUpgrade(ship, planet, out int nextLevel, out float cost, out _))
                return;

            int p = ship.BranchIndex;
            if (!UpgradeTree.IsValidUpgradeStep(ship.ShipLevel, p, nextLevel, targetBranchIndex)) return;

            UpgradeTree tree = UpgradeSystem.Instance != null ? UpgradeSystem.Instance.UpgradeTree : null;
            if (tree == null) return;

            int storePlanetId = planet.PlanetId;
            ShipUpgradeNode targetNode = tree.GetNodeForBranch(nextLevel, targetBranchIndex);
            string resolvedChassisId = GetChassisIdForUpgradeLadderSlot(ship, storePlanetId, nextLevel, targetBranchIndex);
            if (targetNode == null && string.IsNullOrEmpty(resolvedChassisId))
                return;

            HomePlanet homePlanet = GetHomePlanetForTeam(ship.ShipTeam);
            if (homePlanet == null) return;
            if (!homePlanet.TrySpendContributedGems(clientId, cost))
                return;

            if (targetNode != null && targetNode.shipData != null)
            {
                ship.SetShipData(targetNode.shipData);
            }
            else if (!string.IsNullOrEmpty(resolvedChassisId))
            {
                ShipData baseData = ship.CurrentShipData;
                ShipData runtime = baseData != null ? Instantiate(baseData) : ScriptableObject.CreateInstance<ShipData>();
                runtime.shipLevel = nextLevel;
                runtime.branchIndex = targetBranchIndex;
                runtime.shipPrefab = null;
                runtime.shipName = resolvedChassisId;
                ShipChassisDefinition chassisDef = planetShipFamilyConfig != null ? planetShipFamilyConfig.GetChassisByChassisId(resolvedChassisId) : null;
                if (chassisDef != null && !string.IsNullOrEmpty(chassisDef.displayName))
                    runtime.shipName = chassisDef.displayName;
                ship.SetShipData(runtime);
                ship.SetCurrentChassisId(resolvedChassisId);
                int chassisIndex = planetShipFamilyConfig != null ? planetShipFamilyConfig.GetIndexForChassisIdForPlanet(resolvedChassisId, storePlanetId) : -1;
                if (chassisIndex < 0)
                    chassisIndex = ParseChassisIndexFromId(resolvedChassisId);
                ship.SetCurrentChassisIndex(chassisIndex);
                GameObject prefab = GetShipPrefabForChassisId(resolvedChassisId);
                if (prefab != null)
                    ship.ApplyShipVisualFromPrefab(prefab);
                ship.ResetAttributesOnlyFromServer();
            }
            else
                return;

            ship.RefillCombatVitalsToMaxFromServer();

            string chassisIdForClientVisual = (targetNode != null && targetNode.shipData != null) ? null : resolvedChassisId;
            NotifyShipLevelUpgradedClientRpc(shipNetworkId, nextLevel, chassisIdForClientVisual, new ClientRpcParams
            {
                Send = new ClientRpcSendParams { TargetClientIds = new[] { clientId } }
            });
        }

        #endregion

        #region Client notifications

        [ClientRpc]
        private void NotifyCardPurchasedClientRpc(string cardId, ulong shipNetworkId, ClientRpcParams rpcParams = default)
        {
            _clientSpinOfferCardIds[0] = string.Empty;
            _clientSpinOfferCardIds[1] = string.Empty;
            _clientSpinOfferCardIds[2] = string.Empty;
            ClientSpinOfferConsumed?.Invoke();
        }

        [ClientRpc]
        private void NotifyChassisPurchasedClientRpc(string chassisId, int chassisIndex, ulong shipNetworkId, ClientRpcParams rpcParams = default)
        {
            NetworkObject shipNet = GetNetworkObject(shipNetworkId);
            Starship ship = shipNet != null ? shipNet.GetComponent<Starship>() : null;
            if (ship == null) return;
            GameObject prefab = GetShipPrefabForChassisId(chassisId);
            if (prefab != null)
                ship.ApplyShipVisualFromPrefab(prefab);
        }

        [ClientRpc]
        private void NotifyShipLevelUpgradedClientRpc(ulong shipNetworkId, int newLevel, string chassisIdForVisual, ClientRpcParams rpcParams = default)
        {
            if (string.IsNullOrEmpty(chassisIdForVisual)) return;
            NetworkObject shipNet = GetNetworkObject(shipNetworkId);
            Starship ship = shipNet != null ? shipNet.GetComponent<Starship>() : null;
            if (ship == null) return;
            GameObject prefab = GetShipPrefabForChassisId(chassisIdForVisual);
            if (prefab != null)
                ship.ApplyShipVisualFromPrefab(prefab);
        }

        #endregion

        #region Helpers

        /// <summary>Resolves a card on the given ship's family deck first, then searches all configured families.</summary>
        public CardData GetCardByIdForShip(Starship ship, string cardId)
        {
            if (string.IsNullOrEmpty(cardId)) return null;
            var family = TryResolveFamilyForShip(ship);
            if (family != null)
            {
                foreach (var card in family.GetUpgradeCards())
                {
                    if (card != null && card.cardId == cardId) return card;
                }
            }
            return GetCardById(cardId);
        }

        /// <summary>Public lookup across every <see cref="ShipFamilyDefinition"/> in <see cref="planetShipFamilyConfig"/> (e.g. client UI when ship context is unclear).</summary>
        public CardData GetCardById(string cardId)
        {
            if (string.IsNullOrEmpty(cardId) || planetShipFamilyConfig?.families == null) return null;
            foreach (var entry in planetShipFamilyConfig.families)
            {
                if (entry?.shipFamilyDefinition == null) continue;
                foreach (var card in entry.shipFamilyDefinition.GetUpgradeCards())
                {
                    if (card != null && card.cardId == cardId) return card;
                }
            }
            return null;
        }

        private ShipChassisDefinition FindChassisById(string chassisId)
        {
            if (planetShipFamilyConfig != null)
            {
                ShipChassisDefinition chassis = planetShipFamilyConfig.GetChassisByChassisId(chassisId);
                if (chassis != null) return chassis;
            }
            return null;
        }

        private HomePlanet GetHomePlanetForTeam(TeamManager.Team team)
        {
            if (team == TeamManager.Team.None) return null;
            foreach (var home in HomePlanet.AllHomePlanets)
            {
                if (home != null && home.AssignedTeam == team) return home;
            }
            return null;
        }

        private static int ParseChassisIndexFromId(string chassisId)
        {
            if (string.IsNullOrEmpty(chassisId)) return -1;
            int idx = chassisId.LastIndexOf('_');
            if (idx < 0) return -1;
            string numPart = chassisId.Substring(idx + 1).TrimStart('0');
            if (string.IsNullOrEmpty(numPart)) numPart = "1";
            if (!int.TryParse(numPart, out int num) || num < 1 || num > 20) return -1;
            return num - 1;
        }

        private bool TeamOwnsPlanetId(TeamManager.Team team, int planetId)
        {
            if (team == TeamManager.Team.None || planetId <= 0) return false;
            foreach (var planet in Planet.AllPlanets)
            {
                if (planet != null && planet.PlanetId == planetId && planet.TeamOwnership == team)
                    return true;
            }
            return false;
        }

        #endregion
    }
}

