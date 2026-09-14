using TitanOrbit;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using Unity.Networking.Transport;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// Syncs the Burst-readable egress hook and ensures every connection entity has counters.
    /// <para>
    /// Unity.NetCode cannot reference <see cref="TitanOrbitDebugFlags"/>, so this system copies
    /// the GameManager flag into <see cref="TitanOrbitEgressMeterHook.Enabled"/> before GhostSend
    /// runs. Also adds <see cref="TitanOrbitConnectionEgressCounters"/> on any connection that
    /// missed the Connect/Accept path (host-migration, fake host).
    /// </para>
    /// World: ClientSimulation and ServerSimulation. Group: InitializationSystemGroup (early).
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(InitializationSystemGroup), OrderFirst = true)]
    public partial struct TitanOrbitEgressMeterHookSyncSystem : ISystem
    {
        EntityQuery _missingCountersQuery;

        /// <summary>
        /// Caches a query for connection entities that still lack egress counters.
        /// </summary>
        public void OnCreate(ref SystemState state)
        {
            _missingCountersQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<NetworkStreamConnection>(),
                ComponentType.Exclude<TitanOrbitConnectionEgressCounters>());
        }

        /// <summary>
        /// Publishes the Inspector toggle into Burst SharedStatic, then backfills missing counters
        /// only while the meter is on (structural change is rare).
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // --- Burst hook ---
            // [TITAN-ORBIT] GhostSend / Rpc / CommandSend / Receive jobs read this SharedStatic.
            TitanOrbitEgressMeterHook.Enabled.Data = TitanOrbitDebugFlags.EgressMeterEnabled;

            if (!TitanOrbitDebugFlags.EgressMeterEnabled)
                return;

            // --- Backfill ---
            // [ECS/DOTS] Connect and Accept already add the component. This covers leftover paths.
            if (_missingCountersQuery.IsEmptyIgnoreFilter)
                return;

            var em = state.EntityManager;
            using var entities = _missingCountersQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
                em.AddComponent<TitanOrbitConnectionEgressCounters>(entities[i]);
        }
    }

    /// <summary>
    /// Copies this ClientWorld connection's receive/upload counters into
    /// <see cref="TitanOrbitEgressMeter"/> for the overlay. Also samples Editor ghost-type stats
    /// and Relay / UTP header hints.
    /// World: ClientSimulation. Group: SimulationSystemGroup, last — after receive and send jobs.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderLast = true)]
    public partial struct TitanOrbitEgressMeterClientCopySystem : ISystem
    {
        bool _metricsMonitorReady;

        /// <summary>
        /// Last <see cref="TitanOrbitEgressMeter.ResetVersion"/> this ClientWorld applied.
        /// Separate from ServerWorld so Local Host reset cannot skip one world's ECS counters.
        /// </summary>
        int _appliedResetVersion;

        /// <summary>
        /// Publishes client receive + upload totals. No work when the meter is off.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            if (!TitanOrbitDebugFlags.EgressMeterEnabled)
                return;

            ApplyResetIfRequested(ref state);

            // --- Sum this client's connection(s) ---
            // [NETCODE] A dedicated client has one connection entity. Local Host IPC is the same.
            ulong recvSnap = 0;
            ulong recvRpc = 0;
            ulong recvPkts = 0;
            ulong sendCmd = 0;
            ulong sendCmdPkts = 0;
            ulong sendRpc = 0;
            ulong sendRpcPkts = 0;

            foreach (var counters in SystemAPI.Query<RefRO<TitanOrbitConnectionEgressCounters>>())
            {
                var c = counters.ValueRO;
                recvSnap += c.RecvSnapshotBytes;
                recvRpc += c.RecvRpcBytes;
                recvPkts += c.RecvPacketCount;
                sendCmd += c.SendCommandBytes;
                sendCmdPkts += c.SendCommandPackets;
                sendRpc += c.SendRpcBytes;
                sendRpcPkts += c.SendRpcPackets;
            }

            TitanOrbitEgressMeter.ClientRecvSnapshotBytes = recvSnap;
            TitanOrbitEgressMeter.ClientRecvRpcBytes = recvRpc;
            TitanOrbitEgressMeter.ClientRecvPacketCount = recvPkts;
            TitanOrbitEgressMeter.ClientSendCommandBytes = sendCmd;
            TitanOrbitEgressMeter.ClientSendCommandPackets = sendCmdPkts;
            TitanOrbitEgressMeter.ClientSendRpcBytes = sendRpc;
            TitanOrbitEgressMeter.ClientSendRpcPackets = sendRpcPkts;
            TitanOrbitEgressMeter.RelayActive = TitanOrbitRelayState.HasClientRelay;
            TitanOrbitEgressMeter.UtpHeaderBytes = TryReadUtpHeaderBytes(ref state);
            TitanOrbitEgressMeter.HasSample = true;

#if UNITY_EDITOR || NETCODE_DEBUG
            EnsureGhostMetricsMonitor(ref state);
            CopyGhostTypeBreakdown(ref state);
#endif
        }

        /// <summary>
        /// Zeros ECS counters on this ClientWorld when the HUD requested a session reset.
        /// Does not ClearSnapshot — that would wipe ServerWorld rows in the same process.
        /// </summary>
        void ApplyResetIfRequested(ref SystemState state)
        {
            if (_appliedResetVersion == TitanOrbitEgressMeter.ResetVersion)
                return;

            foreach (var counters in SystemAPI.Query<RefRW<TitanOrbitConnectionEgressCounters>>())
                counters.ValueRW = default;

            _appliedResetVersion = TitanOrbitEgressMeter.ResetVersion;
        }

        /// <summary>
        /// Reads UTP unreliable-pipeline header size so the HUD can estimate UDP+UTP wire bytes.
        /// Returns 20 if the driver is not ready (join).
        /// </summary>
        static int TryReadUtpHeaderBytes(ref SystemState state)
        {
            if (!SystemAPI.TryGetSingleton<NetworkStreamDriver>(out var driver))
                return 20;

            ref var store = ref driver.DriverStore;
            if (store.DriversCount < 1)
                return 20;

            ref readonly var inst = ref store.GetDriverInstanceRO(store.FirstDriver);
            if (!inst.driver.IsCreated)
                return 20;

            int header = inst.driver.MaxHeaderSize(inst.unreliablePipeline);
            return header > 0 ? header : 20;
        }

