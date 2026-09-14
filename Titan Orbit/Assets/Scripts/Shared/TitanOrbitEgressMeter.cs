using System;

namespace TitanOrbit
{
    /// <summary>
    /// Process-wide snapshot of measured NetCode payload for the egress overlay.
    /// <para>
    /// ECS copy systems write these running totals every tick while
    /// <see cref="TitanOrbitDebugFlags.EgressMeterEnabled"/> is true.
    /// <c>ClientEgressMeterHUD</c> reads them on the main thread once per second to derive KB/s.
    /// This lives in Shared so Core / Game / NetCode can share the numbers without a cycle.
    /// </para>
    /// <para>
    /// [TITAN-ORBIT] Totals are NetCode payload bytes (snapshot / RPC / command datagrams),
    /// not UDP/IP or Relay encapsulation. The HUD adds an estimated header line from packet counts.
    /// </para>
    /// Not thread-safe beyond Unity's main thread (system OnUpdate + OnGUI).
    /// </summary>
    public static class TitanOrbitEgressMeter
    {
        /// <summary>Cap on Local Host server-send rows shown in the overlay.</summary>
        public const int MaxServerConnections = 16;

        /// <summary>One server connection's outbound payload since connect (or last reset).</summary>
        public struct ServerConnRow
        {
            /// <summary>NetCode <c>NetworkId</c> (1-based). 0 means unused slot.</summary>
            public int NetworkId;

            /// <summary>Ghost snapshot bytes the server <c>EndSend</c>'d to this connection.</summary>
            public ulong SendSnapshotBytes;

            /// <summary>Reliable RPC packet bytes the server <c>EndSend</c>'d to this connection.</summary>
            public ulong SendRpcBytes;

            /// <summary>Snapshot + RPC packets actually handed to UTP for this connection.</summary>
            public ulong SendPackets;
        }

        /// <summary>
        /// Bumped by the HUD when the player hits Reset. Each copy system keeps its own
        /// applied version so ClientWorld and ServerWorld can zero their own ECS counters
        /// without wiping each other's overlay fields (Local Host runs both worlds).
        /// </summary>
        public static int ResetVersion;

        /// <summary>Client receive: ghost snapshot payload (server → this player).</summary>
        public static ulong ClientRecvSnapshotBytes;

        /// <summary>Client receive: RPC payload (server → this player, plus any other inbound RPCs).</summary>
        public static ulong ClientRecvRpcBytes;

        /// <summary>Client receive: inbound Data events counted (snapshots + RPCs + other).</summary>
        public static ulong ClientRecvPacketCount;

        /// <summary>Client upload: command packets (this player → server). Not egress.</summary>
        public static ulong ClientSendCommandBytes;

        /// <summary>Client upload: command packet count.</summary>
        public static ulong ClientSendCommandPackets;

        /// <summary>Client upload: RPCs this client sent (requests). Not egress.</summary>
        public static ulong ClientSendRpcBytes;

        /// <summary>Client upload: RPC packets this client sent.</summary>
        public static ulong ClientSendRpcPackets;

        /// <summary>How many <see cref="ServerConnections"/> slots are valid this tick.</summary>
        public static int ServerConnectionCount;

        /// <summary>Local Host / MPPM host: per-connection server send. Unused slots have NetworkId 0.</summary>
        public static readonly ServerConnRow[] ServerConnections = new ServerConnRow[MaxServerConnections];

        /// <summary>True when this client stored a Unity Relay join allocation.</summary>
        public static bool RelayActive;

        /// <summary>
        /// UTP pipeline header bytes per packet (queried from the driver when possible).
        /// HUD adds this plus 28 (UDP+IPv4) per packet for a wire estimate.
        /// </summary>
        public static int UtpHeaderBytes = 20;

        /// <summary>Editor: bits of this client's last snapshot attributed to ship ghosts.</summary>
        public static ulong GhostShipBits;

        /// <summary>Editor: ship ghost instances in that snapshot.</summary>
        public static uint GhostShipCount;

        /// <summary>Editor: bits attributed to planet ghosts.</summary>
        public static ulong GhostPlanetBits;

        /// <summary>Editor: planet ghost instances in that snapshot.</summary>
        public static uint GhostPlanetCount;

        /// <summary>Editor: bits attributed to gem ghosts.</summary>
        public static ulong GhostGemBits;

        /// <summary>Editor: gem ghost instances in that snapshot.</summary>
        public static uint GhostGemCount;

        /// <summary>Editor: bits for any other ghost types (transports, singletons, …).</summary>
        public static ulong GhostOtherBits;

        /// <summary>Editor: other ghost instances in that snapshot.</summary>
        public static uint GhostOtherCount;

        /// <summary>True after at least one copy system ran with the meter on this session.</summary>
        public static bool HasSample;

        /// <summary>
        /// HUD calls this so the next copy-system tick zeros ECS counters.
        /// Also clears these overlay totals immediately so the panel does not keep stale KB/s.
        /// Use when you want a clean "sit still vs fight" window.
        /// </summary>
        public static void RequestReset()
        {
            ResetVersion++;
            ClearSnapshot();
        }

        /// <summary>
        /// Clears the Shared snapshot (not ECS). HUD calls this from <see cref="RequestReset"/>.
        /// Copy systems must not call this — ClientWorld and ServerWorld share this static state.
        /// </summary>
        public static void ClearSnapshot()
        {
            ClientRecvSnapshotBytes = 0;
            ClientRecvRpcBytes = 0;
            ClientRecvPacketCount = 0;
            ClientSendCommandBytes = 0;
            ClientSendCommandPackets = 0;
            ClientSendRpcBytes = 0;
            ClientSendRpcPackets = 0;
            ServerConnectionCount = 0;
            Array.Clear(ServerConnections, 0, MaxServerConnections);
            GhostShipBits = 0;
            GhostShipCount = 0;
            GhostPlanetBits = 0;
            GhostPlanetCount = 0;
            GhostGemBits = 0;
            GhostGemCount = 0;
            GhostOtherBits = 0;
            GhostOtherCount = 0;
            HasSample = false;
        }
    }
}
