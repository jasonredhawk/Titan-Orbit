using UnityEngine;
using TitanOrbit.Camera;
using TitanOrbit.Entities;

namespace TitanOrbit.Core
{
    /// <summary>
    /// Initializes the scene at runtime - finds and connects components
    /// </summary>
    public class SceneInitializer : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private CameraController cameraController;
        [SerializeField] private GameObject playerShipPrefab;

        private void Start()
        {
            InitializeScene();
        }

        private void InitializeScene()
        {
            // Find camera controller if not assigned
            if (cameraController == null)
            {
                cameraController = FindObjectOfType<CameraController>();
            }

            // Find player ship and set camera target
            if (cameraController != null)
            {
                Starship playerShip = FindObjectOfType<Starship>();
                if (playerShip != null)
                {
                    cameraController.SetTarget(playerShip.transform);
                }
            }

            Debug.Log("Scene initialized!");
        }

        public void SetCameraTarget(Transform target)
        {
            if (cameraController != null)
            {
                cameraController.SetTarget(target);
            }
        }
    }
}
