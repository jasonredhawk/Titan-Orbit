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
    /// Primary for display and Base: the member with the highest base of that type
    /// (chassis parts and moon-store purchases compete together). A level-2 engine
    /// keeps the Move Base over a later level-1 buy. Every other member of the pool
    /// is an extra — its PerExtra still adds, its Base does not.
    /// Engines and thrusters are <b>separate</b> pools: engines own Move + Energy,
    /// thrusters own Accel + Turn. <see cref="PropulsionPoolKey"/> is leftover for
    /// older HUD binders that still group both.
    /// </para>
    /// </summary>
    public static class ShipComponentStackAggregation
    {
        /// <summary>[LEGACY] Old shared engine+thruster key. Live stacking uses Engine / Thruster.</summary>
        public const string PropulsionPoolKey = "Propulsion";

        /// <summary>Engine Extra Level pool — Move Speed + Energy Cap/Regen + OVERDRIVE.</summary>
        public const string EnginePoolKey = "Engine";

        /// <summary>Thruster Extra Level pool — Acceleration + Turn.</summary>
        public const string ThrusterPoolKey = "Thruster";

        /// <summary>
        /// One stack pool after primary selection — feeds Extra Level evaluation.
        /// </summary>
        public struct PoolContribution
        {
            /// <summary>Pool key (Weapon, Propulsion, Cockpit, …).</summary>
            public string PoolKey;

            /// <summary>Stats from the display primary (highest base in the pool).</summary>
            public ShipComponentAbilityStats PrimaryStats;

            /// <summary>Total members in the pool (including primary).</summary>
            public int ComponentCount;

            /// <summary>True when this pool is weapons (fire power divides by count).</summary>
            public bool IsWeaponPool;
        }

        /// <summary>True for the Engine Extra Level pool.</summary>
        public static bool IsEnginePoolKey(string poolKey) =>
            string.Equals(poolKey, EnginePoolKey, StringComparison.OrdinalIgnoreCase);

        /// <summary>True for the Thruster Extra Level pool.</summary>
        public static bool IsThrusterPoolKey(string poolKey) =>
            string.Equals(poolKey, ThrusterPoolKey, StringComparison.OrdinalIgnoreCase);

        /// <summary>True for Engine, Thruster, or the leftover shared Propulsion key.</summary>
        public static bool IsAnyPropulsionPoolKey(string poolKey) =>
            IsEnginePoolKey(poolKey)
            || IsThrusterPoolKey(poolKey)
            || string.Equals(poolKey, PropulsionPoolKey, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Pool key for stacking: Engine and Thruster stay separate so Move Base
        /// and Accel Base are not stolen from each other. Other parts use the
        /// canonical type (Wing, Cockpit, Weapon, …).
        /// </summary>
        public static string ResolveStackPoolKey(string componentId)
        {
            if (ShipFamilyPartTypes.IsEngineLikeName(componentId))
                return EnginePoolKey;
            if (ShipFamilyPartTypes.IsThrusterLikeName(componentId))
                return ThrusterPoolKey;
            if (ShipComponentAbilityStats.IsPropulsionComponent(componentId))
                return EnginePoolKey;

            string type = ShipComponentAbilityStats.ResolvePartTypeForSuggestedStats(componentId);
            if (string.IsNullOrWhiteSpace(type))
                return "Other";

            // [TITAN-ORBIT] Every family weapon shares one Extra Level pool.
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
            IReadOnlyList<ShipComponentAbilityStats> perComponentStats,
            int storeExtraStartIndex = int.MaxValue)
        {
            AggregatePrimaries(
                componentIds, perComponentStats, out ShipComponentAbilityStats combined, out _,
                storeExtraStartIndex);
            return combined;
        }

        /// <summary>
        /// Primary-per-pool aggregation plus the list of pool contributions (count + primary stats).
        /// </summary>
        public static void AggregatePrimaries(
            IReadOnlyList<string> componentIds,
            IReadOnlyList<ShipComponentAbilityStats> perComponentStats,
            out ShipComponentAbilityStats combinedPrimary,
            out List<PoolContribution> pools,
            int storeExtraStartIndex = int.MaxValue)
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

            // Turn Base is one part (highest turnSpeed), not one Base per pool.
            // Every other part still adds its turn PerExtra.
            ApplySingleTurnBase(
                componentIds, perComponentStats, ref combinedPrimary, storeExtraStartIndex);
        }

        /// <summary>
        /// Replaces a summed turn Base with the single best part’s Base
        /// (chassis or purchased). Every non-cosmetic part still contributes its turn PerExtra.
        /// </summary>
        static void ApplySingleTurnBase(
            IReadOnlyList<string> componentIds,
            IReadOnlyList<ShipComponentAbilityStats> perComponentStats,
            ref ShipComponentAbilityStats combinedPrimary,
            int storeExtraStartIndex = int.MaxValue)
        {
            int best = ShipComponentExtraLevelMath.PickBestTurnBaseIndex(
                componentIds, perComponentStats, storeExtraStartIndex);
            float perSum = 0f;
            int count = Mathf.Min(componentIds.Count, perComponentStats.Count);
            for (int i = 0; i < count; i++)
            {
                string id = componentIds[i];
                if (string.IsNullOrWhiteSpace(id))
                    continue;
                if (ShipFamilyPartCalcProfileSet.IsCosmeticPartName(id))
                    continue;
                perSum += Mathf.Max(0f, perComponentStats[i].turnSpeedPerExtraLevel);
            }

            combinedPrimary.turnSpeed = best >= 0 ? perComponentStats[best].turnSpeed : 0f;
            combinedPrimary.turnSpeedPerExtraLevel = perSum;
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

            // [TITAN-ORBIT] Engine primary must not leak Accel into the hull snapshot
            // when thrusters exist (and vice versa for Move).
            ShipPropulsionAggregation.ClassifyPropulsionRoles(
                componentIds, out bool hasEngines, out bool hasThrusters);
            string primaryId = componentIds != null && primaryGlobal < componentIds.Count
                ? componentIds[primaryGlobal]
                : string.Empty;
            result.PrimaryStats = ShipPropulsionAggregation.MaskAbilityStatsForRole(
                primaryId, perComponentStats[primaryGlobal], hasEngines, hasThrusters);
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
        /// Highest base in the pool wins, whether that part was on the chassis or bought
        /// later. Engines use move speed (then energy); thrusters use acceleration (then turn);
        /// other types use the additive base score. A tie keeps the earlier list index, so a
        /// chassis part stays primary when a purchase has the same base.
        /// </summary>
        /// <param name="storeExtraStartIndex">
        /// Kept so callers can still pass the moon-store split. Purchase order does not
        /// choose the primary — only base score does.
        /// </param>
        public static int PickPrimaryLocalIndex(
            string poolKey,
            List<int> memberIndices,
            IReadOnlyList<ShipComponentAbilityStats> perComponentStats,
            int storeExtraStartIndex = int.MaxValue)
        {
            _ = storeExtraStartIndex;
            if (memberIndices == null || memberIndices.Count == 0)
                return 0;

            int bestLocal = 0;
            float bestScore = float.NegativeInfinity;
            bool enginePool = IsEnginePoolKey(poolKey);
            bool thrusterPool = IsThrusterPoolKey(poolKey);
            bool legacyPropulsion = string.Equals(poolKey, PropulsionPoolKey, StringComparison.OrdinalIgnoreCase);

            for (int m = 0; m < memberIndices.Count; m++)
            {
                ShipComponentAbilityStats s = perComponentStats[memberIndices[m]];
                float score;
                if (enginePool || legacyPropulsion)
                    score = ScoreEnginePrimary(s);
                else if (thrusterPool)
                    score = ScoreThrusterPrimary(s);
                else
                    score = ScoreGenericPrimary(s);
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

        /// <summary>
        /// Moon-store component ids that own their pool’s Base. Chassis parts and purchases
        /// are scored together; only a buy with a strictly higher base than every other
        /// member is returned. Weaker buys stay extras and must not replace the mesh.
        /// Weapons are omitted — a bought gun unlocks a bullet type and does not take a barrel’s Base.
        /// </summary>
        public static void CollectStoreExtrasThatOwnPoolBase(
            IReadOnlyList<string> componentIds,
            IReadOnlyList<ShipComponentAbilityStats> perComponentStats,
            int storeExtraStartIndex,
            List<string> into)
        {
            if (into == null)
                return;
            into.Clear();
            if (componentIds == null || perComponentStats == null)
                return;
            if (storeExtraStartIndex < 0 || storeExtraStartIndex >= componentIds.Count)
                return;

            int count = Mathf.Min(componentIds.Count, perComponentStats.Count);
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
                if (IsWeaponPoolKey(pair.Key))
                    continue;

                int primaryLocal = PickPrimaryLocalIndex(
                    pair.Key, pair.Value, perComponentStats, storeExtraStartIndex);
                int primaryGlobal = pair.Value[primaryLocal];
                if (primaryGlobal < storeExtraStartIndex)
                    continue;

                string winner = componentIds[primaryGlobal];
                if (string.IsNullOrWhiteSpace(winner))
                    continue;
                if (!ContainsId(into, winner))
                    into.Add(winner);
            }
        }

        static bool ContainsId(List<string> ids, string id)
        {
            for (int i = 0; i < ids.Count; i++)
            {
                if (string.Equals(ids[i], id, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>Engine primary: cruise base first, then energy plant size.</summary>
        static float ScoreEnginePrimary(in ShipComponentAbilityStats s)
        {
            return s.moveSpeed * 1_000_000f + s.energyCap;
        }

        /// <summary>Thruster primary: thrust base first, then turn.</summary>
        static float ScoreThrusterPrimary(in ShipComponentAbilityStats s)
        {
            return s.accelerationCap * 1_000_000f + s.turnSpeed * 1_000f;
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
