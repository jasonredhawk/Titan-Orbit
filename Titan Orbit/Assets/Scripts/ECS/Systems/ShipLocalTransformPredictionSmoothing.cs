using System;
using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Optional Burst smoothing for owner reconciliation (currently unused — see bootstrap).
    /// [NETCODE] When re-enabled, keep blend near 1.0 until the motor is highly deterministic;
    /// low blends (e.g. 0.45) produce hybrid poses that feel like forward/back rubber-banding.
    /// </summary>
    public static class ShipLocalTransformPredictionSmoothing
    {
        /// <summary>1 = full accept of corrected pose (no hybrid leftover).</summary>
        const float PositionBlend = 1f;

        /// <summary>Matched to position so facing does not lag the hull.</summary>
        const float RotationBlend = 1f;

        /// <summary>Pointer for <see cref="GhostPredictionSmoothing.RegisterSmoothingAction{T}"/>.</summary>
        public static readonly PortableFunctionPointer<GhostPredictionSmoothing.SmoothingActionDelegate> Action =
            new PortableFunctionPointer<GhostPredictionSmoothing.SmoothingActionDelegate>(SmoothingAction);

        /// <summary>
        /// NetCode reconciliation callback — blends previous predicted pose toward corrected pose.
        /// </summary>
        [BurstCompile(DisableDirectCall = true)]
        static unsafe void SmoothingAction(IntPtr currentData, IntPtr previousData, IntPtr userData)
        {
            ref var current = ref UnsafeUtility.AsRef<LocalTransform>(currentData.ToPointer());
            ref var previous = ref UnsafeUtility.AsRef<LocalTransform>(previousData.ToPointer());
            _ = userData;

            current.Position = math.lerp(previous.Position, current.Position, PositionBlend);
            current.Rotation = math.slerp(previous.Rotation, current.Rotation, RotationBlend);
        }
    }
}
