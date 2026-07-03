using UnityEngine;

namespace Unity.Netcode
{
    /// <summary>
    /// Compile-time stubs for legacy OrbitStationUI NGO branches. ECS paths are used at runtime.
    /// </summary>
    public class NetworkObject : MonoBehaviour
    {
        public bool IsSpawned => false;
        public ulong NetworkObjectId => 0;
    }

    public class NetworkManager : MonoBehaviour
    {
        public static NetworkManager Singleton => null;
        public bool IsServer => false;
        public bool IsListening => false;
    }
}
