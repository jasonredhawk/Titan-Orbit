using System;
using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Burst smoothing delegate registered on the client <see cref="GhostPredictionSmoothing"/> singleton.
    /// Eases position/rotation when NetCode rolls back and resimulates the local predicted ship —
    /// reduces visible snaps without adding proxy lerp (see ship-simulation rule).
    /// Invoked only during reconciliation, not every render frame.
    /// </summary>
    [BurstCompile]
    public static class ShipLocalTransformPredictionSmoothing
    {
        /// <summary>Fraction of correction applied per reconciliation step (higher = snappier).</summary>
        const float PositionBlend = 0.45f;

        /// <summary>Rotation blend matched to position so facing does not lag behind hull.</summary>
        const float RotationBlend = 0.45f;

        /// <summary>Pointer passed to <see cref="GhostPredictionSmoothing.RegisterSmoothingAction{T}"/>.</summary>
        public static readonly PortableFunctionPointer<GhostPredictionSmoothing.SmoothingActionDelegate> Action =
            new PortableFunctionPointer<GhostPredictionSmoothing.SmoothingActionDelegate>(SmoothingAction);

        /// <summary>
        /// NetCode reconciliation callback — blends from previous predicted pose toward corrected pose.
        /// </summary>
        [BurstCompile(DisableDirectCall = true)]
        static unsafe void SmoothingAction(IntPtr currentData, IntPtr previousData, IntPtr userData)
        {
            ref var current = ref UnsafeUtility.AsRef<LocalTransform>(currentData.ToPointer());
            ref var previous = ref UnsafeUtility.AsRef<LocalTransform>(previousData.ToPointer());

            // [NETCODE] userData unused — stateless correction per package contract.
            _ = userData;

            current.Position = math.lerp(previous.Position, current.Position, PositionBlend);
            current.Rotation = math.slerp(previous.Rotation, current.Rotation, RotationBlend);
        }
    }
}
