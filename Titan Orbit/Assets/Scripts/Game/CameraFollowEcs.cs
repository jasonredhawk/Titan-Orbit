using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Baseline top-down follow: hard lock to the local ship ECS pose each LateUpdate.
    /// </summary>
    [DefaultExecutionOrder(67001)]
    public class CameraFollowEcs : MonoBehaviour
    {
        [SerializeField] Vector3 offsetAtReferenceLevel = new Vector3(0f, 40f, 0f);
        [SerializeField] float gameplayFieldOfView = 45f;

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
            Vector3 targetPos;
            if (ShipDisplayPose.HasLocalPose)
                targetPos = ShipDisplayPose.LocalPosition;
            else if (!EcsGameBridge.TryGetLocalShipPosition(out targetPos))
                return;

            transform.position = new Vector3(targetPos.x, targetPos.y, targetPos.z) + offsetAtReferenceLevel;
            transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        }
    }
}
