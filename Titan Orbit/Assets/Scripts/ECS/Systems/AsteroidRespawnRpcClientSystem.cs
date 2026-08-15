using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [NETCODE] Client applies <see cref="AsteroidRespawnRpc"/> by spawning a local (non-ghost)
    /// asteroid — map asteroids are not streamed under dynamic ghost relevancy.
    /// <para>
    /// Copies RPC payloads out of the query first, consumes receive entities, then Instantiates.
    /// Structural changes (Instantiate / strip GhostInstance) must not run inside SystemAPI foreach.
    /// </para>
    /// <para>
    /// [TITAN-ORBIT] This system ticks every client sim frame (no RequireForUpdate). Respawn
    /// receive entities must be consumed immediately (MaxRpcAgeFrames). A failed apply (join skip,
    /// missing prefab) is queued and retried — otherwise the server has a live rock and the client
    /// has empty space that still rams the hull.
    /// Zombie wipe is pose-tight and culled-only so a respawn cannot hard-destroy live neighbors.
    /// </para>
    /// World: ClientSimulation.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct AsteroidRespawnRpcClientSystem : ISystem
    {
        /// <summary>Scratch copy of one inbound respawn RPC (blittable).</summary>
        struct Pending
        {
            public float3 Position;
            public float Scale;
            public float GemValue;
            public float MaxHealth;
            public float Size;
            public Entity RpcEntity;
        }

        /// <summary>No RequireForUpdate — retries must run on frames with zero inbound RPCs.</summary>
        public void OnCreate(ref SystemState state)
        {
        }

        /// <summary>
        /// Consumes inbound respawn RPCs, applies or queues them, then retries earlier misses.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            var em = state.EntityManager;
            var pending = new NativeList<Pending>(8, Allocator.Temp);

            // --- Phase 1: copy inbound respawn RPCs ---
            // Consume even when GamePrefabs is missing — MaxRpcAgeFrames would otherwise drop
            // the spawn forever and leave an invisible server rock.
            foreach (var (rpc, reqEntity) in SystemAPI.Query<RefRO<AsteroidRespawnRpc>>()
                         .WithAll<ReceiveRpcCommandRequest>().WithEntityAccess())
            {
                var r = rpc.ValueRO;
                pending.Add(new Pending
                {
                    Position = r.Position,
                    Scale = r.Scale,
                    GemValue = r.GemValue,
                    MaxHealth = r.MaxHealth,
                    Size = r.Size,
                    RpcEntity = reqEntity,
                });
            }

            // --- Phase 2: consume RPCs ---
            if (pending.Length > 0)
            {
                var destroyEcb = new EntityCommandBuffer(Allocator.Temp);
                for (int i = 0; i < pending.Length; i++)
                    destroyEcb.DestroyEntity(pending[i].RpcEntity);
                destroyEcb.Playback(em);
                destroyEcb.Dispose();
            }

            Entity prefab = Entity.Null;
            if (SystemAPI.TryGetSingleton<GamePrefabs>(out var prefabs))
                prefab = prefabs.Asteroid;

            // --- Phase 3: wipe the zombie at this slot, then Instantiates ---
            for (int i = 0; i < pending.Length; i++)
            {
                var p = pending[i];
                float3 pos = p.Position;
                pos.y = 0f;
                if (!ClientLocalAsteroidCombatSync.TryApplyAsteroidRespawn(
                    em, prefab, pos, p.Scale, p.GemValue, p.MaxHealth, p.Size))
                {
                    ClientLocalAsteroidCombatSync.QueueUnmatchedRespawn(
                        pos, p.Scale, p.GemValue, p.MaxHealth, p.Size);
                }
            }

            pending.Dispose();

            // --- Phase 4: retry earlier misses ---
            ClientLocalAsteroidCombatSync.RetryUnmatchedRespawns(em, prefab);
        }
    }
}
