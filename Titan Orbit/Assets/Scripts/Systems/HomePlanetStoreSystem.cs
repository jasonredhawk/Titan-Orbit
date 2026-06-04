using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Entities;
using TitanOrbit.Core;
using TitanOrbit.Data;

namespace TitanOrbit.Systems
{
    /// <summary>
    /// Handles Home Planet store: purchase validation (contributed gems), equipping drones/rockets/mines into ship equipment slots.
    /// </summary>
    public class HomePlanetStoreSystem : NetworkBehaviour
    {
        public static HomePlanetStoreSystem Instance { get; private set; }

        [Header("Store - Prefabs (assign in editor)")]
        [SerializeField] private GameObject fighterDronePrefab;
        [SerializeField] private GameObject shieldDronePrefab;
        [SerializeField] private GameObject miningDronePrefab;
        [Tooltip("Network prefab for drones dropped on ship death (LootableDrone + NetworkObject).")]
        [SerializeField] private GameObject lootableDroneNetworkPrefab;

        public GameObject FighterDronePrefab => fighterDronePrefab;
        public GameObject ShieldDronePrefab => shieldDronePrefab;
        public GameObject MiningDronePrefab => miningDronePrefab;
        public GameObject LootableDroneNetworkPrefab => lootableDroneNetworkPrefab;

        private void Awake()
        {
            if (Instance == null) Instance = this;
            else Destroy(gameObject);
        }

        /// <summary>Server: get contributed gems for a client at their team's home planet.</summary>
        public float GetContributedGemsForClient(ulong clientId)
        {
            if (!IsServer) return 0f;
            TeamManager.Team team = TeamManager.Instance != null ? TeamManager.Instance.GetPlayerTeam(clientId) : TeamManager.Team.None;
            if (team == TeamManager.Team.None) return 0f;
            HomePlanet home = GetHomePlanetForTeam(team);
            return home != null ? home.GetContributedGems(clientId) : 0f;
        }

        [ServerRpc(RequireOwnership = false)]
        public void RequestContributedGemsServerRpc(ServerRpcParams rpcParams = default)
        {
            ulong clientId = rpcParams.Receive.SenderClientId;
            float gems = GetContributedGemsForClient(clientId);
            var par = new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = new ulong[] { clientId } } };
            ResponseContributedGemsClientRpc(gems, par);
        }

        [ClientRpc]
        public void ResponseContributedGemsClientRpc(float gems, ClientRpcParams rpcParams = default)
        {
            TitanOrbit.UI.HomePlanetStoreUI.OnContributedGemsReceived(gems);
            TitanOrbit.UI.OrbitStationUI.OnContributedGemsReceived(gems);
        }

        /// <summary>Server: purchase item. Deducts from contributed gems and adds item to an equipment slot on the player's ship.</summary>
        [ServerRpc(RequireOwnership = false)]
        public void PurchaseItemServerRpc(ulong homePlanetNetworkId, ulong shipNetworkId, StoreItemType itemType, ServerRpcParams rpcParams = default)
        {
            ulong clientId = rpcParams.Receive.SenderClientId;
            NetworkObject homeNet = GetNetworkObject(homePlanetNetworkId);
            HomePlanet home = homeNet != null ? homeNet.GetComponent<HomePlanet>() : null;
            if (home == null || home.AssignedTeam == TeamManager.Team.None) return;
            if (TeamManager.Instance == null || TeamManager.Instance.GetPlayerTeam(clientId) != home.AssignedTeam) return;

            NetworkObject shipNet = GetNetworkObject(shipNetworkId);
            Starship ship = shipNet != null ? shipNet.GetComponent<Starship>() : null;
            if (ship == null || ship.OwnerClientId != clientId) return;
            if (!ship.HasEmptyEquipmentSlot) return;
            if (StoreItemData.IsShipComponent(itemType)) return;

            float cost = StoreItemData.GetPrice(itemType);
            if (!home.TrySpendContributedGems(clientId, cost)) return;

            if (!ship.AddEquipmentFromServer(itemType))
            {
                home.RefundContributedGems(clientId, cost);
                return;
            }

            NotifyPurchaseClientRpc(clientId, itemType, new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = new ulong[] { clientId } } });
        }

        /// <summary>Server: purchase a ship-family component into an equipment slot.</summary>
        [ServerRpc(RequireOwnership = false)]
        public void PurchaseComponentServerRpc(ulong homePlanetNetworkId, ulong shipNetworkId, string componentId, ServerRpcParams rpcParams = default)
        {
            if (string.IsNullOrWhiteSpace(componentId)) return;
            ulong clientId = rpcParams.Receive.SenderClientId;
            NetworkObject homeNet = GetNetworkObject(homePlanetNetworkId);
            HomePlanet home = homeNet != null ? homeNet.GetComponent<HomePlanet>() : null;
            if (home == null || home.AssignedTeam == TeamManager.Team.None) return;
            if (TeamManager.Instance == null || TeamManager.Instance.GetPlayerTeam(clientId) != home.AssignedTeam) return;

            NetworkObject shipNet = GetNetworkObject(shipNetworkId);
            Starship ship = shipNet != null ? shipNet.GetComponent<Starship>() : null;
            if (ship == null || ship.OwnerClientId != clientId) return;
            if (!ship.HasEmptyEquipmentSlot) return;
            if (ship.HasComponentEquipped(componentId)) return;

            ShipFamilyDefinition family = CardShopSystem.Instance != null
                ? CardShopSystem.Instance.GetShipFamilyForShip(ship)
                : null;
            if (family == null || !family.TryGetComponentEntry(componentId, out ShipFamilyComponentEntry componentEntry) || componentEntry == null)
                return;

            float cost = ShipComponentStoreData.GetComponentGemPrice(componentEntry, ship.ShipLevel);
            if (!home.TrySpendContributedGems(clientId, cost)) return;

            if (!ship.AddComponentEquipmentFromServer(componentId))
            {
                home.RefundContributedGems(clientId, cost);
                return;
            }

            NotifyComponentPurchaseClientRpc(clientId, componentId, new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = new ulong[] { clientId } } });
        }

        /// <summary>Legacy hook after snapshot restore; swarm visuals rebuild from equipment list automatically.</summary>
        public void RespawnEquipmentDronesForShip(Starship ship)
        {
            ship?.DroneSwarm?.OnStarshipNetworkSpawn();
        }

        [ClientRpc]
        private void NotifyPurchaseClientRpc(ulong clientId, StoreItemType itemType, ClientRpcParams rpcParams = default)
        {
            // Optional: play sound / UI feedback
        }

        [ClientRpc]
        private void NotifyComponentPurchaseClientRpc(ulong clientId, string componentId, ClientRpcParams rpcParams = default)
        {
        }

        public enum DroneType { Fighter, Shield, Mining }

        private HomePlanet GetHomePlanetForTeam(TeamManager.Team team)
        {
            foreach (var hp in HomePlanet.AllHomePlanets)
                if (hp != null && hp.AssignedTeam == team) return hp;
            return null;
        }
    }
}
