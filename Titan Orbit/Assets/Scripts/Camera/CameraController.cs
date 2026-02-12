using UnityEngine;

namespace TitanOrbit.Camera
{
    /// <summary>
    /// Camera controller that follows the player ship with smooth movement
    /// Handles toroidal map boundaries
    /// </summary>
    public class CameraController : MonoBehaviour
    {
        [Header("Camera Settings")]
        [SerializeField] private Transform target;
        [SerializeField] private Vector3 offset = new Vector3(0, 20, 0); // Above ship for top-down view

        [Header("Toroidal Map Settings")]
        [SerializeField] private float mapWidth = 300f;
        [SerializeField] private float mapHeight = 300f;
        [SerializeField] private bool enableToroidalWrapping = true;

        private UnityEngine.Camera cam;

        private void Awake()
        {
            cam = GetComponent<UnityEngine.Camera>();
            if (cam == null)
            {
                cam = gameObject.AddComponent<UnityEngine.Camera>();
            }

            // Set up camera for top-down view
            transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        }

        private void LateUpdate()
        {
            if (target == null) return;

            // Lock camera to ship - ship always centered, no delay
            Vector3 targetPosition = target.position + offset;
            if (enableToroidalWrapping)
            {
                targetPosition = WrapPosition(targetPosition);
            }
            transform.position = targetPosition;
        }

        private Vector3 WrapPosition(Vector3 position)
        {
            // Wrap X
            if (position.x > mapWidth / 2f)
            {
                position.x -= mapWidth;
            }
            else if (position.x < -mapWidth / 2f)
            {
                position.x += mapWidth;
            }

            // Wrap Z
            if (position.z > mapHeight / 2f)
            {
                position.z -= mapHeight;
            }
            else if (position.z < -mapHeight / 2f)
            {
                position.z += mapHeight;
            }

            return position;
        }

        public void SetTarget(Transform newTarget)
        {
            target = newTarget;
        }

        public void SetMapSize(float width, float height)
        {
            mapWidth = width;
            mapHeight = height;
        }
    }
}
