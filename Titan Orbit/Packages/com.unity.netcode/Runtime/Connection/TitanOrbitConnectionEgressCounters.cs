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
    /// increment these only when <see cref="TitanOrbitEgressMeterHook.IsEnabled"/> is true
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
    /// Burst does not run C# static constructors, so we never store a <see cref="SharedStatic{T}"/>
    /// in a static field. <see cref="SharedStatic{T}.GetOrCreate{TContext,TSubContext}"/> is called
    /// at each access — same native slot, no null wrapper.
    /// </para>
    /// TitanOrbit copy systems set <see cref="IsEnabled"/> from the GameManager toggle.
    /// Unity.NetCode cannot reference TitanOrbit.Shared, so the flag lives here.
    /// </summary>
    public static class TitanOrbitEgressMeterHook
    {
        /// <summary>Unique type keys so Burst SharedStatic storage does not collide with other flags.</summary>
        public struct EnabledContext { }

        /// <summary>Sub-key paired with <see cref="EnabledContext"/>.</summary>
        public struct EnabledKey { }

        /// <summary>
        /// When true, send/receive Burst jobs increment <see cref="TitanOrbitConnectionEgressCounters"/>.
        /// Written from TitanOrbit systems on the main thread; read from Burst via GetOrCreate
        /// (Burst does not run static constructors, so we never cache SharedStatic in a field).
        /// </summary>
        public static bool IsEnabled
        {
            get => SharedStatic<bool>.GetOrCreate<EnabledContext, EnabledKey>().Data;
            set => SharedStatic<bool>.GetOrCreate<EnabledContext, EnabledKey>().Data = value;
        }

        /// <summary>Burst-safe 0/1 for job fields (GetOrCreate, not a cached SharedStatic field).</summary>
        public static byte EnabledByte()
        {
            return SharedStatic<bool>.GetOrCreate<EnabledContext, EnabledKey>().Data ? (byte)1 : (byte)0;
        }

        static bool MeterIsOn()
        {
            return SharedStatic<bool>.GetOrCreate<EnabledContext, EnabledKey>().Data;
        }

        /// <summary>
        /// Adds a successful snapshot send. No-op when the meter is off or the connection has no counters.
        /// Called from GhostSendSystem after <c>EndSend</c> succeeds.
        /// </summary>
        public static void TryAddSendSnapshot(
            ref ComponentLookup<TitanOrbitConnectionEgressCounters> lookup,
            Entity entity,
            int byteCount)
        {
            if (Hint.Likely(!MeterIsOn()) || byteCount <= 0 || entity == Entity.Null)
                return;
            if (!lookup.HasComponent(entity))
                return;
            var counters = lookup[entity];
            counters.SendSnapshotBytes += (ulong)byteCount;
            counters.SendSnapshotPackets++;
            lookup[entity] = counters;
        }

        /// <summary>
        /// Adds a successful RPC send. No-op when the meter is off or the connection has no counters.
        /// Called from RpcSystem after <c>EndSend</c> succeeds (server send or client upload).
        /// </summary>
        public static void TryAddSendRpc(
            ref ComponentLookup<TitanOrbitConnectionEgressCounters> lookup,
            Entity entity,
            int byteCount)
        {
            if (Hint.Likely(!MeterIsOn()) || byteCount <= 0 || entity == Entity.Null)
                return;
            if (!lookup.HasComponent(entity))
                return;
            var counters = lookup[entity];
            counters.SendRpcBytes += (ulong)byteCount;
            counters.SendRpcPackets++;
            lookup[entity] = counters;
        }

        /// <summary>
        /// Adds a successful command send (client upload). No-op when the meter is off.
        /// Called from CommandSendPacketSystem after <c>EndSend</c> succeeds.
        /// </summary>
        public static void TryAddSendCommand(
            ref ComponentLookup<TitanOrbitConnectionEgressCounters> lookup,
            Entity entity,
            int byteCount)
        {
            if (Hint.Likely(!MeterIsOn()) || byteCount <= 0 || entity == Entity.Null)
                return;
            if (!lookup.HasComponent(entity))
                return;
            var counters = lookup[entity];
            counters.SendCommandBytes += (ulong)byteCount;
            counters.SendCommandPackets++;
            lookup[entity] = counters;
        }

        /// <summary>
        /// Adds one inbound UTP Data event classified by <see cref="NetworkStreamProtocol"/>.
        /// Called from NetworkStreamReceiveSystem before GhostReceive can clobber the snapshot buffer.
        /// </summary>
        public static void TryAddRecv(
            ref ComponentLookup<TitanOrbitConnectionEgressCounters> lookup,
            Entity entity,
            byte msgType,
            int payloadBytes)
        {
            if (Hint.Likely(!MeterIsOn()) || payloadBytes <= 0 || entity == Entity.Null)
                return;
            if (!lookup.HasComponent(entity))
                return;
            var counters = lookup[entity];
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
            lookup[entity] = counters;
        }
    }
}
