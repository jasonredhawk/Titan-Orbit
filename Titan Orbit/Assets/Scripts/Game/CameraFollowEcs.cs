using TitanOrbit.Core;
using TitanOrbit.ECS;
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
        Vector3 _lastAppliedPos;
        bool _hasLastApplied;
        string _lastSource = "none";

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
            if (!TryResolveFollowTarget(out var targetPos, out string source))
            {
                // [DIAGNOSTIC] Expected before Join Team / after leave — log once per streak, not every frame.
                if (_hasLastApplied || _lastSource != "none")
                {
                    Debug.LogWarning(
                        $"[AsteroidBlink] CAMERA_NO_TARGET frame={Time.frameCount} " +
                        $"hasPose={ShipDisplayPose.HasLocalPose} " +
                        $"backlog={ClientJoinSettleCache.GhostSpawnBacklog} " +
                        $"suppress={ClientTeamFlowState.ShouldSuppressLocalPlayerControl()} " +
                        $"lastSource={_lastSource}");
                    _hasLastApplied = false;
                    _lastSource = "none";
                }

                return;
            }

            // --- Hard-lock to presentation pose (one smoothing owner: NetCode) ---
            Vector3 next = targetPos + offsetAtReferenceLevel;
            if (_hasLastApplied)
            {
                float jump = Vector3.Distance(next, _lastAppliedPos);
                if (jump >= 1.5f)
                {
                    Debug.LogWarning(
                        $"[AsteroidBlink] CAMERA_APPLY_JUMP delta={jump:F2} source={source} " +
                        $"from=({_lastAppliedPos.x:F1},{_lastAppliedPos.z:F1}) " +
                        $"to=({next.x:F1},{next.z:F1}) hasPose={ShipDisplayPose.HasLocalPose} " +
                        $"backlog={ClientJoinSettleCache.GhostSpawnBacklog}");
                }
            }

            transform.position = next;
            transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            _lastAppliedPos = next;
            _hasLastApplied = true;
            _lastSource = source;
        }

        /// <summary>
        /// Resolves world follow position. Moon-dock cinematic overrides presentation when active
        /// (stable moon anchor while parked — not the spinning surface hull).
        /// </summary>
        static bool TryResolveFollowTarget(out Vector3 targetPos, out string source)
        {
            // [HYBRID] Moon dock GameObject applier overrides during landing/dock/takeoff.
            if (ShipMoonDockVisualApplier.TryGetLocalFollowPosition(out targetPos))
            {
                source = "MoonDock";
                return true;
            }

            // [NETCODE] Presentation pose from ShipVisualSyncSystem — not raw sim.
            if (ShipDisplayPose.HasLocalPose)
            {
                targetPos = ShipDisplayPose.LocalPosition;
                source = "ShipDisplayPose";
                return true;
            }

            if (EcsGameBridge.TryGetLocalShipPresentationPosition(out targetPos))
            {
                source = "PresentationCache";
                return true;
            }

            if (EcsGameBridge.TryGetLocalShipPosition(out targetPos))
            {
                source = "SimLocalShip";
                return true;
            }

            source = "none";
            return false;
        }
    }
}
