using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server → client notify helpers for people-transport VFX.
    /// <para>
    /// Server entities stay non-ghost (bullet / delivery authority). Clients never Instantiates a
    /// PeopleTransportGhost — they mirror <see cref="PeopleTransportPoseRpc"/> onto hybrid GOs.
    /// </para>
    /// </summary>
    public static class PeopleTransportNetNotify
    {
        /// <summary>
        /// Broadcasts an Active pose (or Consumed / Destroyed end) and mirrors it into the host
        /// VFX bridge when a ClientWorld exists in-process.
        /// </summary>
        public static void SendPose(
            ref EntityCommandBuffer ecb,
            uint sequence,
            float3 position,
            float3 velocity,
            byte status)
        {
            if (sequence == 0)
                return;

            position.y = 0f;
            velocity.y = 0f;

            // --- Host in-process (Editor / listen-server) ---
            if (ClientServerBootstrap.ClientWorld != null && ClientServerBootstrap.ClientWorld.IsCreated)
            {
                PeopleTransportVfxBridge.EnqueuePose(new PeopleTransportVfxBridge.PoseUpdate
                {
                    Sequence = sequence,
                    Position = position,
                    Velocity = velocity,
                    Status = status,
                });
            }

            // --- All remote clients (+ host client connection) ---
            Entity rpcEntity = ecb.CreateEntity();
            ecb.AddComponent(rpcEntity, new PeopleTransportPoseRpc
            {
                Sequence = sequence,
                Position = position,
                Velocity = velocity,
                Status = status,
            });
            ecb.AddComponent(rpcEntity, new SendRpcCommandRequest { TargetConnection = Entity.Null });
        }

        /// <summary>
        /// End-of-life notify + destroy via ECB (delivery, return, abort).
        /// </summary>
        public static void EndAndDestroy(
            ref EntityCommandBuffer ecb,
            Entity transportEntity,
            in PeopleTransportState transport,
            float3 position,
            byte status)
        {
            SendPose(ref ecb, transport.Sequence, position, transport.Velocity, status);
            ecb.DestroyEntity(transportEntity);
        }

        /// <summary>
        /// End-of-life notify + destroy via EntityManager (bullet path — no ECB).
        /// </summary>
        public static void EndAndDestroyImmediate(
            ref SystemState state,
            Entity transportEntity,
            in PeopleTransportState transport,
            float3 position,
            byte status)
        {
            if (transport.Sequence != 0)
            {
                var ecb = new EntityCommandBuffer(Unity.Collections.Allocator.Temp);
                SendPose(ref ecb, transport.Sequence, position, transport.Velocity, status);
                ecb.Playback(state.EntityManager);
                ecb.Dispose();
            }

            state.EntityManager.DestroyEntity(transportEntity);
        }

        /// <summary>Reads current transform position for end notify (Y forced to 0).</summary>
        public static float3 ReadPosition(EntityManager em, Entity transportEntity)
        {
            if (!em.HasComponent<LocalTransform>(transportEntity))
                return float3.zero;
            float3 p = em.GetComponentData<LocalTransform>(transportEntity).Position;
            p.y = 0f;
            return p;
        }
    }
}
