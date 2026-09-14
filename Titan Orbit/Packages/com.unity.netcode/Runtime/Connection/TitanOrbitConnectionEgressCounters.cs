using Unity.Burst;
using Unity.Burst.CompilerServices;
using Unity.Entities;

namespace Unity.NetCode
{
    /// <summary>
    /// [TITAN-ORBIT] Per-connection NetCode payload counters for the GameManager egress overlay.
    /// <para>
    /// Ghost — a networked entity replica. RPC — a one-shot reliable message (bullet spawn, store buy).
    /// Snapshot — the periodic ghost delta packet the server sends at NetworkTickRate.
    /// </para>
    /// <para>
    /// Added on every connection entity (client Connect, server Accept). Burst jobs in
    /// GhostSendSystem / RpcSystem / CommandSendPacketSystem / NetworkStreamReceiveSystem
    /// increment these only when <see cref="TitanOrbitEgressMeterHook.Enabled"/> is true
    /// so production play pays a single predicted-false branch.
    /// </para>
    /// Not a ghost and not replicated — local bookkeeping on the connection entity.
    /// </summary>
    public struct TitanOrbitConnectionEgressCounters : IComponentData
    {
        /// <summary>Server → this connection: snapshot datagram payload bytes (<c>EndSend</c>).</summary>
        public ulong SendSnapshotBytes;

        /// <summary>This connection outbound: RPC datagram payload bytes (<c>EndSend</c>).</summary>
        public ulong SendRpcBytes;

        /// <summary>Client → server: command datagram payload bytes (<c>EndSend</c>).</summary>
        public ulong SendCommandBytes;

        /// <summary>Successful snapshot <c>EndSend</c> count.</summary>
        public ulong SendSnapshotPackets;

        /// <summary>Successful RPC <c>EndSend</c> count.</summary>
        public ulong SendRpcPackets;

        /// <summary>Successful command <c>EndSend</c> count.</summary>
        public ulong SendCommandPackets;

        /// <summary>Inbound snapshot payload bytes (UTP Data event, before GhostReceive clobber).</summary>
        public ulong RecvSnapshotBytes;

        /// <summary>Inbound RPC payload bytes.</summary>
        public ulong RecvRpcBytes;

        /// <summary>Inbound command payload bytes (server receive of client upload).</summary>
        public ulong RecvCommandBytes;

        /// <summary>Inbound UTP Data events (all NetCode message types).</summary>
        public ulong RecvPacketCount;
    }

    /// <summary>
    /// [TITAN-ORBIT] Burst-safe on/off for <see cref="TitanOrbitConnectionEgressCounters"/> increments.
    /// <para>
    /// <see cref="SharedStatic{T}"/> is a Burst-readable static. TitanOrbit copy systems set
    /// <c>Enabled.Data</c> from <c>TitanOrbitDebugFlags.EgressMeterEnabled</c> each tick.
    /// Unity.NetCode cannot reference TitanOrbit.Shared, so the flag lives here.
    /// </para>
    /// </summary>
    public static class TitanOrbitEgressMeterHook
    {
        /// <summary>Unique type keys so Burst SharedStatic storage does not collide with other flags.</summary>
        public struct EnabledContext { }

        /// <summary>Sub-key paired with <see cref="EnabledContext"/>.</summary>
        public struct EnabledKey { }

        /// <summary>
        /// When true, send/receive Burst jobs increment <see cref="TitanOrbitConnectionEgressCounters"/>.
        /// Written from TitanOrbit systems on the main thread; read from Burst jobs.
        /// </summary>
        public static readonly SharedStatic<bool> Enabled =
            SharedStatic<bool>.GetOrCreate<EnabledContext, EnabledKey>();