#if UNITY_EDITOR || NETCODE_DEBUG
        /// <summary>
        /// Creates a GhostMetricsMonitor singleton so GhostStatsCollectionSystem fills per-type sizes.
        /// Editor / NETCODE_DEBUG only — those types do not exist in player builds without the define.
        /// </summary>
        void EnsureGhostMetricsMonitor(ref SystemState state)
        {
            if (_metricsMonitorReady || SystemAPI.HasSingleton<GhostMetricsMonitor>())
            {
                _metricsMonitorReady = true;
                return;
            }

            var em = state.EntityManager;
            var entity = em.CreateEntity();
            em.AddComponent<GhostMetricsMonitor>(entity);
            em.AddBuffer<GhostNames>(entity);
            em.AddBuffer<GhostMetrics>(entity);
            em.SetName(entity, "TitanOrbitEgressMetricsMonitor");
            _metricsMonitorReady = true;
        }

        /// <summary>
        /// Buckets this client's last snapshot into ship / planet / gem / other using GhostMetrics
        /// (bits) plus GhostNames. No managed string alloc — FixedString IndexOf only.
        /// </summary>
        static void CopyGhostTypeBreakdown(ref SystemState state)
        {
            TitanOrbitEgressMeter.GhostShipBits = 0;
            TitanOrbitEgressMeter.GhostShipCount = 0;
            TitanOrbitEgressMeter.GhostPlanetBits = 0;
            TitanOrbitEgressMeter.GhostPlanetCount = 0;
            TitanOrbitEgressMeter.GhostGemBits = 0;
            TitanOrbitEgressMeter.GhostGemCount = 0;
            TitanOrbitEgressMeter.GhostOtherBits = 0;
            TitanOrbitEgressMeter.GhostOtherCount = 0;

            if (!SystemAPI.TryGetSingletonBuffer<GhostMetrics>(out var metrics) || metrics.Length == 0)
                return;

            bool haveNames = SystemAPI.TryGetSingletonBuffer<GhostNames>(out var names);

            int count = metrics.Length;
            for (int i = 0; i < count; i++)
            {
                FixedString64Bytes typeName = default;
                if (haveNames && i < names.Length)
                    typeName = names[i].Name;

                ClassifyGhostType(in typeName, metrics[i].SizeInBits, metrics[i].InstanceCount);
            }
        }

        static readonly FixedString32Bytes NeedleShip = "Ship";
        static readonly FixedString32Bytes NeedleShipLower = "ship";
        static readonly FixedString32Bytes NeedlePlanet = "Planet";
        static readonly FixedString32Bytes NeedlePlanetLower = "planet";
        static readonly FixedString32Bytes NeedleGem = "Gem";
        static readonly FixedString32Bytes NeedleGemLower = "gem";

        /// <summary>
        /// Maps a ghost prefab name onto ship / planet / gem / other buckets.
        /// Names come from bake (StarshipGhost, PlanetGhost, GemGhost).
        /// </summary>
        static void ClassifyGhostType(in FixedString64Bytes typeName, uint sizeInBits, uint instanceCount)
        {
            if (typeName.IndexOf(NeedleShip) >= 0 || typeName.IndexOf(NeedleShipLower) >= 0)
            {
                TitanOrbitEgressMeter.GhostShipBits += sizeInBits;
                TitanOrbitEgressMeter.GhostShipCount += instanceCount;
            }
            else if (typeName.IndexOf(NeedlePlanet) >= 0 || typeName.IndexOf(NeedlePlanetLower) >= 0)
            {
                TitanOrbitEgressMeter.GhostPlanetBits += sizeInBits;
                TitanOrbitEgressMeter.GhostPlanetCount += instanceCount;
            }
            else if (typeName.IndexOf(NeedleGem) >= 0 || typeName.IndexOf(NeedleGemLower) >= 0)
            {
                TitanOrbitEgressMeter.GhostGemBits += sizeInBits;
                TitanOrbitEgressMeter.GhostGemCount += instanceCount;
            }
            else
            {
                TitanOrbitEgressMeter.GhostOtherBits += sizeInBits;
                TitanOrbitEgressMeter.GhostOtherCount += instanceCount;
            }
        }
