using Unity.Networking.Transport.Relay;
using Unity.Services.Relay.Models;

namespace TitanOrbit.NetCode
{
    /// <summary>Thread-safe relay configuration consumed by <see cref="TitanOrbitRelayDriverConstructor"/>.</summary>
    public static class TitanOrbitRelayState
    {
        static RelayServerData s_ServerRelay;
        static RelayServerData s_ClientRelay;
        static bool s_HasServerRelay;
        static bool s_HasClientRelay;

        public static void SetServerRelay(RelayServerData data)
        {
            s_ServerRelay = data;
            s_HasServerRelay = true;
        }

        public static void SetClientRelay(RelayServerData data)
        {
            s_ClientRelay = data;
            s_HasClientRelay = true;
        }

        public static void Clear()
        {
            s_HasServerRelay = false;
            s_HasClientRelay = false;
        }

        public static bool TryGetServerRelay(out RelayServerData data)
        {
            data = s_ServerRelay;
            return s_HasServerRelay;
        }

        public static bool TryGetClientRelay(out RelayServerData data)
        {
            data = s_ClientRelay;
            return s_HasClientRelay;
        }
    }

    public static class TitanOrbitRelayUtility
    {
        public static RelayServerData FromAllocation(Allocation allocation, string connectionType = "dtls")
        {
            return allocation.ToRelayServerData(connectionType);
        }

        public static RelayServerData FromJoinAllocation(JoinAllocation allocation, string connectionType = "dtls")
        {
            return allocation.ToRelayServerData(connectionType);
        }

        public static string ConnectionTypeForPlatform(string overrideType = null)
        {
            if (!string.IsNullOrEmpty(overrideType))
                return overrideType.ToLowerInvariant();
#if UNITY_WEBGL && !UNITY_EDITOR
            return "wss";
#else
            return "dtls";
#endif
        }
    }
}
