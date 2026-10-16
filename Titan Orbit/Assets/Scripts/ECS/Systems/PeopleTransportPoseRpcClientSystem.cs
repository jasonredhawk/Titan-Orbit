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
                float3 vel = r.Velocity;
                // Seat retarget keeps Position.y (seatId + 1) and the velocity sentinel.
                // End poses and real Active snaps stay planar.
                bool seatRetarget = r.Status == PeopleTransportPoseStatus.Active &&
                                    vel.y <= PeopleTransportPoseStatus.SeatRetargetVelocityY + 0.01f;
                if (!seatRetarget)
                {
                    pos.y = 0f;
                    vel.y = 0f;
                }

                PeopleTransportVfxBridge.EnqueuePose(new PeopleTransportVfxBridge.PoseUpdate
                {
                    Sequence = r.Sequence,
                    Position = pos,
                    Velocity = vel,
                    Status = r.Status,
                    Health = r.Health,
                });
                ecb.DestroyEntity(entity);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }
}
