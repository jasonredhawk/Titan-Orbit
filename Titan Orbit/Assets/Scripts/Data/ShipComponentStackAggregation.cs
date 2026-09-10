using System;
using System.Collections.Generic;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Groups hull parts into Extra Level pools and picks a <b>primary</b> for HUD / visuals.
    /// <para>
    /// [TITAN-ORBIT] Combat numbers no longer use primary-only Base + primary PerExtra × (N−1).
    /// <see cref="ShipComponentExtraLevelMath.AggregateAndEvaluate"/> Extra-Levels each part
    /// with that part’s own PerExtra and sums them (Engine PerExtra ≠ Thruster PerExtra).
    /// </para>
    /// <para>
    /// Primary for display: the newest moon-store extra in the pool (purchased gear becomes
    /// the main part). When the pool is chassis-only, we keep the highest-valued prefab part.
    /// Engines + Thrusters share <see cref="PropulsionPoolKey"/>.
    /// </para>
    /// </summary>
    public static class ShipComponentStackAggregation
    {
        /// <summary>Shared pool key for engines and thrusters.</summary>
        public const string PropulsionPoolKey = "Propulsion";

        /// <summary>
        /// One stack pool after primary selection — feeds Extra Level evaluation.
        /// </summary>
        public struct PoolContribution
        {
            /// <summary>Pool key (Weapon, Propulsion, Cockpit, …).</summary>
            public string PoolKey;

            /// <summary>Stats from the display primary (newest store extra, else highest-valued).</summary>
            public ShipComponentAbilityStats PrimaryStats;

            /// <summary>Total members in the pool (including primary).</summary>
            public int ComponentCount;

            /// <summary>True when this pool is weapons (fire power divides by count).</summary>
            public bool IsWeaponPool;
        }

        /// <summary>
        /// Pool key for stacking: Engines+Thrusters → <see cref="PropulsionPoolKey"/>;
        /// otherwise canonical part type (Wing, Cockpit, Weapon, …).
        /// </summary>
        public static string ResolveStackPoolKey(string componentId)
        {
            if (ShipComponentAbilityStats.IsPropulsionComponent(componentId))
                return PropulsionPoolKey;

            string type = ShipComponentAbilityStats.ResolvePartTypeForSuggestedStats(componentId);
            if (string.IsNullOrWhiteSpace(type))
                return "Other";

            // [TITAN-ORBIT] Weapon Bullet / Weapon Cannon share one Weapon pool for Extra Level count.
            if (ShipFamilyPartTypes.IsWeapon(type))
                return "Weapon";

            return type.Trim();
        }

        /// <summary>True when the pool key is the shared weapon pool.</summary>
        public static bool IsWeaponPoolKey(string poolKey) =>
            string.Equals(poolKey, "Weapon", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Rebuilds hull <b>primary</b> totals from per-part lists (no Extra Level yet).
        /// Call before family fallbacks; evaluate later with <see cref="ShipComponentExtraLevelMath"/>.
        /// </summary>
        public static ShipComponentAbilityStats AggregateAllPools(
            IReadOnlyList<string> componentIds,
            IReadOnlyList<ShipComponentAbilityStats> perComponentStats)
        {
            AggregatePrimaries(componentIds, perComponentStats, out ShipComponentAbilityStats combined, out _);
            return combined;
        }

        /// <summary>
        /// Primary-per-pool aggregation plus the list of pool contributions (count + primary stats).
        /// </summary>
        public static void AggregatePrimaries(
            IReadOnlyList<string> componentIds,
            IReadOnlyList<ShipComponentAbilityStats> perComponentStats,
            out ShipComponentAbilityStats combinedPrimary,
            out List<PoolContribution> pools)
        {
            combinedPrimary = default;
            pools = new List<PoolContribution>(8);

            if (componentIds == null || perComponentStats == null)
                return;

            int count = Mathf.Min(componentIds.Count, perComponentStats.Count);
            if (count <= 0)
                return;

            // --- Group indices by pool key ---
            var groups = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < count; i++)
            {
                string id = componentIds[i];
                if (string.IsNullOrWhiteSpace(id))
                    continue;
                if (ShipFamilyPartCalcProfileSet.IsCosmeticPartName(id))
                    continue;

                string key = ResolveStackPoolKey(id);
                if (!groups.TryGetValue(key, out List<int> list))
                {
                    list = new List<int>(4);
                    groups[key] = list;
                }

                list.Add(i);
            }

            foreach (KeyValuePair<string, List<int>> pair in groups)
            {
                PoolContribution contrib = AggregatePoolPrimary(
                    pair.Key, pair.Value, componentIds, perComponentStats);
                pools.Add(contrib);
                combinedPrimary.AddInPlace(contrib.PrimaryStats);
            }
        }

        /// <summary>
        /// Primary-only aggregate for one pool. Extras do not add base stats.
        /// </summary>
        public static PoolContribution AggregatePoolPrimary(
            string poolKey,
            List<int> memberIndices,
            IReadOnlyList<string> componentIds,
            IReadOnlyList<ShipComponentAbilityStats> perComponentStats)
        {
            var result = new PoolContribution
            {
                PoolKey = poolKey ?? string.Empty,
                PrimaryStats = default,
                ComponentCount = 0,
                IsWeaponPool = IsWeaponPoolKey(poolKey),
            };

            if (memberIndices == null || memberIndices.Count == 0)
                return result;

            int primaryLocal = PickPrimaryLocalIndex(
                poolKey, memberIndices, perComponentStats);
            int primaryGlobal = memberIndices[primaryLocal];

            result.PrimaryStats = perComponentStats[primaryGlobal];
            result.ComponentCount = memberIndices.Count;
            return result;
        }

        /// <summary>
        /// [LEGACY name] Primary-only pool aggregate for older call sites.
        /// Display-primary stats only. Live Extra Level evaluates every member with its own PerExtra.
        /// </summary>
        public static ShipComponentAbilityStats AggregatePoolWeighted(
            string poolKey,
            List<int> memberIndices,
            IReadOnlyList<string> componentIds,
            IReadOnlyList<ShipComponentAbilityStats> perComponentStats)
        {
            return AggregatePoolPrimary(poolKey, memberIndices, componentIds, perComponentStats).PrimaryStats;
        }

        /// <summary>
        /// Primary index within <paramref name="memberIndices"/> (local list index, not global).
        /// Newest moon-store extra in the pool wins (Orbit Menu purchase becomes the main part).
        /// Chassis-only pools: propulsion uses highest moveSpeed; others use the additive score.
        /// </summary>
        /// <param name="storeExtraStartIndex">
        /// First list index that is a moon-store extra (<see cref="int.MaxValue"/> = none).
        /// Store rows are appended after prefab children, so the last extra in the pool is newest.
        /// </param>
        public static int PickPrimaryLocalIndex(
            string poolKey,
            List<int> memberIndices,
            IReadOnlyList<ShipComponentAbilityStats> perComponentStats,
            int storeExtraStartIndex = int.MaxValue)
        {
            if (memberIndices == null || memberIndices.Count == 0)
                return 0;

            // --- Purchased gear becomes the display / visual primary ---
            // [TITAN-ORBIT] Extras are appended after chassis parts. The last extra in this
            // pool is the most recent Orbit Menu buy (second cockpit, foreign engine, …).
            if (storeExtraStartIndex < int.MaxValue)
            {
                int lastExtraLocal = -1;
                for (int m = 0; m < memberIndices.Count; m++)
                {
                    if (memberIndices[m] >= storeExtraStartIndex)
                        lastExtraLocal = m;
                }

                if (lastExtraLocal >= 0)
                    return lastExtraLocal;
            }

            int bestLocal = 0;
            float bestScore = float.NegativeInfinity;
            bool propulsion = string.Equals(poolKey, PropulsionPoolKey, StringComparison.OrdinalIgnoreCase);

            for (int m = 0; m < memberIndices.Count; m++)
            {
                ShipComponentAbilityStats s = perComponentStats[memberIndices[m]];
                float score = propulsion ? ScorePropulsionPrimary(s) : ScoreGenericPrimary(s);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestLocal = m;
                }
            }

            return bestLocal;
        }

        /// <summary>Global list index of the propulsion primary, or -1.</summary>
        /// <param name="storeExtraStartIndex">First moon-store extra index, or <see cref="int.MaxValue"/>.</param>
        public static int PickPropulsionPrimaryGlobalIndex(
            IReadOnlyList<string> componentIds,
            IReadOnlyList<ShipComponentAbilityStats> perComponentStats,
            int storeExtraStartIndex = int.MaxValue)
        {
            if (componentIds == null || perComponentStats == null)
                return -1;

            int count = Mathf.Min(componentIds.Count, perComponentStats.Count);
            var members = new List<int>(4);
            for (int i = 0; i < count; i++)
            {
                if (!ShipComponentAbilityStats.IsPropulsionComponent(componentIds[i]))
                    continue;
                if (ShipFamilyPartCalcProfileSet.IsCosmeticPartName(componentIds[i]))
                    continue;
                members.Add(i);
            }

            if (members.Count == 0)
                return -1;

            int local = PickPrimaryLocalIndex(
                PropulsionPoolKey, members, perComponentStats, storeExtraStartIndex);
            return members[local];
        }

        /// <summary>Count of non-cosmetic parts in a pool key.</summary>
        public static int CountPoolMembers(
            string poolKey,
            IReadOnlyList<string> componentIds)
        {
            if (componentIds == null || string.IsNullOrWhiteSpace(poolKey))
                return 0;

            int n = 0;
            for (int i = 0; i < componentIds.Count; i++)
            {
                string id = componentIds[i];
                if (string.IsNullOrWhiteSpace(id))
                    continue;
                if (ShipFamilyPartCalcProfileSet.IsCosmeticPartName(id))
                    continue;
                if (string.Equals(ResolveStackPoolKey(id), poolKey, StringComparison.OrdinalIgnoreCase))
                    n++;
            }

            return n;
        }

        static float ScorePropulsionPrimary(in ShipComponentAbilityStats s)
        {
            // Lexicographic via large place values: move >> accel >> energy.
            return s.moveSpeed * 1_000_000f
                   + s.accelerationCap * 1_000f
                   + s.energyCap;
        }

        static float ScoreGenericPrimary(in ShipComponentAbilityStats s)
        {
            return Mathf.Abs(s.healthCap)
                   + Mathf.Abs(s.energyCap)
                   + Mathf.Abs(s.moveSpeed)
                   + Mathf.Abs(s.firePower)
                   + Mathf.Abs(s.rammingPower)
                   + Mathf.Abs(s.turnSpeed)
                   + Mathf.Abs(s.maxGems)
                   + Mathf.Abs(s.tractorBeamDistance);
        }
    }
}
