using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [NETCODE] Client applies <see cref="AsteroidDestroyedRpc"/> by soft-destroying the matching
    /// seed-hydrated local asteroid (cull collision + hide GO). Covers bullet / mine / ram kills —
    /// not only HitRpc. Hard <c>DestroyEntity</c> waits for <see cref="AsteroidRespawnRpc"/>.
    /// <para>
    /// Copies RPC payloads out of the query first, consumes receive entities, then soft-destroys
    /// local bodies (structural DestroyEntity must not run inside SystemAPI foreach — and kill
    /// frames avoid DestroyEntity entirely so predicted ship movement stays healthy).
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

        /// <summary>Requires inbound destroy RPCs.</summary>
        public void OnCreate(ref SystemState state)
        {
            var builder = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<AsteroidDestroyedRpc>()
                .WithAll<ReceiveRpcCommandRequest>();
            state.RequireForUpdate(state.GetEntityQuery(builder));
        }

        /// <summary>Soft-destroys local asteroids at each RPC pose, then consumes the RPC entity.</summary>
        public void OnUpdate(ref SystemState state)
        {
            var em = state.EntityManager;
            var pending = new NativeList<Pending>(8, Allocator.Temp);

            // --- Phase 1: copy payloads (no structural changes) ---
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

            if (pending.Length == 0)
            {
                pending.Dispose();
                return;
            }

            // --- Phase 2: consume RPCs ---
            var destroyEcb = new EntityCommandBuffer(Allocator.Temp);
            for (int i = 0; i < pending.Length; i++)
                destroyEcb.DestroyEntity(pending[i].RpcEntity);
            destroyEcb.Playback(em);
            destroyEcb.Dispose();

            // --- Phase 3: soft-destroy local seed-hydrated rocks (no DestroyEntity) ---
            // [TITAN-ORBIT] Hard teardown on kill froze client predicted ship movement.
            // Respawn RPC hard-destroys the zombie immediately before Instantiates.
            for (int i = 0; i < pending.Length; i++)
            {
                var p = pending[i];
                float3 pos = p.Position;
                pos.y = 0f;
                ClientLocalAsteroidCombatSync.SoftDestroyLocalAsteroidsNear(em, pos, p.Scale);
            }

            pending.Dispose();
        }
    }
}
