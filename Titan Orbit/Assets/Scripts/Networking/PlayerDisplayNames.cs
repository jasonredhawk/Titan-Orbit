using UnityEngine;
using Unity.Netcode;
using System.Collections.Generic;
using TitanOrbit.Data;

namespace TitanOrbit.Networking
{
    /// <summary>
    /// Holds display names per client. Clients send their name via ServerRpc; server syncs to all.
    /// </summary>
    public class PlayerDisplayNames : NetworkBehaviour
    {
        public static PlayerDisplayNames Instance { get; private set; }

        private readonly Dictionary<ulong, string> serverNames = new Dictionary<ulong, string>();
        private static readonly Dictionary<ulong, string> clientNames = new Dictionary<ulong, string>();

        private void Awake()
        {
            if (Instance == null) Instance = this;
            else if (Instance != this) Destroy(gameObject);
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        public override void OnNetworkSpawn()
        {
            if (IsClient)
            {
                string name = string.IsNullOrWhiteSpace(NetworkGameManager.LocalPlayerDisplayName)
                    ? GameNames.GetRandomPlayerName()
                    : NetworkGameManager.LocalPlayerDisplayName.Trim();
                if (name.Length > 32) name = name.Substring(0, 32);
                SetMyDisplayNameServerRpc(name);
            }
        }

        [ServerRpc(RequireOwnership = false)]
        private void SetMyDisplayNameServerRpc(string name, ServerRpcParams p = default)
        {
            ulong clientId = p.Receive.SenderClientId;
            if (string.IsNullOrWhiteSpace(name)) name = "Player " + clientId;
            if (name.Length > 32) name = name.Substring(0, 32);
            serverNames[clientId] = name;
            SyncDisplayNameClientRpc(clientId, name);
        }

        [ClientRpc]
        private void SyncDisplayNameClientRpc(ulong clientId, string name)
        {
            clientNames[clientId] = name ?? ("Player " + clientId);
        }

        public static string GetDisplayName(ulong clientId, bool isAi = false)
        {
            if (isAi) return GameNames.GetNameForAI(clientId);
            if (Instance != null && Instance.IsServer && Instance.serverNames.TryGetValue(clientId, out string s))
                return s;
            if (clientNames.TryGetValue(clientId, out string c))
                return c;
            return "Player " + clientId;
        }

        public static void RemoveClient(ulong clientId)
        {
            if (Instance != null && Instance.IsServer)
                Instance.serverNames.Remove(clientId);
            clientNames.Remove(clientId);
        }
    }
}
