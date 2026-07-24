using TitanOrbit.Shared;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Top-down camera hard-locks to <see cref="ShipDisplayPose"/> (NetCode presentation pose).
    /// [TITAN-ORBIT] No SmoothDamp chase — NetCode is the only smoothing owner for flight feel.
    /// Moon-dock cinematic overrides with a hard lock on the ship hull (including surface spin while
    /// docked). Client only; order 67001 after <see cref="ShipVisualSyncSystem"/> fills the pose.
    /// </summary>
    [DefaultExecutionOrder(67001)]
    public class CameraFollowEcs : MonoBehaviour
    {
        /// <summary>[UNITY] World-space offset from ship (Y lift for top-down view).</summary>
        [SerializeField] Vector3 offsetAtReferenceLevel = new Vector3(0f, 40f, 0f);

        /// <summary>[UNITY] Perspective FOV for gameplay camera.</summary>
        [SerializeField] float gameplayFieldOfView = 45f;

        UnityEngine.Camera cam;

        /// <summary>[UNITY] Awake — cache camera and lock to top-down euler (90° pitch).</summary>
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
        /// [UNITY] LateUpdate — presentation pose is published during ECS Update on this frame.
        /// </summary>
        void LateUpdate()
        {
            if (!TryResolveFollowTarget(out var targetPos))
                return;

            // --- Hard-lock to presentation pose (one smoothing owner: NetCode) ---
            transform.position = targetPos + offsetAtReferenceLevel;
            transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        }

        /// <summary>
        /// Resolves world follow position. Moon-dock cinematic overrides presentation when active
        /// (ship hull through landing, surface spin, and takeoff).
        /// </summary>
        static bool TryResolveFollowTarget(out Vector3 targetPos)
        {
            // [HYBRID] Moon dock GameObject applier overrides during landing/dock/takeoff —
            // follow the spinning hull, not the moon center.
            if (ShipMoonDockVisualApplier.TryGetLocalFollowPosition(out targetPos))
                return true;

            // [NETCODE] Presentation pose from ShipVisualSyncSystem — not raw sim.
            if (ShipDisplayPose.HasLocalPose)
            {
                targetPos = ShipDisplayPose.LocalPosition;
                return true;
            }

            if (EcsGameBridge.TryGetLocalShipPresentationPosition(out targetPos))
                return true;

            return EcsGameBridge.TryGetLocalShipPosition(out targetPos);
        }
    }
}
