using UnityEngine;
using TitanOrbit.Generation;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Keeps toroidal map entities visible when the camera is near edges.
    /// Stores the logical (canonical) position and each frame moves the transform to the
    /// toroidal copy that is closest to the camera—same idea as the minimap's placement.
    /// No wrapping in Update: we only update display position here in LateUpdate.
    /// </summary>
    public class ToroidalRenderer : MonoBehaviour
    {
        private Vector3 logicalPosition;
        private bool logicalPositionStored;
        private static UnityEngine.Camera s_cachedMainCamera;
        private static int s_cachedCameraFrame = -1;

        private void Start()
        {
            StoreLogicalPosition();
        }

        private void OnEnable()
        {
            // In case we're enabled after position is set (e.g. network spawn)
            if (!logicalPositionStored)
            {
                StoreLogicalPosition();
            }
        }

        private void StoreLogicalPosition()
        {
            logicalPosition = transform.position;
            logicalPositionStored = true;
        }

        private void LateUpdate()
        {
            // Cache Camera.main once per frame (it does FindGameObjectWithTag internally); 314+ entities were each calling it every frame causing lag.
            if (Time.frameCount != s_cachedCameraFrame)
            {
                s_cachedCameraFrame = Time.frameCount;
                s_cachedMainCamera = UnityEngine.Camera.main;
            }
            UnityEngine.Camera cam = s_cachedMainCamera;
            if (cam == null) return;

            // If we haven't stored logical position yet (e.g. late spawn), use current position as canonical
            if (!logicalPositionStored)
            {
                StoreLogicalPosition();
            }

            // Place this entity at the toroidal copy closest to the camera so it's always visible.
            Vector3 displayPos = ToroidalMap.GetDisplayPosition(logicalPosition, cam.transform.position);
            transform.position = displayPos;
        }
    }
}
