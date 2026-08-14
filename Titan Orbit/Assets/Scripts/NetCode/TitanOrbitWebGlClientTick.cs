using Unity.Entities;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// [TITAN-ORBIT] WebGL ClientWorld tick that never runs Transform / predicted-fixed Burst.
    /// <para>
    /// Chrome 2026-08-09/10: stock <c>World.Update</c> after join OOBs once
    /// <see cref="TransformSystemGroup"/> is enabled (same native class as Windows Join Team
    /// Crash!!!). Menu boot strips GhostSpawn + CommandBuffers and unticks the world; join still
    /// called <c>clientWorld.Update()</c> 30× then every <c>ClientConnectWatch</c> frame.
    /// </para>
    /// Desktop / Editor keep a normal <c>World.Update</c>.
    /// </summary>
    public static class TitanOrbitWebGlClientTick
    {
        static bool s_LoggedOnce;

        /// <summary>
        /// Ticks a client world. On WebGL, forces Transform / predicted / fixed-step off first.
        /// </summary>
        /// <param name="world">ClientWorld (or any world on WebGL — server is not created there).</param>
        public static void SafeUpdate(World world)
        {
            if (world == null || !world.IsCreated)
                return;

#if UNITY_WEBGL && !UNITY_EDITOR
            DisableUnsafeGroups(world);
            if (!s_LoggedOnce)
            {
                s_LoggedOnce = true;
                Debug.Log("[WebGLClientTick] SafeUpdate — Transform/Predicted/FixedStep forced OFF.");
            }
#endif
            world.Update();
        }

        /// <summary>
        /// Parks Burst-heavy groups that OOB on WebGL. Call after CreateClientWorld and before
        /// any join tick so JoinSettle cannot race them back on in the same Update.
        /// </summary>
        public static void DisableUnsafeGroups(World world)
        {
            if (world == null || !world.IsCreated)
                return;

            var transform = world.GetExistingSystemManaged<TransformSystemGroup>();
            if (transform != null)
                transform.Enabled = false;

            // LocalToWorldSystem is unmanaged — JoinSettle disables it on WebGL (ECS allowUnsafe).

            var predicted = world.GetExistingSystemManaged<PredictedSimulationSystemGroup>();
            if (predicted != null)
                predicted.Enabled = false;

            var fixedStep = world.GetExistingSystemManaged<FixedStepSimulationSystemGroup>();
            if (fixedStep != null)
                fixedStep.Enabled = false;

            var presentation = world.GetExistingSystemManaged<PresentationSystemGroup>();
            if (presentation != null)
                presentation.Enabled = false;
        }
    }
}
