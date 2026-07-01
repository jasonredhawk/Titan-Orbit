using System.Collections.Generic;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>Maps ship network ids to hull proxy roots that carry weapon mount children.</summary>
    public static class ShipWeaponProxyRegistry
    {
        static readonly Dictionary<int, Transform> s_HullByNetworkId = new Dictionary<int, Transform>();

        public static void Register(int networkId, Transform hullRoot)
        {
            if (networkId <= 0 || hullRoot == null)
                return;
            s_HullByNetworkId[networkId] = hullRoot;
        }

        public static void Unregister(int networkId, Transform hullRoot)
        {
            if (networkId <= 0)
                return;
            if (s_HullByNetworkId.TryGetValue(networkId, out var existing) && existing == hullRoot)
                s_HullByNetworkId.Remove(networkId);
        }

        public static bool TryGetHull(int networkId, out Transform hullRoot)
        {
            hullRoot = null;
            if (networkId <= 0)
                return false;
            return s_HullByNetworkId.TryGetValue(networkId, out hullRoot) && hullRoot != null;
        }
    }
}
