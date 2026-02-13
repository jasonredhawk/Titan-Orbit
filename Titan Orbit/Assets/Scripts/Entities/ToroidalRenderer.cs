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
            UnityEngine.Camera cam = UnityEngine.Camera.main;
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
