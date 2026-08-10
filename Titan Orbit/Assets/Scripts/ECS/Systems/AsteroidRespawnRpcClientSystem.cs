using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [NETCODE] Client applies <see cref="AsteroidRespawnRpc"/> by spawning a local (non-ghost)
    /// asteroid — map asteroids are not streamed under dynamic ghost relevancy.
    /// <para>
    /// Copies RPC payloads out of the query first, destroys receive entities, then Instantiates.
    /// Structural changes (Instantiate / strip GhostInstance) must not run inside SystemAPI foreach.
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

        public void OnCreate(ref SystemState state)
        {
            var builder = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<AsteroidRespawnRpc>()
                .WithAll<ReceiveRpcCommandRequest>();
            state.RequireForUpdate(state.GetEntityQuery(builder));
        }

        /// <summary>
        /// Spawns one local asteroid per RPC, then destroys the receive entity.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // --- Prefab ---
            if (!SystemAPI.TryGetSingleton<GamePrefabs>(out var prefabs) || prefabs.Asteroid == Entity.Null)
                return;

            var em = state.EntityManager;
            var pending = new NativeList<Pending>(8, Allocator.Temp);

            // --- Phase 1: copy payloads (no structural changes) ---
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

            if (pending.Length == 0)
            {
                pending.Dispose();
                return;
            }

            // --- Phase 2: consume RPCs (avoids MaxRpcAgeFrames stale warnings) ---
            var destroyEcb = new EntityCommandBuffer(Allocator.Temp);
            for (int i = 0; i < pending.Length; i++)
                destroyEcb.DestroyEntity(pending[i].RpcEntity);
            destroyEcb.Playback(em);
            destroyEcb.Dispose();

            // --- Phase 3: spawn local asteroids (structural changes OK now) ---
            for (int i = 0; i < pending.Length; i++)
            {
                var p = pending[i];
                var body = new MapLayoutBlueprint.Body
                {
                    EntityKind = 3,
                    Position = p.Position,
                    Scale = p.Scale,
                    AsteroidScale = new float3(p.Scale),
                    GemValue = p.GemValue,
                    MaxHealth = p.MaxHealth,
                    Size = p.Size,
                };
                ClientLocalMapBodySpawn.SpawnAsteroid(em, prefabs.Asteroid, body);
            }

            pending.Dispose();
        }
    }
}