        /// <summary>
        /// Adds a successful snapshot send. No-op when the meter is off or the connection has no counters.
        /// Called from GhostSendSystem after <c>EndSend</c> succeeds.
        /// </summary>
        /// <param name="lookup">Read-write lookup of counters on connection entities.</param>
        /// <param name="entity">The connection entity that was sent to.</param>
        /// <param name="byteCount">UTP payload length of that snapshot packet.</param>
        public static void TryAddSendSnapshot(
            ref ComponentLookup<TitanOrbitConnectionEgressCounters> lookup,
            Entity entity,
            int byteCount)
        {
            if (Hint.Likely(!Enabled.Data) || byteCount <= 0 || entity == Entity.Null)
                return;
            if (!lookup.HasComponent(entity))
                return;
            ref var counters = ref lookup.GetRefRW(entity).ValueRW;
            counters.SendSnapshotBytes += (ulong)byteCount;
            counters.SendSnapshotPackets++;
        }

        /// <summary>
        /// Adds a successful RPC send. No-op when the meter is off or the connection has no counters.
        /// Called from RpcSystem after <c>EndSend</c> succeeds (server egress or client upload).
        /// </summary>
        /// <param name="lookup">Read-write lookup of counters on connection entities.</param>
        /// <param name="entity">The connection entity that was sent to / from.</param>
        /// <param name="byteCount">UTP payload length of that RPC packet.</param>
        public static void TryAddSendRpc(
            ref ComponentLookup<TitanOrbitConnectionEgressCounters> lookup,
            Entity entity,
            int byteCount)
        {
            if (Hint.Likely(!Enabled.Data) || byteCount <= 0 || entity == Entity.Null)
                return;
            if (!lookup.HasComponent(entity))
                return;
            ref var counters = ref lookup.GetRefRW(entity).ValueRW;
            counters.SendRpcBytes += (ulong)byteCount;
            counters.SendRpcPackets++;
        }

        /// <summary>
        /// Adds a successful command send (client upload). No-op when the meter is off.
        /// Called from CommandSendPacketSystem after <c>EndSend</c> succeeds.
        /// </summary>
        /// <param name="lookup">Read-write lookup of counters on connection entities.</param>
        /// <param name="entity">This client's connection entity.</param>
        /// <param name="byteCount">UTP payload length of that command packet.</param>
        public static void TryAddSendCommand(
            ref ComponentLookup<TitanOrbitConnectionEgressCounters> lookup,
            Entity entity,
            int byteCount)
        {
            if (Hint.Likely(!Enabled.Data) || byteCount <= 0 || entity == Entity.Null)
                return;
            if (!lookup.HasComponent(entity))
                return;
            ref var counters = ref lookup.GetRefRW(entity).ValueRW;
            counters.SendCommandBytes += (ulong)byteCount;
            counters.SendCommandPackets++;
        }

        /// <summary>
        /// Adds one inbound UTP Data event classified by <see cref="NetworkStreamProtocol"/>.
        /// Called from NetworkStreamReceiveSystem before GhostReceive can clobber the snapshot buffer.
        /// </summary>
        /// <param name="lookup">Read-write lookup of counters on connection entities.</param>
        /// <param name="entity">The connection that received the packet.</param>
        /// <param name="msgType">NetCode protocol byte (Command / Snapshot / Rpc).</param>
        /// <param name="payloadBytes">Unread UTP payload length at the Data event (includes the protocol byte).</param>
        public static void TryAddRecv(
            ref ComponentLookup<TitanOrbitConnectionEgressCounters> lookup,
            Entity entity,
            byte msgType,
            int payloadBytes)
        {
            if (Hint.Likely(!Enabled.Data) || payloadBytes <= 0 || entity == Entity.Null)
                return;
            if (!lookup.HasComponent(entity))
                return;
            ref var counters = ref lookup.GetRefRW(entity).ValueRW;
            counters.RecvPacketCount++;
            switch ((NetworkStreamProtocol)msgType)
            {
                case NetworkStreamProtocol.Snapshot:
                    counters.RecvSnapshotBytes += (ulong)payloadBytes;
                    break;
                case NetworkStreamProtocol.Rpc:
                    counters.RecvRpcBytes += (ulong)payloadBytes;
                    break;
                case NetworkStreamProtocol.Command:
                    counters.RecvCommandBytes += (ulong)payloadBytes;
                    break;
            }
        }
    }
}
