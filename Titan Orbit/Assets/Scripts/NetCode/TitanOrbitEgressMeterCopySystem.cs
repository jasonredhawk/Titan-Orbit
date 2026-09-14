using TitanOrbit;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using Unity.Networking.Transport;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// Adds this tick's inbound snapshot + RPC payload into <see cref="TitanOrbitEgressMeter"/>
    /// after UTP receive and before GhostReceive consumes the snapshot buffer.
    /// <para>
    /// <see cref="SystemBase"/> (managed, never Burst) so it cannot crash Local Host the way a
    /// GhostSend/Receive job hook can. Off = immediate return. No LINQ, no GetAllEntities.
    /// </para>
    /// World: ClientSimulation. Group: NetworkReceiveSystemGroup, after
    /// <see cref="NetworkStreamReceiveSystem"/>.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(NetworkReceiveSystemGroup))]
    [UpdateAfter(typeof(NetworkStreamReceiveSystem))]
    public partial class TitanOrbitEgressMeterReceiveSampleSystem : SystemBase
    {
        /// <summary>
        /// Accumulates inbound snapshot/RPC bytes still sitting on the connection this tick.
        /// GhostReceive (later, GhostSimulationSystemGroup) then consumes the snapshot buffer.
        /// </summary>
        protected override void OnUpdate()
        {
            if (!TitanOrbitDebugFlags.EgressMeterEnabled)
                return;

            foreach (var (snapshots, rpcs) in SystemAPI
                         .Query<DynamicBuffer<IncomingSnapshotDataStreamBuffer>,
                             DynamicBuffer<IncomingRpcDataStreamBuffer>>())
            {
                int snapLen = snapshots.Length;
                int rpcLen = rpcs.Length;
                if (snapLen > 0)
                {
                    TitanOrbitEgressMeter.ClientRecvSnapshotBytes += (ulong)snapLen;
                    TitanOrbitEgressMeter.ClientRecvPacketCount++;
                }
                if (rpcLen > 0)
                {
                    TitanOrbitEgressMeter.ClientRecvRpcBytes += (ulong)rpcLen;
                    TitanOrbitEgressMeter.ClientRecvPacketCount++;
                }
            }

            TitanOrbitEgressMeter.HasSample = true;
        }
    }

    /// <summary>
    /// Copies UTP header size, a Relay-join flag (should stay false — play is direct UDP),
    /// and Editor ghost-type stats for the overlay.
    /// Receive byte totals are accumulated by
    /// <see cref="TitanOrbitEgressMeterReceiveSampleSystem"/> (do not overwrite them here).
    /// World: ClientSimulation. Group: SimulationSystemGroup, last.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderLast = true)]
    public partial class TitanOrbitEgressMeterClientCopySystem : SystemBase
    {
        bool _metricsMonitorReady;

        /// <summary>
        /// Publishes header hints and ghost rows. No work when the meter is off.
        /// </summary>
        protected override void OnUpdate()
        {
            if (!TitanOrbitDebugFlags.EgressMeterEnabled)
                return;

            TitanOrbitEgressMeter.RelayActive = TitanOrbitRelayState.HasClientRelay;
            TitanOrbitEgressMeter.UtpHeaderBytes = ReadUtpHeaderBytes();
            TitanOrbitEgressMeter.HasSample = true;

#if UNITY_EDITOR || NETCODE_DEBUG
            EnsureGhostMetricsMonitor();
            CopyGhostTypeBreakdown();
#endif
        }

        /// <summary>
        /// Reads UTP unreliable-pipeline header size so the HUD can estimate UDP+UTP wire bytes.
        /// Returns 20 if the driver is not ready (join).
        /// </summary>
        int ReadUtpHeaderBytes()
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
        void EnsureGhostMetricsMonitor()
        {
            if (_metricsMonitorReady || SystemAPI.HasSingleton<GhostMetricsMonitor>())
            {
                _metricsMonitorReady = true;
                return;
            }

            var entity = EntityManager.CreateEntity();
            EntityManager.AddComponent<GhostMetricsMonitor>(entity);
            EntityManager.AddBuffer<GhostNames>(entity);
            EntityManager.AddBuffer<GhostMetrics>(entity);
            EntityManager.SetName(entity, "TitanOrbitEgressMetricsMonitor");
            _metricsMonitorReady = true;
        }

        /// <summary>
        /// Buckets this client's last snapshot into ship / planet / gem / other using GhostMetrics
        /// (bits) plus GhostNames. No managed string alloc — FixedString IndexOf only.
        /// </summary>
        void CopyGhostTypeBreakdown()
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

                ClassifyGhostType(typeName, metrics[i].SizeInBits, metrics[i].InstanceCount);
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
        static void ClassifyGhostType(FixedString64Bytes typeName, uint sizeInBits, uint instanceCount)
        {
            FixedString32Bytes ship = NeedleShip;
            FixedString32Bytes shipLower = NeedleShipLower;
            FixedString32Bytes planet = NeedlePlanet;
            FixedString32Bytes planetLower = NeedlePlanetLower;
            FixedString32Bytes gem = NeedleGem;
            FixedString32Bytes gemLower = NeedleGemLower;

            if (typeName.IndexOf(ship) >= 0 || typeName.IndexOf(shipLower) >= 0)
            {
                TitanOrbitEgressMeter.GhostShipBits += sizeInBits;
                TitanOrbitEgressMeter.GhostShipCount += instanceCount;
            }
            else if (typeName.IndexOf(planet) >= 0 || typeName.IndexOf(planetLower) >= 0)
            {
                TitanOrbitEgressMeter.GhostPlanetBits += sizeInBits;
                TitanOrbitEgressMeter.GhostPlanetCount += instanceCount;
            }
            else if (typeName.IndexOf(gem) >= 0 || typeName.IndexOf(gemLower) >= 0)
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
}
