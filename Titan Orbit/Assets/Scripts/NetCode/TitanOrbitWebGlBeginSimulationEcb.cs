#if UNITY_WEBGL && !UNITY_EDITOR
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using UnityEngine;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// WebGL ClientWorld excludes every <see cref="EntityCommandBufferSystem"/> (OnCreate WASM-OOB).
    /// <see cref="Unity.NetCode.RpcSystem"/> first runs after connect (RequireForUpdate on the
    /// connection RPC buffers) and then <c>GetSingleton&lt;BeginSimulationEntityCommandBufferSystem.Singleton&gt;</c>
    /// OOBs because that singleton was never created.
    /// </summary>
    static class TitanOrbitWebGlBeginSimulationEcb
    {
        static UnsafeList<EntityCommandBuffer> s_Pending;

        public static void Ensure(World world)
        {
            if (world == null || !world.IsCreated)
                return;

            var em = world.EntityManager;
            using (var q = em.CreateEntityQuery(
                       ComponentType.ReadOnly<BeginSimulationEntityCommandBufferSystem.Singleton>()))
            {
                if (!q.IsEmptyIgnoreFilter)
                    return;
            }

            if (!s_Pending.IsCreated)
                s_Pending = new UnsafeList<EntityCommandBuffer>(8, Allocator.Persistent);

            var singleton = default(BeginSimulationEntityCommandBufferSystem.Singleton);
            singleton.SetPendingBufferList(ref s_Pending);
            singleton.SetAllocator(Allocator.TempJob);
            Entity entity = em.CreateEntity(typeof(BeginSimulationEntityCommandBufferSystem.Singleton));
            em.SetComponentData(entity, singleton);
            // #region agent log
            Debug.Log("CONNECT_JOIN begin-sim-ecb-created");
            // #endregion
        }

        public static void Playback(World world)
        {
            if (world == null || !world.IsCreated || !s_Pending.IsCreated || s_Pending.Length == 0)
                return;

            world.EntityManager.CompleteAllTrackedJobs();
            int n = s_Pending.Length;
            for (int i = 0; i < n; i++)
            {
                EntityCommandBuffer ecb = s_Pending[i];
                if (ecb.IsCreated)
                {
                    ecb.Playback(world.EntityManager);
                    ecb.Dispose();
                }
            }

            s_Pending.Clear();
            // #region agent log
            Debug.Log("CONNECT_JOIN begin-sim-ecb-playback n=" + n);
            // #endregion
        }
    }
}
#endif
