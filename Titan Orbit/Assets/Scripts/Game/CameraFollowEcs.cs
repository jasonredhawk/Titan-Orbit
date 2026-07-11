using TitanOrbit.Shared;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Top-down camera rig that hard-locks to the local ship each LateUpdate. Reads
    /// <see cref="ShipDisplayPose"/> (presentation-phase pose) — not raw sim transforms —
    /// so the view matches what the player sees without extra smoothing on predicted movement.
    /// Client only; dedicated server has no camera. Paired with <see cref="ShipVisualSyncSystem"/>
    /// which fills presentation pose before this runs (execution order 67001).
    /// </summary>
    [DefaultExecutionOrder(67001)]
    public class CameraFollowEcs : MonoBehaviour
    {
        // [UNITY] World-space offset from ship position at reference ship level (Y lift for top-down view).
        [SerializeField] Vector3 offsetAtReferenceLevel = new Vector3(0f, 40f, 0f);

        // [UNITY] Perspective FOV — gameplay uses perspective, not orthographic minimap camera.
        [SerializeField] float gameplayFieldOfView = 45f;

        UnityEngine.Camera cam;

        /// <summary>
        /// [UNITY] Awake — cache camera and lock to top-down euler (90° pitch).
        /// </summary>
        void Awake()
        {
            cam = GetComponent<UnityEngine.Camera>();
            if (cam != null)
            {
                cam.orthographic = false;
                cam.fieldOfView = gameplayFieldOfView;
            }

            transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        }

        /// <summary>
        /// [UNITY] LateUpdate — presentation cache is filled during Update; read via EcsGameBridge helper.
        /// </summary>
        void LateUpdate()
        {
            // --- Resolve follow target ---
            Vector3 targetPos;
            if (!EcsGameBridge.TryGetLocalShipPresentationPosition(out targetPos) &&
                !EcsGameBridge.TryGetLocalShipPosition(out targetPos))
                return; // [STANDARD] No local ship yet (loading, dead, or not spawned).

            // --- Apply rig ---
            // [TITAN-ORBIT] Y comes from ship + fixed camera lift; rotation stays locked top-down.
            transform.position = new Vector3(targetPos.x, targetPos.y, targetPos.z) + offsetAtReferenceLevel;
            transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        }
    }
}
