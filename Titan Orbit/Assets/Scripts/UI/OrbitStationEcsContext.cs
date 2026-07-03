using UnityEngine;

namespace TitanOrbit.UI
{
    /// <summary>ECS planet ids for legacy OrbitStationUI store RPC shims.</summary>
    public static class OrbitStationEcsContext
    {
        public static bool IsActive { get; private set; }
        public static int StorePlanetId { get; private set; }
        public static int HomePlanetId { get; private set; }
        public static int ShipLevel { get; private set; } = 1;
        public static int BranchIndex { get; private set; }

        public static void Set(int storePlanetId, int homePlanetId, int shipLevel, int branchIndex)
        {
            IsActive = storePlanetId > 0;
            StorePlanetId = storePlanetId;
            HomePlanetId = homePlanetId;
            ShipLevel = Mathf.Max(1, shipLevel);
            BranchIndex = branchIndex;
        }

        public static void Clear()
        {
            IsActive = false;
            StorePlanetId = 0;
            HomePlanetId = 0;
            ShipLevel = 1;
            BranchIndex = 0;
        }

        /// <summary>True when legacy NGO NetworkObject checks can be skipped.</summary>
        public static bool UseEcsStoreRpc => IsActive && StorePlanetId > 0;
    }
}
