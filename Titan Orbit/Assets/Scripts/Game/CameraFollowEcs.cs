using UnityEngine;

namespace TitanOrbit.Game
{
    [DefaultExecutionOrder(65000)]
    public class CameraFollowEcs : MonoBehaviour
    {
        [SerializeField] Vector3 offset = new Vector3(0f, 40f, 0f);
        [SerializeField] float smooth = 8f;

        void LateUpdate()
        {
            if (!EcsGameBridge.TryGetLocalShipPosition(out var targetPos))
                return;
            var desired = targetPos + offset;
            transform.position = Vector3.Lerp(transform.position, desired, Time.deltaTime * smooth);
            transform.LookAt(targetPos);
        }
    }
}
