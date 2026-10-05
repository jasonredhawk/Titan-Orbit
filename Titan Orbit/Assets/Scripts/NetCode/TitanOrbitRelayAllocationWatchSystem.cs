using Unity.Entities;
using Unity.NetCode;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Relay;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// Relay sets <see cref="RelayConnectionStatus.AllocationInvalid"/> when the driver stops
    /// pinging for about 10 seconds. That state cannot be healed on the old socket: the host
    /// must allocate again, and the client must build a new driver. These systems only record
    /// the status; session code performs the rebind or the client reset.
    /// </summary>
    public static class TitanOrbitRelayAllocationSignal
    {
        static byte s_ClientInvalid;
        static byte s_ServerInvalid;

        public static void MarkClientInvalid() => s_ClientInvalid = 1;

        public static void MarkServerInvalid() => s_ServerInvalid = 1;

        public static bool ConsumeClientInvalid()
        {
            if (s_ClientInvalid == 0)
                return false;
            s_ClientInvalid = 0;
            return true;
        }

        public static bool ConsumeServerInvalid()
        {
            if (s_ServerInvalid == 0)
                return false;
            s_ServerInvalid = 0;
            return true;
        }

        public static void ClearServerInvalid() => s_ServerInvalid = 0;
    }

    /// <summary>Client world: remember when this player's Relay allocation dies.</summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderLast = true)]
    public partial struct TitanOrbitClientRelayAllocationWatchSystem : ISystem
    {
        byte _invalidFrames;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<NetworkStreamDriver>();
        }

        public void OnUpdate(ref SystemState state)
        {
            if (!TitanOrbitRelayState.HasClientRelay)
            {
                _invalidFrames = 0;
                return;
            }

            if (!TitanOrbitRelayAllocationWatch.IsAllocationInvalid(SystemAPI.GetSingleton<NetworkStreamDriver>()))
            {
                _invalidFrames = 0;
                return;
            }

            // One torn read must not reset a live match. Half a second of the same status is enough.
            if (_invalidFrames < 30)
            {
                _invalidFrames++;
                return;
            }

            TitanOrbitRelayAllocationSignal.MarkClientInvalid();
        }
    }

    /// <summary>Server world: remember when the host allocation dies so the lobby join code can be replaced.</summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderLast = true)]
    public partial struct TitanOrbitServerRelayAllocationWatchSystem : ISystem
    {
        byte _invalidFrames;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<NetworkStreamDriver>();
        }

        public void OnUpdate(ref SystemState state)
        {
            if (!TitanOrbitRelayState.TryGetServerRelay(out _))
            {
                _invalidFrames = 0;
                return;
            }

            if (TitanOrbitSessionManager.Instance != null &&
                TitanOrbitSessionManager.Instance.IsRecreateDedicatedMatchInProgress)
                return;

            if (!TitanOrbitRelayAllocationWatch.IsAllocationInvalid(SystemAPI.GetSingleton<NetworkStreamDriver>()))
            {
                _invalidFrames = 0;
                return;
            }

            if (_invalidFrames < 30)
            {
                _invalidFrames++;
                return;
            }

            TitanOrbitRelayAllocationSignal.MarkServerInvalid();
        }
    }

    static class TitanOrbitRelayAllocationWatch
    {
        public static bool IsAllocationInvalid(NetworkStreamDriver stream)
        {
            ref var store = ref stream.DriverStore;
            for (int i = store.FirstDriver; i < store.LastDriver; ++i)
            {
                ref readonly NetworkDriver driver = ref store.GetDriverRO(i);
                if (!driver.IsCreated)
                    continue;
                if (driver.GetRelayConnectionStatus() == RelayConnectionStatus.AllocationInvalid)
                    return true;
            }

            return false;
        }
    }
}
