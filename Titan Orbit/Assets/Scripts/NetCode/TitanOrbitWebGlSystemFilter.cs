using System;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// Shared WebGL ClientWorld system filter. Used by
    /// <see cref="TitanOrbitBootstrap"/> at player boot and by the Editor
    /// <c>TitanOrbit → WebGL → Validate DynamicAssemblyList</c> menu.
    /// </summary>
    public static class TitanOrbitWebGlSystemFilter
    {
        public const string DynamicAssemblyListSystemHint = "SetRpcSystemDynamicAssemblyListSystem";

        /// <summary>
        /// True when the system must be omitted from the WebGL ClientWorld.
        /// </summary>
        public static bool IsExcluded(string systemName)
        {
            if (string.IsNullOrEmpty(systemName))
                return false;

            // [UNITY] Entities Graphics — no compute shaders on WebGL.
            if (systemName.StartsWith("Unity.Rendering.", StringComparison.Ordinal))
                return true;
            if (systemName.StartsWith("Unity.Entities.Graphics.", StringComparison.Ordinal))
                return true;

            // [UNITY] Physics↔EG bridge — hybrid GameObject proxies own visuals on WebGL.
            if (systemName.StartsWith("Unity.Physics.GraphicsIntegration.", StringComparison.Ordinal))
                return true;

            // [TITAN-ORBIT] Proven WASM OOB on BeginVariableRateSimulationEntityCommandBufferSystem
            // OnCreate. Titan Orbit uses NetCode predicted fixed-step, not VariableRateSimulation.
            if (systemName.IndexOf("VariableRateSimulation", StringComparison.Ordinal) >= 0)
                return true;

            // [TITAN-ORBIT] Proven WASM OOB on GhostSpawnSystem OnCreate. Join cannot spawn ghosts
            // until a WebGL-safe OnCreate path exists — keep excluded with the menu-untick policy.
            if (systemName == "Unity.NetCode.GhostSpawnSystem")
                return true;

            // [TITAN-ORBIT] Proven WASM OOB on every EntityCommandBufferSystem OnCreate,
            // including NetworkGroupCommandBufferSystem (Chrome 2026-09-25 leaf 164).
            // NetworkStreamReceiveSystem plays back a local ECB on WebGL instead.
            if (systemName.IndexOf("CommandBufferSystem", StringComparison.Ordinal) >= 0)
                return true;

            // [TITAN-ORBIT] Multiplayer Center NetcodeForEntities example systems are not used by
            // Titan Orbit gameplay — omit from WebGL ClientWorld.
            // Side effect: this also drops SetRpcSystemDynamicAssemblyListSystem, so WebGL
            // stays RpcCollection.DynamicAssemblyList=0 while server/Windows stay 1.
            if (systemName.IndexOf("Unity.Multiplayer.Center", StringComparison.Ordinal) >= 0
                || systemName.IndexOf("Unity_Multiplayer_Center", StringComparison.Ordinal) >= 0)
                return true;

            // [TITAN-ORBIT] People-transport spawn RPC client — omitted on WebGL menu boot path
            // (not required until ClientWorld is re-ticked for in-game join).
            if (systemName == "TitanOrbit.ECS.PeopleTransportSpawnRpcClientSystem")
                return true;

            return false;
        }

        public static bool IsDynamicAssemblyListSystem(string systemName)
        {
            return !string.IsNullOrEmpty(systemName) &&
                   systemName.IndexOf(DynamicAssemblyListSystemHint, StringComparison.Ordinal) >= 0;
        }
    }
}
