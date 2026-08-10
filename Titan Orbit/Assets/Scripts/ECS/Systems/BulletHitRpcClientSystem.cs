using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Client: receives <see cref="BulletHitRpc"/> and feeds <see cref="BulletVfxBridge"/> so
    /// <c>BulletVfxDriver</c> can play impact VFX and destroy the matching tracer.
    /// World: ClientSimulation. Group: SimulationSystemGroup.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(BulletSpawnRpcClientSystem))]
    public partial struct BulletHitRpcClientSystem : ISystem
    {
        /// <summary>Re-queues broadcast hit RPCs into the VFX bridge.</summary>
        public void OnUpdate(ref SystemState state)
        {
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (rpc, entity) in SystemAPI
                         .Query<RefRO<BulletHitRpc>>()
                         .WithAll<ReceiveRpcCommandRequest>()
                         .WithEntityAccess())
            {
                var r = rpc.ValueRO;
                float3 hit = r.HitPosition;
                hit.y = 0f;

                BulletVfxBridge.EnqueueHit(new BulletVfxBridge.HitRequest
                {
                    Sequence = r.Sequence,
                    HitPosition = hit,
                    Damage = r.Damage,
                    OwnerTeam = r.OwnerTeam,
                    BankIndex = r.BankIndex,
                    ScaleMultiplier = r.ScaleMultiplier > 0f ? r.ScaleMultiplier : 1f,
                    AsteroidHealthAfter = r.AsteroidHealthAfter,
                    PlanetaryDefensePlanetId = r.PlanetaryDefensePlanetId,
                    PlanetaryDefenseSlotIndex = r.PlanetaryDefenseSlotIndex,
                    PlanetaryDefenseHealthAfter = r.PlanetaryDefenseHealthAfter,
                });
                ecb.DestroyEntity(entity);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }
}