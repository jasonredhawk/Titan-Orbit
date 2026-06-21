using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Entities;

namespace TitanOrbit.Networking
{
    /// <summary>
    /// Remote ship display: interpolates pose on the shared server-time playhead and applies to BankPivot
    /// for smooth toroidal rendering.
    /// </summary>
    [DefaultExecutionOrder(31000)]
    public sealed class ShipVisualInterpolator : ClientRenderTimelineSource
    {
        private Starship starship;
        private Rigidbody rb;
        private Vector3 displayPosition;
        private Quaternion displayRotation;
        private bool hasDisplayPose;

        public bool TryGetDisplayPosition(out Vector3 pos)
        {
            pos = hasDisplayPose ? displayPosition : (rb != null ? rb.position : transform.position);
            return hasDisplayPose;
        }

        public bool TryGetDisplayRotation(out Quaternion rot)
        {
            rot = hasDisplayPose ? displayRotation : transform.rotation;
            return hasDisplayPose;
        }

        private void Awake()
        {
            starship = GetComponent<Starship>();
            rb = GetComponent<Rigidbody>();
        }

        public void OnNetworkMotorStateReceived(ShipMotorStateSnapshot state)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsClient || nm.IsServer) return;

            PushSnapshot(state.ServerTime, state.Position, state.Rotation, state.Velocity, state.LastAppliedInputSequence);
        }

        private void LateUpdate()
        {
            if (starship == null || starship.IsLocalPlayerShip()) return;

            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsClient || nm.IsServer) return;

            var timeline = ClientRenderTimeline.Instance ?? ClientRenderTimeline.EnsureExists();
            double renderTime = timeline.RenderServerTime;

            if (!TrySampleAt(renderTime, out displayPosition, out displayRotation, out _))
            {
                hasDisplayPose = false;
                return;
            }

            hasDisplayPose = true;
            displayPosition.y = 0f;

            if (rb != null)
            {
                rb.position = displayPosition;
                rb.rotation = displayRotation;
            }
            transform.position = displayPosition;
            transform.rotation = displayRotation;
        }
    }
}
