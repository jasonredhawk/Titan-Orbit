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
        [SerializeField] private ShipUnlockTable shipUnlockTable;
        [Tooltip("Assign PlanetShipFamilyConfig (from Assets/Data) so each planet shows its own ship collection. Create via Titan Orbit > Assign ModularExamples to Planet Ship Family Config.")]
        [SerializeField] private PlanetShipFamilyConfig planetShipFamilyConfig;
        [SerializeField] private List<CardData> allCards = new List<CardData>();
        [Tooltip("AstroEagle ship prefabs (1–20) from UltimateSpaceshipsCreator/Prefabs/ModularExamples/AstroEagle. Assign in inspector or use menu Titan Orbit > Assign AstroEagle Prefabs to CardShopSystem.")]
        [SerializeField] private GameObject[] astroEagleShipPrefabs = new GameObject[20];

        private void Awake()
        {
            if (Instance == null)
            {
                Instance = this;
            }
            else if (Instance != this)
            {
                Destroy(gameObject);
            }
            if (shipUnlockTable == null)
                shipUnlockTable = ScriptableObject.CreateInstance<ShipUnlockTable>();
            // Wire planet ship family config so each planet shows its own ship collection
            if (shipUnlockTable != null && planetShipFamilyConfig != null)
                shipUnlockTable.planetShipFamilyConfig = planetShipFamilyConfig;
            if (shipUnlockTable != null)
                shipUnlockTable.GetUnlockedEntries(1);
            if (allCards == null || allCards.Count == 0)
                allCards = GetDefaultCards();
        }

        /// <summary>Creates a runtime list of exactly 20 default cards for the shop. Used when no cards are assigned in the inspector.</summary>
        private static List<CardData> GetDefaultCards()
        {
            var list = new List<CardData>();
            int id = 0;

            CardData Add(string name, string desc, int level, int rar, float cost, SlotType slotType, float dmgMul = 1f, float gemAdd = 0f, float energyRegenAdd = 0f, float energyCapAdd = 0f, float healthAdd = 0f, float healthRegenAdd = 0f, float moveAdd = 0f, float rotAdd = 0f, float bulletSpeedMul = 1f)
            {
                var c = ScriptableObject.CreateInstance<CardData>();
                c.cardId = "card_" + (id++);
                c.displayName = name;
                c.description = desc;
                c.cardLevel = level;
                c.rarity = Mathf.Clamp(rar, 1, 4);
                c.slotType = slotType;
                c.minHomePlanetLevel = 1;
                c.originPlanetId = 0;
                c.gemCost = cost;
                c.damageMultiplier = dmgMul;
                c.gemCapacityAdd = gemAdd;
                c.energyRegenAdd = energyRegenAdd;
                c.energyCapacityAdd = energyCapAdd;
                c.maxHealthAdd = healthAdd;
                c.healthRegenAdd = healthRegenAdd;
                c.movementSpeedAdd = moveAdd;
                c.rotationSpeedAdd = rotAdd;
                c.bulletSpeedMultiplier = bulletSpeedMul;
                list.Add(c);
                return c;
            }

            // 20 distinct shop cards: variety of effects and levels (Weapon / Ship / Cargo slot types)
            Add("Weapon Damage", "+5% weapon damage.", 1, 1, 15f, SlotType.Weapon, dmgMul: 1.05f);
            Add("Gem Capacity", "+20 gem capacity.", 1, 1, 18f, SlotType.Cargo, gemAdd: 20f);
            Add("Energy Regen", "+1.5 energy/sec.", 1, 1, 12f, SlotType.Ship, energyRegenAdd: 1.5f);
            Add("Energy Capacity", "+10 energy capacity.", 1, 1, 14f, SlotType.Ship, energyCapAdd: 10f);
            Add("Max Health", "+15 max health.", 1, 1, 16f, SlotType.Ship, healthAdd: 15f);
            Add("Health Regen", "+0.3 health/sec.", 1, 1, 10f, SlotType.Ship, healthRegenAdd: 0.3f);
            Add("Movement Speed", "+0.5 move speed.", 1, 1, 12f, SlotType.Ship, moveAdd: 0.5f);
            Add("Rotation Speed", "+15 rotation speed.", 1, 1, 10f, SlotType.Ship, rotAdd: 15f);
            Add("Bullet Speed", "+6% bullet speed.", 1, 1, 11f, SlotType.Weapon, bulletSpeedMul: 1.06f);
            Add("Weapon Damage II", "+8% weapon damage.", 2, 1, 28f, SlotType.Weapon, dmgMul: 1.08f);
            Add("Gem Capacity II", "+38 gem capacity.", 2, 1, 32f, SlotType.Cargo, gemAdd: 38f);
            Add("Energy Regen II", "+3 energy/sec.", 2, 1, 24f, SlotType.Ship, energyRegenAdd: 3f);
            Add("Max Health II", "+28 max health.", 2, 1, 30f, SlotType.Ship, healthAdd: 28f);
            Add("Weapon Damage III", "+12% weapon damage.", 3, 1, 45f, SlotType.Weapon, dmgMul: 1.12f);
            Add("Gem Capacity III", "+55 gem capacity.", 3, 1, 52f, SlotType.Cargo, gemAdd: 55f);
            Add("Energy Regen III", "+4.5 energy/sec.", 3, 1, 40f, SlotType.Ship, energyRegenAdd: 4.5f);
            Add("Max Health III", "+45 max health.", 3, 1, 48f, SlotType.Ship, healthAdd: 45f);
            Add("Weapon Damage IV", "+18% weapon damage.", 4, 1, 65f, SlotType.Weapon, dmgMul: 1.18f);
            Add("Gem Capacity IV", "+80 gem capacity.", 4, 1, 75f, SlotType.Cargo, gemAdd: 80f);
            Add("Energy Regen IV", "+6 energy/sec.", 4, 1, 58f, SlotType.Ship, energyRegenAdd: 6f);

            return list;
        }

        #region Query helpers (server-side)

        /// <summary>
        /// Returns the list of chassis that are unlocked for the given home planet level.
        /// When planetShipFamilyConfig is set, use GetUnlockedChassisEntriesForHomeLevel with store context for planet-specific ships.
        /// </summary>
        public List<ShipChassisDefinition> GetUnlockedChassisForHomeLevel(int homePlanetLevel)
        {
            if (shipUnlockTable == null)
                shipUnlockTable = ScriptableObject.CreateInstance<ShipUnlockTable>();
            return shipUnlockTable.GetUnlockedChassis(homePlanetLevel);
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
        /// Uses planetShipFamilyConfig: home (planetId 0) = AstroEagle, planet 1 = CombinedSpaceships, etc.
        /// </summary>
        public List<ShipUnlockEntry> GetUnlockedChassisEntriesForHomeLevel(int homePlanetLevel, bool isHomeStore, int storePlanetId)
        {
            if (shipUnlockTable == null)
                shipUnlockTable = ScriptableObject.CreateInstance<ShipUnlockTable>();
            return shipUnlockTable.GetUnlockedEntriesForPlanet(homePlanetLevel, storePlanetId);
        }

        /// <summary>Returns the chassis at the given index in the unlock table (for grid dimensions). Returns null if index is invalid.</summary>
        public ShipChassisDefinition GetChassisByIndex(int index)
        {
            return shipUnlockTable != null ? shipUnlockTable.GetChassisByIndex(index) : null;
        }

        /// <summary>Returns the ship prefab for the given chassis index (AstroEagle_01 = index 0, etc.). Used so the first ship can apply AstroEagle1 visual on spawn.</summary>
        public GameObject GetShipPrefabForChassisIndex(int index)
        {
            if (index < 0) return null;
            ShipChassisDefinition chassis = GetChassisByIndex(index);
            if (chassis == null) return null;
            GameObject prefab = GetAstroEaglePrefabForChassisId(chassis.chassisId);
            if (prefab == null && astroEagleShipPrefabs != null && index < astroEagleShipPrefabs.Length)
                prefab = astroEagleShipPrefabs[index];
            return prefab;
        }

        /// <summary>Returns the ship prefab for the given chassis ID (e.g. AstroEagle_01, CraizanStar_05). Uses PlanetShipFamilyConfig when set.</summary>
        public GameObject GetShipPrefabForChassisId(string chassisId)
        {
            if (string.IsNullOrEmpty(chassisId)) return null;
            if (shipUnlockTable?.planetShipFamilyConfig != null)
            {
                GameObject prefab = shipUnlockTable.planetShipFamilyConfig.GetPrefabByChassisId(chassisId);
                if (prefab != null) return prefab;
            }
            int index = shipUnlockTable != null ? shipUnlockTable.GetIndexForChassisId(chassisId) : -1;
            return GetShipPrefabForChassisIndex(index);
        }

        /// <summary>Returns the starter chassis ID (home planet's first ship). AstroEagle_01 when no config, else first family's ship 1.</summary>
        public string GetStarterChassisId()
        {
            if (shipUnlockTable?.planetShipFamilyConfig != null && shipUnlockTable.planetShipFamilyConfig.families != null && shipUnlockTable.planetShipFamilyConfig.families.Count > 0)
            {
                return shipUnlockTable.planetShipFamilyConfig.GetChassisIdForPlanetAndIndex(0, 0);
            }
            return "AstroEagle_01";
        }

        /// <summary>Returns true if the ship can purchase a level upgrade (same ship family, next tier). Cost = same as other ships of that tier. Out params: nextLevel, cost, chassisId.</summary>
        public bool CanPurchaseShipLevelUpgrade(Starship ship, Planet storePlanet, out int nextLevel, out float cost, out string chassisId)
        {
            nextLevel = 0;
            cost = 0f;
            chassisId = null;
            if (ship == null || storePlanet == null || shipUnlockTable == null) return false;
            if (ship.ShipLevel >= 6) return false; // Level 7 is special (planet 6 + full gems) - not handled as simple purchase for now

            string currentCid = ship.CurrentChassisId;
            if (string.IsNullOrEmpty(currentCid)) return false;
            int usIdx = currentCid.IndexOf('_');
            if (usIdx <= 0) return false;
            string shipFamily = currentCid.Substring(0, usIdx);

            HomePlanet homePlanet = GetHomePlanetForTeam(ship.ShipTeam);
            if (homePlanet == null) return false;
            int homeLevel = homePlanet.HomePlanetLevel;
            nextLevel = ship.ShipLevel + 1;
            if (nextLevel > homeLevel) return false; // Planet level gates ship level

            bool isHome = storePlanet is HomePlanet hp && hp.AssignedTeam == ship.ShipTeam;
            bool isCaptured = !isHome && storePlanet.TeamOwnership == ship.ShipTeam;
            if (!isHome && !isCaptured) return false;

            int nextChassisIndex = ShipUnlockTable.GetFirstChassisIndexForTier(nextLevel);
            if (nextChassisIndex >= 20) return false;

            chassisId = $"{shipFamily}_{(nextChassisIndex + 1):D2}";

            cost = ShipUnlockTable.GetTierCost(nextLevel);
            return true;
        }

        /// <summary>
        /// Returns all cards that are allowed at the given home planet level and that match the origin planet filter.
        /// If originPlanetId is 0, returns global/home cards; if positive, returns cards bound to that planet.
        /// </summary>
        public List<CardData> GetAvailableCardsForPlanet(int homePlanetLevel, int originPlanetIdFilter)
        {
            var result = new List<CardData>();
            if (allCards == null) return result;

            foreach (var card in allCards)
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
        /// </summary>
        public List<CardData> GetAvailableCardsForHomeStore(int homePlanetLevel, TeamManager.Team team)
        {
            var result = new List<CardData>();
            if (allCards == null) return result;

            if (team != TeamManager.Team.None && (team != _cachedOwnedPlanetTeam || Time.time - _lastOwnedPlanetsRefresh >= OwnedPlanetsCacheInterval))
            {
                _cachedOwnedPlanetTeam = team;
                _lastOwnedPlanetsRefresh = Time.time;
                _cachedOwnedPlanetIds.Clear();
                foreach (var planet in FindObjectsOfType<Planet>())
                {
                    if (planet.TeamOwnership == team && planet.PlanetId > 0)
                        _cachedOwnedPlanetIds.Add(planet.PlanetId);
                }
            }
            var ownedPlanetIds = team != TeamManager.Team.None ? _cachedOwnedPlanetIds : new HashSet<int>();

            foreach (var card in allCards)
            {
                if (card == null) continue;
                if (homePlanetLevel < card.minHomePlanetLevel) continue;

                if (card.originPlanetId <= 0 || ownedPlanetIds.Contains(card.originPlanetId))
                    result.Add(card);
            }

            return result;
        }

        #endregion

        #region Purchases

        /// <summary>
        /// Server: purchase a card at the given planet for the given ship, using contributed gems.
        /// The client passes the cardId and the planetNetworkId where they are docked.
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

            // Determine home planet and home level for this team.
            HomePlanet homePlanet = GetHomePlanetForTeam(ship.ShipTeam);
            if (homePlanet == null) return;
            int homeLevel = homePlanet.HomePlanetLevel;

            // Lookup card.
            CardData card = FindCardById(cardId);
            if (card == null) return;
            if (homeLevel < card.minHomePlanetLevel) return;

            // Slots: 1 per ship level, 1 card per slot. Only allow purchase if ship has an empty slot.
            if (!ship.HasEmptySlot) return;

            // Card level: ship can only equip cards with level <= ship level.
            int cardLvl = Mathf.Max(1, card.cardLevel);
            if (cardLvl > ship.ShipLevel) return;

            // Planet gating: home planet sells all unlocked cards; captured planet sells its unique cards.
            int originPlanetId = card.originPlanetId;
            bool isHome = planet is HomePlanet hp && hp.AssignedTeam == ship.ShipTeam;
            bool isCapturedPlanet = !isHome && planet.TeamOwnership == ship.ShipTeam;

            if (isHome)
            {
                // At home we allow any unlocked card whose originPlanetId <= 0 (global) or belongs to a captured planet.
                if (originPlanetId > 0 && !TeamOwnsPlanetId(ship.ShipTeam, originPlanetId))
                    return;
            }
            else if (isCapturedPlanet)
            {
                // At captured planet: only that planet's own cards (by originPlanetId).
                if (originPlanetId != planet.PlanetId) // assumes Planet has PlanetId; otherwise this will be wired later.
                    return;
            }
            else
            {
                // Neutral or enemy planet: no purchases.
                return;
            }

            float cost = card.gemCost;
            if (cost <= 0f) cost = 20f;

            // Use contributed gems at the team's home planet as currency.
            if (!homePlanet.TrySpendContributedGems(clientId, cost))
                return;

            // Add card to the ship's server-side loadout. UI/grid placement will be layered on later.
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
            int chassisIndex = shipUnlockTable != null ? shipUnlockTable.GetIndexForChassisId(chassisId) : -1;

            if (shipUnlockTable?.planetShipFamilyConfig != null)
            {
                chassisIndex = ParseChassisIndexFromId(chassisId);
                if (chassisIndex < 0) return;
                if (homeLevel < tierLevel) return;
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
                if (prefab == null && chassisIndex >= 0 && chassisIndex < (astroEagleShipPrefabs?.Length ?? 0))
                    prefab = astroEagleShipPrefabs[chassisIndex];
                if (prefab != null)
                    ship.ApplyShipVisualFromPrefab(prefab);
                else
                    Debug.LogWarning($"CardShopSystem: No prefab for chassis '{chassisId}'. Run Titan Orbit > Assign ModularExamples to Planet Ship Family Config.");
            }
            ship.SetCurrentChassisIndex(chassisIndex);
            ship.SetCurrentChassisId(chassisId);
            ship.ResetAttributesOnlyFromServer();

            NotifyChassisPurchasedClientRpc(chassisId, chassisIndex, shipNetworkId, new ClientRpcParams
            {
                Send = new ClientRpcSendParams { TargetClientIds = new[] { clientId } }
            });
        }

        /// <summary>
        /// Server: purchase a level upgrade for the current ship (same family, next tier). Cost = same as other ships of that tier. Uses contributed gems.
        /// </summary>
        [ServerRpc(RequireOwnership = false)]
        public void PurchaseShipLevelUpgradeServerRpc(ulong planetNetworkId, ulong shipNetworkId, ServerRpcParams rpcParams = default)
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

            HomePlanet homePlanet = GetHomePlanetForTeam(ship.ShipTeam);
            if (homePlanet == null) return;
            if (!homePlanet.TrySpendContributedGems(clientId, cost))
                return;

            // Same ship: only increase level (more slots, higher capacity). Keep visual, chassis, cards, and attribute upgrades.
            ship.SetShipLevelFromTier(nextLevel);

            NotifyShipLevelUpgradedClientRpc(shipNetworkId, nextLevel, new ClientRpcParams
            {
                Send = new ClientRpcSendParams { TargetClientIds = new[] { clientId } }
            });
        }

        #endregion

        #region Client notifications

        [ClientRpc]
        private void NotifyCardPurchasedClientRpc(string cardId, ulong shipNetworkId, ClientRpcParams rpcParams = default)
        {
            // Hook for UI feedback (e.g. floating text, SFX).
        }

        [ClientRpc]
        private void NotifyChassisPurchasedClientRpc(string chassisId, int chassisIndex, ulong shipNetworkId, ClientRpcParams rpcParams = default)
        {
            NetworkObject shipNet = GetNetworkObject(shipNetworkId);
            Starship ship = shipNet != null ? shipNet.GetComponent<Starship>() : null;
            if (ship == null) return;
            GameObject prefab = GetShipPrefabForChassisId(chassisId);
            if (prefab == null && chassisIndex >= 0 && chassisIndex < (astroEagleShipPrefabs?.Length ?? 0))
                prefab = astroEagleShipPrefabs[chassisIndex];
            if (prefab != null)
                ship.ApplyShipVisualFromPrefab(prefab);
        }

        [ClientRpc]
        private void NotifyShipLevelUpgradedClientRpc(ulong shipNetworkId, int newLevel, ClientRpcParams rpcParams = default)
        {
            // Level is already synced via networkShipLevel. No visual change - same ship, more slots/abilities.
        }

        #endregion

        #region Helpers

        private CardData FindCardById(string cardId)
        {
            return GetCardById(cardId);
        }

        /// <summary>Public lookup for resolving card IDs to CardData (e.g. for client-side equipped card display).</summary>
        public CardData GetCardById(string cardId)
        {
            if (allCards == null || string.IsNullOrEmpty(cardId)) return null;
            foreach (var card in allCards)
            {
                if (card == null) continue;
                if (card.cardId == cardId) return card;
            }
            return null;
        }

        private ShipChassisDefinition FindChassisById(string chassisId)
        {
            if (shipUnlockTable == null || shipUnlockTable.entries == null) return null;
            foreach (var entry in shipUnlockTable.entries)
            {
                if (entry?.chassis == null) continue;
                if (entry.chassis.chassisId == chassisId)
                    return entry.chassis;
            }
            return null;
        }

        /// <summary>Resolves AstroEagle ship prefab from chassisId (e.g. AstroEagle_01 -> index 0 -> astroEagleShipPrefabs[0]). Falls back to Resources.Load if array not filled.</summary>
        private GameObject GetAstroEaglePrefabForChassisId(string chassisId)
        {
            if (string.IsNullOrEmpty(chassisId) || !chassisId.StartsWith("AstroEagle_")) return null;
            string numPart = chassisId.Substring("AstroEagle_".Length);
            if (!int.TryParse(numPart, out int num) || num < 1 || num > 20) return null;
            int index = num - 1;
            if (astroEagleShipPrefabs != null && index < astroEagleShipPrefabs.Length && astroEagleShipPrefabs[index] != null)
                return astroEagleShipPrefabs[index];
            GameObject fallback = Resources.Load<GameObject>($"AstroEagle/AstroEagle{num}");
            return fallback;
        }

        private HomePlanet GetHomePlanetForTeam(TeamManager.Team team)
        {
            if (team == TeamManager.Team.None) return null;
            foreach (var home in FindObjectsOfType<HomePlanet>())
            {
                if (home.AssignedTeam == team) return home;
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
            foreach (var planet in FindObjectsOfType<Planet>())
            {
                if (planet.PlanetId == planetId && planet.TeamOwnership == team)
                    return true;
            }
            return false;
        }

        #endregion
    }
}

