using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Client: receives <see cref="PeopleTransportPoseRpc"/> and feeds
    /// <see cref="PeopleTransportVfxBridge"/> so hybrid floats track server sim / combat positions.
    /// World: ClientSimulation. Group: SimulationSystemGroup.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct PeopleTransportPoseRpcClientSystem : ISystem
    {
        /// <summary>Re-queues pose / end RPCs into the VFX bridge (host also gets in-process enqueue).</summary>
        public void OnUpdate(ref SystemState state)
        {
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (rpc, entity) in SystemAPI
                         .Query<RefRO<PeopleTransportPoseRpc>>()
                         .WithAll<ReceiveRpcCommandRequest>()
                         .WithEntityAccess())
            {
                var r = rpc.ValueRO;
                float3 pos = r.Position;
                pos.y = 0f;
                float3 vel = r.Velocity;
                vel.y = 0f;

                PeopleTransportVfxBridge.EnqueuePose(new PeopleTransportVfxBridge.PoseUpdate
                {
                    Sequence = r.Sequence,
                    Position = pos,
                    Velocity = vel,
                    Status = r.Status,
                });
                ecb.DestroyEntity(entity);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }
}
