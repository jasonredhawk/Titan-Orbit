using System;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Entities;
using TitanOrbit.UI;
using UnityEngine;

namespace TitanOrbit.Systems
{
    /// <summary>
    /// Legacy orbit-store purchase façade for <see cref="OrbitStationUI"/>. NGO ServerRpc names
    /// are preserved for minimal UI diff; implementations forward to <see cref="MoonOrbitRpcClient"/>
    /// ECS commands (server-authoritative), including component purchases.
    /// </summary>
    public class HomePlanetStoreSystem : MonoBehaviour
    {
        /// <summary>Singleton created by OrbitStationBootstrap or scene placement.</summary>
        public static HomePlanetStoreSystem Instance { get; private set; }

        /// <summary>[UNITY] Standard singleton Awake guard.</summary>
        void Awake()
        {
            // --- Unity lifecycle ---
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
        }

        /// <summary>Clears singleton on destroy.</summary>
        void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        /// <summary>
        /// Refreshes contributed-gem totals from server for the home planet in orbit context.
        /// </summary>
        public void RequestContributedGemsServerRpc()
        {
            // --- RequestContributedGemsServerRpc ---
            int homePlanetId = OrbitStationEcsContext.HomePlanetId;
            if (homePlanetId <= 0)
                return;

            // [NETCODE] RPC — server returns gem pool for store UI.
            MoonOrbitRpcClient.RequestContributedGems(homePlanetId);
        }

        /// <summary>
        /// Purchases a store item at the landed home planet. Ignores legacy network ids — uses ECS planet id.
        /// </summary>
        public void PurchaseItemServerRpc(ulong homePlanetNetworkId, ulong shipNetworkId, StoreItemType itemType)
        {
            // --- PurchaseItemServerRpc ---
            int homePlanetId = OrbitStationEcsContext.HomePlanetId;
            if (homePlanetId <= 0)
                return;

            // [NETCODE] Server validates gems and applies loadout on success.
            MoonOrbitRpcClient.PurchaseStoreItem(homePlanetId, itemType);
            MoonOrbitRpcClient.RequestContributedGems(homePlanetId);
        }

        /// <summary>
        /// Purchases a ship-family extra component by stable id into an empty equipment slot.
        /// </summary>
        public void PurchaseComponentServerRpc(ulong homePlanetNetworkId, ulong shipNetworkId, string componentId)
        {
            // --- PurchaseComponentServerRpc ---
            int homePlanetId = OrbitStationEcsContext.HomePlanetId;
            if (homePlanetId <= 0 || string.IsNullOrWhiteSpace(componentId))
                return;

            MoonOrbitRpcClient.PurchaseStoreComponent(homePlanetId, componentId);
            MoonOrbitRpcClient.RequestContributedGems(homePlanetId);
        }
    }
}
