using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Client: receives <see cref="MineExplodeRpc"/> and feeds <see cref="MineExplosionBridge"/>
    /// so <c>MineVisualDriver</c> can play the FireballsV2 (or catalog) burst.
    /// World: ClientSimulation. Group: SimulationSystemGroup.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct MineExplodeRpcClientSystem : ISystem
    {
        /// <summary>Re-queues broadcast explode RPCs into the VFX bridge.</summary>
        public void OnUpdate(ref SystemState state)
        {
            var destroyEcb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (rpc, entity) in SystemAPI
                         .Query<RefRO<MineExplodeRpc>>()
                         .WithAll<ReceiveRpcCommandRequest>()
                         .WithEntityAccess())
            {
                var r = rpc.ValueRO;
                float3 pos = r.Position;
                pos.y = 0f;

                MineExplosionBridge.Enqueue(new MineExplosionBridge.Request
                {
                    Sequence = r.Sequence,
                    Position = pos,
                    OwnerTeam = r.OwnerTeam,
                    ItemLevel = r.ItemLevel,
                    VisualScale = r.VisualScale > 0.01f ? r.VisualScale : 1f,
                    ExplosionVfxScale = r.ExplosionVfxScale > 0f ? r.ExplosionVfxScale : 2f,
                    Damage = r.Damage,
                });

                destroyEcb.DestroyEntity(entity);
            }

            destroyEcb.Playback(state.EntityManager);
            destroyEcb.Dispose();
        }
    }
}
