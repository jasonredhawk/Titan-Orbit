using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Client: receives <see cref="BulletSpawnRpc"/> and feeds <see cref="BulletVfxBridge"/> for
    /// <c>BulletVfxDriver</c> GameObjects. Deduped with host in-process enqueue by Sequence.
    /// World: ClientSimulation. Group: SimulationSystemGroup.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct BulletSpawnRpcClientSystem : ISystem
    {
        /// <summary>Re-queues broadcast spawn RPCs into the VFX bridge.</summary>
        public void OnUpdate(ref SystemState state)
        {
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (rpc, entity) in SystemAPI
                         .Query<RefRO<BulletSpawnRpc>>()
                         .WithAll<ReceiveRpcCommandRequest>()
                         .WithEntityAccess())
            {
                var r = rpc.ValueRO;
                // Keep mount-height SpawnPosition.y; flatten velocity to XZ flight.
                float3 spawn = r.SpawnPosition;
                float3 vel = r.Velocity;
                vel.y = 0f;

                BulletVfxBridge.TryEnqueueSpawn(new BulletVfxBridge.SpawnRequest
                {
                    Sequence = r.Sequence,
                    SpawnPosition = spawn,
                    Velocity = vel,
                    Lifetime = r.Lifetime,
                    MaxDistance = r.MaxDistance,
                    Damage = r.Damage,
                    OwnerTeam = r.OwnerTeam,
                    OwnerNetworkId = r.OwnerNetworkId,
                    BankIndex = r.BankIndex,
                    ScaleMultiplier = r.ScaleMultiplier > 0f ? r.ScaleMultiplier : 1f,
                    IsAnticipation = false,
                    IsDisplaySpace = false,
                });
                ecb.DestroyEntity(entity);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }
}
