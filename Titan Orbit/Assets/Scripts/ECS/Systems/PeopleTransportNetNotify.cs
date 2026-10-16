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
        /// Broadcasts Consumed / Destroyed / Returned (end of a load hop) and mirrors it into
        /// the host VFX bridge when a ClientWorld exists in-process. Do not send Active —
        /// clients magnet to the live ship ghost.
        /// </summary>
        public static void SendPose(
            ref EntityCommandBuffer ecb,
            uint sequence,
            float3 position,
            float3 velocity,
            byte status,
            float health = -1f)
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
                    Health = health,
                });
            }

            // --- Viewers near the end of the hop (a missed end packet times out on the client) ---
            var rpc = new PeopleTransportPoseRpc
            {
                Sequence = sequence,
                Position = position,
                Velocity = velocity,
                Status = status,
                Health = health,
            };
            float3 back = position;
            float speed = math.length(velocity);
            if (speed > 0.01f)
                back = position - velocity / speed * 40f;
            ViewInterestFanout.EmitSegment(ref ecb, rpc, back, position, 16f, 0);
        }

        /// <summary>
        /// Tell clients an in-flight load changed escort seats. Pose layout stays the
        /// same: <see cref="PeopleTransportPoseStatus.SeatRetargetVelocityY"/> marks the
        /// packet, and <c>Position.y</c> is <c>seatId + 1</c>. XZ is the ship so view
        /// interest still reaches the orbiting player. The sphere does not snap.
        /// </summary>
        public static void SendSeatRetarget(
            ref EntityCommandBuffer ecb,
            uint sequence,
            byte seatId,
            float3 shipPosition,
            int shipNetworkId)
        {
            if (sequence == 0)
                return;

            float3 position = shipPosition;
            position.y = seatId + 1f;
            float3 velocity = new float3(0f, PeopleTransportPoseStatus.SeatRetargetVelocityY, 0f);

            if (ClientServerBootstrap.ClientWorld != null && ClientServerBootstrap.ClientWorld.IsCreated)
            {
                PeopleTransportVfxBridge.EnqueuePose(new PeopleTransportVfxBridge.PoseUpdate
                {
                    Sequence = sequence,
                    Position = position,
                    Velocity = velocity,
                    Status = PeopleTransportPoseStatus.Active,
                    Health = -1f,
                });
            }

            var rpc = new PeopleTransportPoseRpc
            {
                Sequence = sequence,
                Position = position,
                Velocity = velocity,
                Status = PeopleTransportPoseStatus.Active,
                Health = -1f,
            };
            ViewInterestFanout.EmitPoint(ref ecb, rpc, shipPosition, 32f, shipNetworkId);
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
            SendPose(ref ecb, transport.Sequence, position, transport.Velocity, status, transport.Health);
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
                SendPose(ref ecb, transport.Sequence, position, transport.Velocity, status, transport.Health);
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
