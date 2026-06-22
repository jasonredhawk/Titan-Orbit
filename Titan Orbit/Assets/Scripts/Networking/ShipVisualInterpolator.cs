using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Entities;
using TitanOrbit.Generation;

namespace TitanOrbit.Networking
{
    /// <summary>
    /// Remote ship display: interpolates pose on the shared server-time playhead, then applies a
    /// snap-free smoothing pass before publishing the pose to BankPivot for toroidal rendering.
    ///
    /// Raw snapshot interpolation jumps whenever the buffer underruns (late/lost packet) or the render
    /// clock corrects — that jump is the "ship catching up after lag" jerk. We hide it with
    /// <b>projective velocity blending</b>: each frame the displayed pose is pushed forward along the last
    /// known velocity (so constant-speed motion has zero added lag) and any residual error against the freshly
    /// sampled target is decayed exponentially. Discontinuities become a smooth glide instead of a pop.
    /// </summary>
    [DefaultExecutionOrder(31000)]
    public sealed class ShipVisualInterpolator : ClientRenderTimelineSource
    {
        // Time constants for how fast the displayed pose closes residual error onto the network-sampled pose.
        // Small = snappier but lets micro-jitter through; large = glassier but laggier on direction changes.
        [SerializeField, Range(0.02f, 0.25f)] private float positionSmoothTime = 0.08f;
        [SerializeField, Range(0.02f, 0.25f)] private float rotationSmoothTime = 0.06f;
        // Error larger than this (toroidal wrap, respawn, teleport) is snapped instead of glided.
        [SerializeField] private float snapErrorDistance = 30f;

        private Starship starship;
        private Rigidbody rb;

        private Vector3 displayPosition;
        private Quaternion displayRotation;
        private Vector3 displayVelocity;
        private bool displayThrusting;
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

        /// <summary>Interpolated planar speed of the remote ship (for engine glow). 0 until a pose is available.</summary>
        public float DisplaySpeed => hasDisplayPose ? displayVelocity.magnitude : 0f;

        /// <summary>Remote owner's forward-thrust intent, delayed in lockstep with the interpolated pose (for thruster flames).</summary>
        public bool DisplayThrusting => hasDisplayPose && displayThrusting;

        private void Awake()
        {
            starship = GetComponent<Starship>();
            rb = GetComponent<Rigidbody>();
        }

        public void OnNetworkMotorStateReceived(ShipMotorStateSnapshot state)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsClient || nm.IsServer) return;

            PushSnapshot(state.ServerTime, state.Position, state.Rotation, state.Velocity, state.LastAppliedInputSequence, state.Thrust);
        }

        private void LateUpdate()
        {
            if (starship == null || starship.IsLocalPlayerShip()) return;
            if (starship.GemMoonDocked) return;

            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsClient || nm.IsServer) return;

            var timeline = ClientRenderTimeline.Instance ?? ClientRenderTimeline.EnsureExists();
            double renderTime = timeline.RenderServerTime;

            if (!TrySampleAt(renderTime, out Vector3 targetPosition, out Quaternion targetRotation, out displayVelocity, out displayThrusting))
            {
                // No data yet: hold whatever we last displayed rather than freezing at origin.
                return;
            }

            targetPosition.y = 0f;
            displayVelocity.y = 0f;

            float dt = Time.unscaledDeltaTime;
            if (!hasDisplayPose)
            {
                displayPosition = targetPosition;
                displayRotation = targetRotation;
            }
            else
            {
                // Projective velocity blending: carry the displayed pose forward along the known velocity so
                // steady motion tracks with no added lag, then decay the residual error toward the freshly
                // sampled target. A late packet's "catch up" becomes a smooth glide instead of a snap.
                Vector3 predicted = displayPosition + displayVelocity * dt;
                Vector3 error = ToroidalMap.ShortestWorldOffsetXZ(predicted, targetPosition);

                if (error.magnitude > snapErrorDistance)
                {
                    displayPosition = targetPosition;
                }
                else
                {
                    float posK = 1f - Mathf.Exp(-dt / Mathf.Max(0.001f, positionSmoothTime));
                    displayPosition = predicted + error * posK;
                }

                float rotK = 1f - Mathf.Exp(-dt / Mathf.Max(0.001f, rotationSmoothTime));
                displayRotation = Quaternion.Slerp(displayRotation, targetRotation, rotK);
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
