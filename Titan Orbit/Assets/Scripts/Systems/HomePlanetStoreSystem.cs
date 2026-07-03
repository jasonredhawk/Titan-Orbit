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
    /// <summary>Legacy card/ship shop queries and ECS purchase delegation for OrbitStationUI.</summary>
    public class HomePlanetStoreSystem : MonoBehaviour
    {
        public static HomePlanetStoreSystem Instance { get; private set; }

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
        }

        void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        public void RequestContributedGemsServerRpc()
        {
            int homePlanetId = OrbitStationEcsContext.HomePlanetId;
            if (homePlanetId <= 0)
                return;
            MoonOrbitRpcClient.RequestContributedGems(homePlanetId);
        }

        public void PurchaseItemServerRpc(ulong homePlanetNetworkId, ulong shipNetworkId, StoreItemType itemType)
        {
            int homePlanetId = OrbitStationEcsContext.HomePlanetId;
            if (homePlanetId <= 0)
                return;
            MoonOrbitRpcClient.PurchaseStoreItem(homePlanetId, itemType);
            MoonOrbitRpcClient.RequestContributedGems(homePlanetId);
        }

        public void PurchaseComponentServerRpc(ulong homePlanetNetworkId, ulong shipNetworkId, string componentId)
        {
            Debug.LogWarning($"[HomePlanetStoreSystem] Component purchase '{componentId}' is not available in ECS yet.");
        }
    }
}
