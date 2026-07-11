using System;
using System.Collections.Generic;
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
    /// ECS commands (server-authoritative). Component purchases log not-implemented until ported.
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
        /// [LEGACY] Component-by-id purchase — not wired to ECS store yet.
        /// </summary>
        public void PurchaseComponentServerRpc(ulong homePlanetNetworkId, ulong shipNetworkId, string componentId)
        {
            Debug.LogWarning($"[HomePlanetStoreSystem] Component purchase '{componentId}' is not available in ECS yet.");
        }
    }
}