#endif
    }

    /// <summary>
    /// Copies each ServerWorld connection's <c>EndSend</c> snapshot + RPC totals into
    /// <see cref="TitanOrbitEgressMeter.ServerConnections"/> so Local Host can compare
    /// server send vs client receive per <c>NetworkId</c>.
    /// World: ServerSimulation. Group: SimulationSystemGroup, last.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderLast = true)]
    public partial struct TitanOrbitEgressMeterServerCopySystem : ISystem
    {
        /// <summary>
        /// Last <see cref="TitanOrbitEgressMeter.ResetVersion"/> this ServerWorld applied.
        /// Independent of the client copy system (Local Host has both worlds in one process).
        /// </summary>
        int _appliedResetVersion;

        /// <summary>
        /// Fills up to <see cref="TitanOrbitEgressMeter.MaxServerConnections"/> rows.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            if (!TitanOrbitDebugFlags.EgressMeterEnabled)
                return;

            if (_appliedResetVersion != TitanOrbitEgressMeter.ResetVersion)
            {
                foreach (var counters in SystemAPI.Query<RefRW<TitanOrbitConnectionEgressCounters>>())
                    counters.ValueRW = default;
                _appliedResetVersion = TitanOrbitEgressMeter.ResetVersion;
            }

            int written = 0;
            foreach (var (counters, networkId) in SystemAPI
                         .Query<RefRO<TitanOrbitConnectionEgressCounters>, RefRO<NetworkId>>())
            {
                if (written >= TitanOrbitEgressMeter.MaxServerConnections)
                    break;

                var c = counters.ValueRO;
                TitanOrbitEgressMeter.ServerConnections[written] = new TitanOrbitEgressMeter.ServerConnRow
                {
                    NetworkId = networkId.ValueRO.Value,
                    SendSnapshotBytes = c.SendSnapshotBytes,
                    SendRpcBytes = c.SendRpcBytes,
                    SendPackets = c.SendSnapshotPackets + c.SendRpcPackets,
                };
                written++;
            }

            for (int i = written; i < TitanOrbitEgressMeter.MaxServerConnections; i++)
                TitanOrbitEgressMeter.ServerConnections[i] = default;

            TitanOrbitEgressMeter.ServerConnectionCount = written;
            TitanOrbitEgressMeter.HasSample = true;
        }
    }
}
