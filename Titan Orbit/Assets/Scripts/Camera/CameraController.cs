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

            // Lock camera to ship - ship is always in wrapped coordinates, so just follow directly
            // No camera wrapping needed - ship position is already wrapped in [-halfWidth, halfWidth]
            Vector3 targetPosition = target.position + offset;
            transform.position = targetPosition;
        }

        public void SetTarget(Transform newTarget)
        {
            target = newTarget;
        }
    }
}
