using System;
using TitanOrbit.Generation;
using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Burst smoothing for owner-predicted ship <see cref="LocalTransform"/> reconciliation.
    /// Online clients roll the hull back onto the server snapshot after a bounce, then
    /// resimulate. With no smoother that resim result replaces the pose on screen in one
    /// tick — the ship and the camera snap backward. This keeps the pose the player
    /// already saw and walks a limited step toward the server result.
    /// A respawn-sized gap and a map wrap still take the corrected pose in one tick.
    /// Bullets and other predicted ghosts pass a null user pointer and are left alone.
    /// </summary>
    public static class ShipLocalTransformPredictionSmoothing
    {
        /// <summary>Gaps smaller than this are quantization. Take the corrected pose.</summary>
        const float QuietDistance = 0.04f;

        /// <summary>
        /// Largest XZ step toward the reconciled pose in one snapshot, in world units.
        /// About one slow-cruise tick, so a bounce disagreement does not pop.
        /// </summary>
        const float MaxPositionStep = 0.15f;

        /// <summary>Gaps this large are a respawn or abandon, not a bounce. Take them whole.</summary>
        const float TeleportDistance = 24f;

        /// <summary>Yaw errors smaller than this (radians) take the corrected facing.</summary>
        const float QuietAngle = 0.01f;

        /// <summary>Largest facing step toward the reconciled pose in one snapshot, in radians.</summary>
        const float MaxAngleStep = 0.12f;

        /// <summary>Pointer for <see cref="GhostPredictionSmoothing.RegisterSmoothingAction{T}"/>.</summary>
        public static readonly PortableFunctionPointer<GhostPredictionSmoothing.SmoothingActionDelegate> Action =
            new PortableFunctionPointer<GhostPredictionSmoothing.SmoothingActionDelegate>(SmoothingAction);

        /// <summary>
        /// NetCode reconciliation callback. <paramref name="currentData"/> is the pose after
        /// resim. <paramref name="previousData"/> is the pose that was on screen.
        /// <paramref name="userData"/> is <see cref="ShipTag"/> for hulls and null otherwise.
        /// </summary>
        [BurstCompile(DisableDirectCall = true)]
        [AOT.MonoPInvokeCallback(typeof(GhostPredictionSmoothing.SmoothingActionDelegate))]
        static unsafe void SmoothingAction(IntPtr currentData, IntPtr previousData, IntPtr userData)
        {
            if (userData == IntPtr.Zero)
                return;

            ref var current = ref UnsafeUtility.AsRef<LocalTransform>(currentData.ToPointer());
            ref var previous = ref UnsafeUtility.AsRef<LocalTransform>(previousData.ToPointer());

            // A wrap is the same hull on the other edge. Lerping it crosses the map.
            if (ToroidalMapEcs.TryGetMapSize(out float mapW, out float mapH) &&
                ToroidalMapEcs.IsWrapJump(previous.Position, current.Position, mapW, mapH))
                return;

            float3 offset = ToroidalMapEcs.TryGetMapSize(out mapW, out mapH)
                ? ToroidalMapEcs.ShortestOffsetXZ(previous.Position, current.Position, mapW, mapH)
                : current.Position - previous.Position;
            offset.y = 0f;
            float dist = math.length(offset);

            // Respawn and abandon still cut. A bounce-sized miss does not.
            if (dist < TeleportDistance && dist > QuietDistance)
            {
                float accept = math.min(1f, MaxPositionStep / dist);
                float3 blended = previous.Position + offset * accept;
                blended.y = math.lerp(previous.Position.y, current.Position.y, accept);
                current.Position = ToroidalMapEcs.TryGetMapSize(out mapW, out mapH)
                    ? ToroidalMapEcs.Wrap(blended, mapW, mapH)
                    : blended;
            }

            float angle = math.angle(previous.Rotation, current.Rotation);
            if (angle > QuietAngle && angle < math.PI)
            {
                float rotAccept = math.min(1f, MaxAngleStep / angle);
                current.Rotation = math.slerp(previous.Rotation, current.Rotation, rotAccept);
            }
        }
    }

    /// <summary>
    /// Same reconcile gate for <see cref="ShipKinematics.Velocity"/>. Position smoothing
    /// alone still lets the next motor tick integrate the server velocity and drag the
    /// hull back through the bounce. Formation fields stay on the resim result.
    /// </summary>
    public static class ShipKinematicsPredictionSmoothing
    {
        /// <summary>Speed errors smaller than this (m/s) take the corrected velocity.</summary>
        const float QuietSpeed = 0.35f;

        /// <summary>Largest speed-error step in one snapshot, in m/s.</summary>
        const float MaxSpeedStep = 2.5f;

        /// <summary>A respawn zeros a fast hull. Take that whole change.</summary>
        const float TeleportSpeed = 45f;

        /// <summary>Pointer for <see cref="GhostPredictionSmoothing.RegisterSmoothingAction{T}"/>.</summary>
        public static readonly PortableFunctionPointer<GhostPredictionSmoothing.SmoothingActionDelegate> Action =
            new PortableFunctionPointer<GhostPredictionSmoothing.SmoothingActionDelegate>(SmoothingAction);

        /// <summary>
        /// Blends planar velocity toward the resim result. Null user data is not a ship.
        /// </summary>
        [BurstCompile(DisableDirectCall = true)]
        [AOT.MonoPInvokeCallback(typeof(GhostPredictionSmoothing.SmoothingActionDelegate))]
        static unsafe void SmoothingAction(IntPtr currentData, IntPtr previousData, IntPtr userData)
        {
            if (userData == IntPtr.Zero)
                return;

            ref var current = ref UnsafeUtility.AsRef<ShipKinematics>(currentData.ToPointer());
            ref var previous = ref UnsafeUtility.AsRef<ShipKinematics>(previousData.ToPointer());

            float3 delta = current.Velocity - previous.Velocity;
            delta.y = 0f;
            float speed = math.length(delta);
            if (speed <= QuietSpeed || speed >= TeleportSpeed)
                return;

            float accept = math.min(1f, MaxSpeedStep / speed);
            float3 velocity = previous.Velocity + delta * accept;
            velocity.y = 0f;
            current.Velocity = velocity;
        }
    }
}
