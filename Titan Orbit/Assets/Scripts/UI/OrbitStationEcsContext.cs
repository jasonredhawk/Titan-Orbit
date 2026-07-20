using UnityEngine;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Static ECS planet and ship context for legacy OrbitStationUI store RPC shims. Set when the
    /// player opens orbit station at a moon; cleared on close. Replaces NGO network object ids
    /// with authoritative PlanetId integers from ghost components. Client only.
    /// </summary>
    public static class OrbitStationEcsContext
    {
        /// <summary>True when store planet id is valid (player is docked at a store moon).</summary>
        public static bool IsActive { get; private set; }

        /// <summary>PlanetId of the moon whose store catalog is shown.</summary>
        public static int StorePlanetId { get; private set; }

        /// <summary>Player homeworld PlanetId for contributed-gem queries.</summary>
        public static int HomePlanetId { get; private set; }

        /// <summary>Current ship level from replicated ShipState (minimum 1).</summary>
        public static int ShipLevel { get; private set; } = 1;

        /// <summary>Upgrade tree branch index from ghosted <c>ShipState.BranchIndex</c>.</summary>
        public static int BranchIndex { get; private set; }

        /// <summary>
        /// Called from orbit UI when docking — caches ids for HomePlanetStoreSystem RPC forwards.
        /// </summary>
        public static void Set(int storePlanetId, int homePlanetId, int shipLevel, int branchIndex)
        {
            // --- Set ---
            IsActive = storePlanetId > 0;
            StorePlanetId = storePlanetId;
            HomePlanetId = homePlanetId;
            ShipLevel = Mathf.Max(1, shipLevel);
            BranchIndex = branchIndex;
        }

        /// <summary>Resets all fields when orbit station closes or ship undocks.</summary>
        public static void Clear()
        {
            // --- Clear state ---
            IsActive = false;
            StorePlanetId = 0;
            HomePlanetId = 0;
            ShipLevel = 1;
            BranchIndex = 0;
        }

        /// <summary>True when store RPCs should use ECS planet ids instead of NGO stubs.</summary>
        public static bool UseEcsStoreRpc => IsActive && StorePlanetId > 0;
    }
}
