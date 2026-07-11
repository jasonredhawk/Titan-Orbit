using System.Collections.Generic;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Static registry mapping NetCode ship network ids to hull proxy <see cref="Transform"/> roots.
    /// <see cref="ShipWeaponMountSyncSystem"/> and client bullet VFX read this to find weapon mount children
    /// on hybrid GameObject proxies. Client presentation only — not authoritative sim state.
    /// </summary>
    public static class ShipWeaponProxyRegistry
    {
        static readonly Dictionary<int, Transform> s_HullByNetworkId = new Dictionary<int, Transform>();

        /// <summary>Records the visual hull root for a spawned ship ghost.</summary>
        public static void Register(int networkId, Transform hullRoot)
        {
            // --- Register ---
            if (networkId <= 0 || hullRoot == null)
                return;
            s_HullByNetworkId[networkId] = hullRoot;
        }

        /// <summary>Removes the mapping when the proxy is destroyed (guards against stale transforms).</summary>
        public static void Unregister(int networkId, Transform hullRoot)
        {
            // --- Unregister ---
            if (networkId <= 0)
                return;
            if (s_HullByNetworkId.TryGetValue(networkId, out var existing) && existing == hullRoot)
                s_HullByNetworkId.Remove(networkId);
        }

        /// <summary>Returns the registered hull root for a ship network id, or false when unknown.</summary>
        public static bool TryGetHull(int networkId, out Transform hullRoot)
        {
            // --- Attempt resolution ---
            hullRoot = null;
            if (networkId <= 0)
                return false;
            return s_HullByNetworkId.TryGetValue(networkId, out hullRoot) && hullRoot != null;
        }
    }
}
