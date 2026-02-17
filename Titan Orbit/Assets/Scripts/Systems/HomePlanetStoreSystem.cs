using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Entities;
using TitanOrbit.Core;
using TitanOrbit.Data;

namespace TitanOrbit.Systems
{
    /// <summary>
    /// Handles Home Planet store: purchase validation (contributed gems), spawning drones, adding rockets/mines to ship.
    /// </summary>
    public class HomePlanetStoreSystem : NetworkBehaviour
    {
        public static HomePlanetStoreSystem Instance { get; private set; }

        [Header("Store - Prefabs (assign in editor)")]
        [SerializeField] private GameObject fighterDronePrefab;
        [SerializeField] private GameObject shieldDronePrefab;
        [SerializeField] private GameObject miningDronePrefab;

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
        }

        /// <summary>Server: purchase item. Deducts from contributed gems and grants item to the player's ship.</summary>
        [ServerRpc(RequireOwnership = false)]
        public void PurchaseItemServerRpc(ulong homePlanetNetworkId, ulong shipNetworkId, StoreItemType itemType, ServerRpcParams rpcParams = default)
        {
            ulong clientId = rpcParams.Receive.SenderClientId;
            NetworkObject homeNet = GetNetworkObject(homePlanetNetworkId);
            HomePlanet home = homeNet != null ? homeNet.GetComponent<HomePlanet>() : null;
            if (home == null || home.AssignedTeam == TeamManager.Team.None) return;
            if (TeamManager.Instance == null || TeamManager.Instance.GetPlayerTeam(clientId) != home.AssignedTeam) return;

            float cost = StoreItemData.GetPrice(itemType);
            if (!home.TrySpendContributedGems(clientId, cost)) return;

            NetworkObject shipNet = GetNetworkObject(shipNetworkId);
            Starship ship = shipNet != null ? shipNet.GetComponent<Starship>() : null;
            if (ship == null || ship.OwnerClientId != clientId) return;

            switch (itemType)
            {
                case StoreItemType.FighterDrone:
                    SpawnDroneForShip(ship, fighterDronePrefab, DroneType.Fighter);
                    break;
                case StoreItemType.ShieldDrone:
                    SpawnDroneForShip(ship, shieldDronePrefab, DroneType.Shield);
                    break;
                case StoreItemType.MiningDrone:
                    SpawnDroneForShip(ship, miningDronePrefab, DroneType.Mining);
                    break;
                case StoreItemType.SmallRockets:
                    ship.AddSmallRocketsServerRpc(StoreItemData.GetPackSize(itemType));
                    break;
                case StoreItemType.LargeRockets:
                    ship.AddLargeRocketsServerRpc(StoreItemData.GetPackSize(itemType));
                    break;
                case StoreItemType.SmallMines:
                    ship.AddSmallMinesServerRpc(StoreItemData.GetPackSize(itemType));
                    break;
                case StoreItemType.LargeMines:
                    ship.AddLargeMinesServerRpc(StoreItemData.GetPackSize(itemType));
                    break;
            }

            NotifyPurchaseClientRpc(clientId, itemType, new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = new ulong[] { clientId } } });
        }

        [ClientRpc]
        private void NotifyPurchaseClientRpc(ulong clientId, StoreItemType itemType, ClientRpcParams rpcParams = default)
        {
            // Optional: play sound / UI feedback
        }

        public enum DroneType { Fighter, Shield, Mining }

        private void SpawnDroneForShip(Starship ship, GameObject prefab, DroneType droneType)
        {
            if (prefab == null || ship == null) return;
            Vector3 pos = ship.transform.position + ship.transform.forward * 2f;
            GameObject go = Instantiate(prefab, pos, Quaternion.identity);
            var drone = go.GetComponent<DroneBase>();
            if (drone != null)
                drone.SetOwnerShip(ship);
            var no = go.GetComponent<NetworkObject>();
            if (no != null) no.Spawn();
        }

        private HomePlanet GetHomePlanetForTeam(TeamManager.Team team)
        {
            foreach (var hp in Object.FindObjectsByType<HomePlanet>(FindObjectsSortMode.None))
                if (hp.AssignedTeam == team) return hp;
            return null;
        }
    }
}
