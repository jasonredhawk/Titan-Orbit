using Unity.Netcode;
using UnityEngine;

namespace TitanOrbit.Networking
{
    /// <summary>
    /// Prevents Netcode from auto-spawning the player prefab on connect. The server spawns the ship when the player joins a team.
    /// </summary>
    public static class DeferredPlayerShipSpawn
    {
        public static void Configure(NetworkManager networkManager)
        {
            if (networkManager == null)
                return;

            networkManager.NetworkConfig.ConnectionApproval = true;
            networkManager.ConnectionApprovalCallback = OnConnectionApproval;
        }

        private static void OnConnectionApproval(
            NetworkManager.ConnectionApprovalRequest request,
            NetworkManager.ConnectionApprovalResponse response)
        {
            response.Approved = true;
            response.CreatePlayerObject = false;
            response.PlayerPrefabHash = null;
            response.Position = null;
            response.Rotation = null;
        }

        /// <summary>Server: spawn the configured player prefab as this client's player object if not already present.</summary>
        public static bool TrySpawnForClient(ulong clientId)
        {
            var nm = NetworkGameManager.ResolveNetworkManagerForGameplay() ?? NetworkManager.Singleton;
            if (nm == null || !nm.IsServer)
                return false;

            if (nm.SpawnManager == null)
                return false;

            if (nm.SpawnManager.GetPlayerNetworkObject(clientId) != null)
                return true;

            if (!nm.ConnectedClients.ContainsKey(clientId))
                return false;

            GameObject prefab = nm.NetworkConfig.PlayerPrefab;
            if (prefab == null)
            {
                Debug.LogError("[DeferredPlayerShipSpawn] PlayerPrefab is not set on NetworkManager.");
                return false;
            }

            GameObject instance = Object.Instantiate(prefab);
            NetworkObject netObj = instance.GetComponent<NetworkObject>();
            if (netObj == null)
            {
                Object.Destroy(instance);
                Debug.LogError("[DeferredPlayerShipSpawn] Player prefab is missing NetworkObject.");
                return false;
            }

            netObj.SpawnAsPlayerObject(clientId);
            return netObj.IsSpawned;
        }
    }
}
