using TitanOrbit.Diagnostics;
using TitanOrbit.Shared;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Top-down camera hard-locks to <see cref="ShipDisplayPose"/> (NetCode presentation pose).
    /// [TITAN-ORBIT] No SmoothDamp chase — NetCode is the only smoothing owner for flight feel.
    /// Moon-dock cinematic still overrides with a hard lock. Client only; order 67001 after
    /// <see cref="ShipVisualSyncSystem"/> fills the pose.
    /// </summary>
    [DefaultExecutionOrder(67001)]
    public class CameraFollowEcs : MonoBehaviour
    {
        /// <summary>[UNITY] World-space offset from ship (Y lift for top-down view).</summary>
        [SerializeField] Vector3 offsetAtReferenceLevel = new Vector3(0f, 40f, 0f);

        /// <summary>[UNITY] Perspective FOV for gameplay camera.</summary>
        [SerializeField] float gameplayFieldOfView = 45f;

        UnityEngine.Camera cam;

        // #region agent log
        Vector3 _dbgLastCam;
        bool _dbgHasCam;
        int _dbgCamLogBudget;
        // #endregion

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
            Vector3 next = targetPos + offsetAtReferenceLevel;

            // #region agent log
            if (_dbgCamLogBudget <= 0)
                _dbgCamLogBudget = 60;
            float camDelta = _dbgHasCam ? Vector3.Distance(next, _dbgLastCam) : 0f;
            float displayDelta = ShipDisplayPose.HasLocalPose
                ? Vector3.Distance(targetPos, _dbgHasCam ? (_dbgLastCam - offsetAtReferenceLevel) : targetPos)
                : -1f;
            if (_dbgHasCam && camDelta > 0.0001f && _dbgCamLogBudget-- > 0)
            {
                string data =
                    "{\"camDelta\":" + camDelta.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) +
                    ",\"displayDelta\":" + displayDelta.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) +
                    ",\"frame\":" + Time.frameCount + "}";
                ShipFlightSmoothDebugLog.Write("H4", "CameraFollowEcs.LateUpdate", "camera hard-lock step", data);
            }
            _dbgLastCam = next;
            _dbgHasCam = true;
            // #endregion

            transform.position = next;
            transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        }

        /// <summary>
        /// Resolves world follow position. Moon-dock cinematic overrides presentation when active.
        /// </summary>
        static bool TryResolveFollowTarget(out Vector3 targetPos)
        {
            // [HYBRID] Moon dock GameObject applier may override during landing/takeoff.
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
