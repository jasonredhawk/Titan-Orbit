using UnityEngine;

namespace Unity.Netcode
{
    // --- Type members ---
    /// <summary>
    /// [LEGACY] Compile-time type stubs so OrbitStationUI NGO-era branches still compile.
    /// Runtime paths use NetCode for Entities — these types always report not-spawned / not-server.
    /// Do not use for new features; prefer TitanOrbit.ECS and TitanOrbit.NetCode APIs.
    /// </summary>
    public class NetworkObject : MonoBehaviour
    {
        /// <summary>Always false — ECS ghosts replace NGO NetworkObject.</summary>
        public bool IsSpawned => false;

        /// <summary>Always zero — ECS uses Entity indices, not NGO network ids.</summary>
        public ulong NetworkObjectId => 0;
    }

    /// <summary>[LEGACY] Stub singleton — real session lives in TitanOrbitSessionManager.</summary>
    public class NetworkManager : MonoBehaviour
    {
        public static NetworkManager Singleton => null;
        public bool IsServer => false;
        public bool IsListening => false;
    }
}
