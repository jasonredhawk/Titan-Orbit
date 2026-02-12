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
        [SerializeField] private Vector3 offset = new Vector3(0, 10, 0);
        [SerializeField] private float followSpeed = 5f;
        [SerializeField] private float rotationSpeed = 2f;

        [Header("Toroidal Map Settings")]
        [SerializeField] private float mapWidth = 1000f;
        [SerializeField] private float mapHeight = 1000f;
        [SerializeField] private bool enableToroidalWrapping = true;

        private UnityEngine.Camera cam;
        private Vector3 velocity;

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

            Vector3 targetPosition = target.position + offset;
            
            // Handle toroidal wrapping
            if (enableToroidalWrapping)
            {
                targetPosition = WrapPosition(targetPosition);
            }

            // Smooth follow
            transform.position = Vector3.SmoothDamp(transform.position, targetPosition, ref velocity, 1f / followSpeed);

            // Optional: Rotate camera to match ship rotation
            if (rotationSpeed > 0)
            {
                Quaternion targetRotation = Quaternion.Euler(90f, target.eulerAngles.y, 0f);
                transform.rotation = Quaternion.Lerp(transform.rotation, targetRotation, rotationSpeed * Time.deltaTime);
            }
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
