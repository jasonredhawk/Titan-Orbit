using System;
using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Burst smoothing for owner-predicted <see cref="LocalTransform"/> reconciliation.
    /// <para>
    /// basics36: after dedicated double-tick fix, Relay/Socket clients reached healthy
    /// <c>cmdAge≈-2</c> / <c>predictLead≈7</c>, but flight still felt choppy with full-snap
    /// reconcile (<see cref="TitanOrbitShipPredictionSmoothingBootstrap"/> previously disabled).
    /// At ~25 FPS, <c>maxDelta≈0.6–0.85</c> is mostly multi-tick motion plus hard correction snaps.
    /// </para>
    /// <para>
    /// [NETCODE] Blend is high (near 1) — mostly accept the corrected pose, keep a small fraction of
    /// the previous predicted pose to hide micro snaps. Low blends (e.g. 0.45) were rejected earlier
    /// (rubber-band) while <c>cmdAge</c> was still ~20.
    /// </para>
    /// </summary>
    public static class ShipLocalTransformPredictionSmoothing
    {
        /// <summary>
        /// How strongly to accept the reconciled pose (1 = full snap). 0.92 softens micro pops.
        /// </summary>
        const float PositionBlend = 0.92f;

        /// <summary>Matched to position so facing does not lag the hull.</summary>
        const float RotationBlend = 0.92f;

        /// <summary>Pointer for <see cref="GhostPredictionSmoothing.RegisterSmoothingAction{T}"/>.</summary>
        public static readonly PortableFunctionPointer<GhostPredictionSmoothing.SmoothingActionDelegate> Action =
            new PortableFunctionPointer<GhostPredictionSmoothing.SmoothingActionDelegate>(SmoothingAction);

        /// <summary>
        /// NetCode reconciliation callback — blends previous predicted pose toward corrected pose.
        /// </summary>
        [BurstCompile(DisableDirectCall = true)]
        [AOT.MonoPInvokeCallback(typeof(GhostPredictionSmoothing.SmoothingActionDelegate))]
        static unsafe void SmoothingAction(IntPtr currentData, IntPtr previousData, IntPtr userData)
        {
            ref var current = ref UnsafeUtility.AsRef<LocalTransform>(currentData.ToPointer());
            ref var previous = ref UnsafeUtility.AsRef<LocalTransform>(previousData.ToPointer());
            _ = userData;

            // current = corrected after resim; previous = pose before reconcile this tick.
            current.Position = math.lerp(previous.Position, current.Position, PositionBlend);
            current.Rotation = math.slerp(previous.Rotation, current.Rotation, RotationBlend);
        }
    }
}
