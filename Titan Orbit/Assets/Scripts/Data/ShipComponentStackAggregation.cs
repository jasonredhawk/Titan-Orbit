using System;
using System.Collections.Generic;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Multi-part stack aggregation: each pool keeps only the <b>primary</b> (highest-valued) part's
    /// stats. Extra copies contribute through the Extra Level formula's <c>(N−1)</c>
    /// term — not by adding discounted base stats.
    /// <para>
    /// [TITAN-ORBIT] Pools are by part type, except Engines + Thrusters share one
    /// <see cref="PropulsionPoolKey"/> pool. Paired with <see cref="ShipComponentExtraLevelMath"/>.
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

            /// <summary>Stats from the highest-valued member only.</summary>
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

            int primaryLocal = PickPrimaryLocalIndex(poolKey, memberIndices, perComponentStats);
            int primaryGlobal = memberIndices[primaryLocal];

            result.PrimaryStats = perComponentStats[primaryGlobal];
            result.ComponentCount = memberIndices.Count;
            return result;
        }

        /// <summary>
        /// [LEGACY name] Primary-only pool aggregate for older call sites.
        /// Extras do not add base stats — they raise Extra Level <c>(N−1)</c> later.
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
        /// Propulsion: highest moveSpeed (tie-break accel, energy). Other: highest additive score.
        /// </summary>
        public static int PickPrimaryLocalIndex(
            string poolKey,
            List<int> memberIndices,
            IReadOnlyList<ShipComponentAbilityStats> perComponentStats)
        {
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
        public static int PickPropulsionPrimaryGlobalIndex(
            IReadOnlyList<string> componentIds,
            IReadOnlyList<ShipComponentAbilityStats> perComponentStats)
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

            int local = PickPrimaryLocalIndex(PropulsionPoolKey, members, perComponentStats);
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
