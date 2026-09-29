#if UNITY_WEBGL && !UNITY_EDITOR
using TitanOrbit.Diagnostics;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// WebGL ClientWorld excludes Unity.Multiplayer.Center, including
    /// <c>SetRpcSystemDynamicAssemblyListSystem</c>. Dedicated server and Windows
    /// clients keep that system, so they send/receive RPCs with
    /// <see cref="RpcCollection.DynamicAssemblyList"/> = 1. WebGL was left at 0,
    /// logged <c>ours=0 theirs=1</c>, then WASM-OOB on the InvalidRpc disconnect.
    /// Set the flag here before <see cref="RpcSystem.OnUpdate"/>.
    /// </summary>
    static class TitanOrbitWebGlDynamicAssemblyList
    {
        static bool s_Logged;

        public static void Ensure(World world)
        {
            if (world == null || !world.IsCreated)
                return;

            var em = world.EntityManager;
            using var q = em.CreateEntityQuery(ComponentType.ReadWrite<RpcCollection>());
            if (q.IsEmptyIgnoreFilter)
            {
                if (!s_Logged)
                {
                    s_Logged = true;
                    // #region agent log
                    Debug.Log("CONNECT_JOIN dal-missing-rpccollection");
                    WebGlBootDebugProbe.Emit("H-DAL-A", "TitanOrbitWebGlDynamicAssemblyList.Ensure",
                        "dal-missing-rpccollection", "{}");
                    // #endregion
                }
                return;
            }

            ref var col = ref q.GetSingletonRW<RpcCollection>().ValueRW;
            bool before = col.DynamicAssemblyList;
            if (!before)
                col.DynamicAssemblyList = true;

            if (!s_Logged)
            {
                s_Logged = true;
                // #region agent log
                Debug.Log("CONNECT_JOIN dal-ensure before=" + (before ? 1 : 0) +
                          " after=" + (col.DynamicAssemblyList ? 1 : 0));
                WebGlBootDebugProbe.Emit("H-DAL-A", "TitanOrbitWebGlDynamicAssemblyList.Ensure",
                    "dal-ensure",
                    "{\"before\":" + (before ? 1 : 0) +
                    ",\"after\":" + (col.DynamicAssemblyList ? 1 : 0) + "}");
                // #endregion
            }
        }
    }
}
#endif
