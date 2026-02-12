using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Camera;
using TitanOrbit.Entities;

namespace TitanOrbit.Core
{
    /// <summary>
    /// Finds the local player when they spawn and sets up camera follow
    /// </summary>
    public class LocalPlayerSetup : MonoBehaviour
    {
        [SerializeField] private CameraController cameraController;
        [SerializeField] private float checkInterval = 0.5f;

        private float nextCheckTime;
        private bool playerFound;

        private void Update()
        {
            if (playerFound) return;
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsClient) return;
            if (Time.time < nextCheckTime) return;

            nextCheckTime = Time.time + checkInterval;

            // Fallback: find camera controller if not assigned
            if (cameraController == null)
            {
                cameraController = GetComponent<TitanOrbit.Camera.CameraController>();
                if (cameraController == null)
                {
                    cameraController = FindObjectOfType<TitanOrbit.Camera.CameraController>();
                }
            }

            // Find local player object
            if (NetworkManager.Singleton.SpawnManager != null)
            {
                NetworkObject localPlayer = NetworkManager.Singleton.SpawnManager.GetLocalPlayerObject();
                if (localPlayer != null)
                {
                    Starship ship = localPlayer.GetComponent<Starship>();
                    if (ship != null && cameraController != null)
                    {
                        cameraController.SetTarget(ship.transform);
                        playerFound = true;
                        Debug.Log("Local player found - camera following ship");
                    }
                }
            }
        }
    }
}