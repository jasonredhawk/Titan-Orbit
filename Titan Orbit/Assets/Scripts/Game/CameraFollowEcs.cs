using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Top-down camera follow for ECS ships. Matches legacy CameraController gameplay pose:
    /// fixed downward view (no yaw with ship), smooth XZ tracking, height offset.
    /// </summary>
    [DefaultExecutionOrder(65000)]
    public class CameraFollowEcs : MonoBehaviour
    {
        [SerializeField] Vector3 offsetAtReferenceLevel = new Vector3(0f, 40f, 0f);
        [Tooltip("0 = hard lock on ship XZ (legacy default).")]
        [SerializeField] float followSmoothTime;
        [SerializeField] float gameplayFieldOfView = 45f;

        Vector3 smoothedFollowXz;
        bool hasSmoothedFollowXz;
        Vector3 followVelocity;
        UnityEngine.Camera cam;

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

        void LateUpdate()
        {
            if (!EcsGameBridge.TryGetLocalShipPosition(out var targetPos))
                return;

            float smoothTime = followSmoothTime;

            Vector3 followXz = new Vector3(targetPos.x, 0f, targetPos.z);
            if (smoothTime <= 0.0001f)
            {
                smoothedFollowXz = followXz;
                hasSmoothedFollowXz = true;
                followVelocity = Vector3.zero;
            }
            else if (!hasSmoothedFollowXz)
            {
                smoothedFollowXz = followXz;
                hasSmoothedFollowXz = true;
            }
            else
            {
                smoothedFollowXz = Vector3.SmoothDamp(
                    smoothedFollowXz,
                    followXz,
                    ref followVelocity,
                    smoothTime,
                    Mathf.Infinity,
                    Time.deltaTime);
            }

            transform.position = new Vector3(
                smoothedFollowXz.x,
                targetPos.y,
                smoothedFollowXz.z) + offsetAtReferenceLevel;

            transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        }
    }
}
