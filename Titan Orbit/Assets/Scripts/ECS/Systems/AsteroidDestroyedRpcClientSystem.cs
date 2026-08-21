using TitanOrbit.Generation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [NETCODE] Client applies <see cref="AsteroidDestroyedRpc"/> by soft-destroying the matching
    /// local asteroid (cull collision + hide GO). Covers bullet / mine / ram kills — not only HitRpc.
    /// Hard <c>DestroyEntity</c> waits for <see cref="AsteroidRespawnRpc"/>.
    /// <para>
    /// Copies RPC payloads out of the query first, consumes receive entities, then soft-destroys
    /// local bodies (structural DestroyEntity must not run inside SystemAPI foreach — and kill
    /// frames avoid DestroyEntity entirely so predicted ship movement stays healthy).
    /// </para>
    /// <para>
    /// [TITAN-ORBIT] This system ticks every client sim frame (no RequireForUpdate on the RPC
    /// query). Destroy receive entities must be consumed immediately (MaxRpcAgeFrames), but a
    /// zero-match apply is queued and retried — otherwise a join skip dropped the kill forever
    /// and left a phantom collider after the mesh hid.
    /// </para>
    /// World: ClientSimulation.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(AsteroidRespawnRpcClientSystem))]
    public partial struct AsteroidDestroyedRpcClientSystem : ISystem
    {
        /// <summary>Scratch copy of one inbound destroy RPC.</summary>
        struct Pending
        {
            public float3 Position;
            public float Scale;
            public Entity RpcEntity;
        }

        /// <summary>No RequireForUpdate — retries must run on frames with zero inbound RPCs.</summary>
        public void OnCreate(ref SystemState state)
        {
        }

        /// <summary>
        /// Soft-destroys local asteroids at each RPC pose, queues misses for retry, then retries
        /// any still-unmatched poses from earlier ticks.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            var em = state.EntityManager;
            var pending = new NativeList<Pending>(8, Allocator.Temp);

            // --- Phase 1: copy inbound destroy RPCs (receive entities, not asteroid gathers) ---
            // [TITAN-ORBIT] Must consume RPCs even during ShouldSkipMapBodyQueries (join Instantiates).
            // Apply/retry lives in ClientLocalAsteroidCombatSync and gates the registry walk there.
            foreach (var (rpc, reqEntity) in SystemAPI.Query<RefRO<AsteroidDestroyedRpc>>()
                         .WithAll<ReceiveRpcCommandRequest>().WithEntityAccess())
            {
                var r = rpc.ValueRO;
                pending.Add(new Pending
                {
                    Position = r.Position,
                    Scale = r.Scale,
                    RpcEntity = reqEntity,
                });
            }

            // --- Phase 2: consume RPCs (avoids MaxRpcAgeFrames stale warnings) ---
            if (pending.Length > 0)
            {
                var destroyEcb = new EntityCommandBuffer(Allocator.Temp);
                for (int i = 0; i < pending.Length; i++)
                    destroyEcb.DestroyEntity(pending[i].RpcEntity);
                destroyEcb.Playback(em);
                destroyEcb.Dispose();
            }

            // --- Phase 3: soft-destroy the single nearest local rock (no DestroyEntity) ---
            // [TITAN-ORBIT] Hard teardown on kill froze client predicted ship movement.
            // Respawn RPC hard-destroys the zombie immediately before Instantiates.
            // Must not radius-wipe neighbors — that hid live rocks in a dense belt.
            for (int i = 0; i < pending.Length; i++)
            {
                var p = pending[i];
                float3 pos = SphericalMapEcs.ProjectToSphere(p.Position);
                int culled = ClientLocalAsteroidCombatSync.SoftDestroyLocalAsteroidsNear(em, pos, p.Scale);
                // Join skip / registry lag: keep the pose until a later tick finds the rock.
                if (culled <= 0)
                    ClientLocalAsteroidCombatSync.QueueUnmatchedDestroy(pos, p.Scale);
            }

            pending.Dispose();

            // --- Phase 4: retry earlier misses (runs even when no RPC arrived this tick) ---
            ClientLocalAsteroidCombatSync.RetryUnmatchedDestroys(em);
        }
    }
}
