using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Core;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Manages minimap markers (defend/attack signals) across the network
    /// </summary>
    public class MinimapMarkerManager : NetworkBehaviour
    {
        public static MinimapMarkerManager Instance { get; private set; }
        
        [Header("Marker Settings")]
        [SerializeField] private GameObject markerPrefab;
        [SerializeField] private float markerHeight = 1f; // Height above ground
        
        private void Awake()
        {
            if (Instance == null)
            {
                Instance = this;
            }
            else
            {
                Destroy(gameObject);
            }
        }
        
        [ServerRpc(RequireOwnership = false)]
        public void CreateMarkerServerRpc(Vector3 worldPosition, MinimapMarker.MarkerType markerType, TeamManager.Team team)
        {
            Debug.Log($"CreateMarkerServerRpc called: worldPosition={worldPosition}, markerType={markerType}, team={team}");
            
            // Create marker GameObject
            GameObject markerObj = new GameObject($"MinimapMarker_{markerType}_{team}");
            markerObj.transform.position = worldPosition;
            
            // Add NetworkObject
            NetworkObject netObj = markerObj.AddComponent<NetworkObject>();
            
            // Add MinimapMarker component
            MinimapMarker marker = markerObj.AddComponent<MinimapMarker>();
            
            // Spawn on network first, then initialize (so spawnTime is set correctly)
            netObj.Spawn();
            Debug.Log($"Marker spawned on network. NetworkObjectId: {netObj.NetworkObjectId}, IsSpawned: {netObj.IsSpawned}");
            
            // Initialize after spawn so network variables are ready
            marker.Initialize(worldPosition, markerType, team);
            Debug.Log($"Marker initialized. Position: {marker.transform.position}");
        }
    }
}
