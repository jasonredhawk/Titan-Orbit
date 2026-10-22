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
    /// Primary for Base: the single best piece of that type across the hull and
    /// every owned part, by fleet-adjusted score. A purchase becomes primary only
    /// when it actually out-scores the current best. A tie keeps the earlier part.
    /// Every other member of the pool is an extra — its PerExtra still adds, its
    /// Base does not. Weapons are outside this contest: each barrel keeps its Base.
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
                    pair.Key, pair.Value, componentIds, perComponentStats, storeExtraStartIndex);
                pools.Add(contrib);
                combinedPrimary.AddInPlace(contrib.PrimaryStats);
            }

            // Turn Base is one part: the best authored turn across hull and gear.
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
            IReadOnlyList<ShipComponentAbilityStats> perComponentStats,
            int storeExtraStartIndex = int.MaxValue)
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
                poolKey, memberIndices, perComponentStats, storeExtraStartIndex);
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
        /// Hull parts and moon-store parts compete together. Engines use move speed (then
        /// energy); thrusters use acceleration (then turn); other types use the additive
        /// base score. A tie keeps the earlier list index. A purchase wins only when its
        /// score is strictly higher. Weapons still keep every barrel's Base in
        /// <see cref="ShipComponentExtraLevelMath.AggregateAndEvaluate"/> — this pick does
        /// not take that Base away.
        /// </summary>
        /// <param name="storeExtraStartIndex">
        /// Kept so callers can still pass the hull/extra split. Ownership no longer
        /// depends on it.
        /// </param>
        public static int PickPrimaryLocalIndex(
            string poolKey,
            List<int> memberIndices,
            IReadOnlyList<ShipComponentAbilityStats> perComponentStats,
            int storeExtraStartIndex = int.MaxValue)
        {
            return PickPrimaryLocalIndex(
                poolKey, memberIndices, perComponentStats, storeExtraStartIndex, perComponentFleet: null);
        }

        /// <summary>
        /// Highest fleet-adjusted score among every member, hull and purchased.
        /// A 1× row uses its naked score. A tie keeps the earlier part.
        /// </summary>
        public static int PickPrimaryLocalIndex(
            string poolKey,
            List<int> memberIndices,
            IReadOnlyList<ShipComponentAbilityStats> perComponentStats,
            int storeExtraStartIndex,
            IReadOnlyList<ShipFamilySpecialBonuses> perComponentFleet)
        {
            // The split is no longer a gate. Callers still pass it.
            _ = storeExtraStartIndex;
            if (memberIndices == null || memberIndices.Count == 0)
                return 0;

            int bestLocal = 0;
            float bestScore = float.NegativeInfinity;
            bool found = false;
            for (int m = 0; m < memberIndices.Count; m++)
            {
                int gi = memberIndices[m];
                if (perComponentStats == null || gi < 0 || gi >= perComponentStats.Count)
                    continue;

                float score = ScoreWithFleet(poolKey, gi, perComponentStats, perComponentFleet);
                if (!found || score > bestScore)
                {
                    found = true;
                    bestScore = score;
                    bestLocal = m;
                }
            }

            return bestLocal;
        }

        /// <summary>Pool score after this row's fleet roll. A 1× roll leaves the catalog number.</summary>
        static float ScoreWithFleet(
            string poolKey,
            int globalIndex,
            IReadOnlyList<ShipComponentAbilityStats> perComponentStats,
            IReadOnlyList<ShipFamilySpecialBonuses> perComponentFleet)
        {
            ShipComponentAbilityStats stats = perComponentStats[globalIndex];
            if (perComponentFleet != null
                && globalIndex >= 0
                && globalIndex < perComponentFleet.Count
                && !perComponentFleet[globalIndex].IsIdentity)
            {
                stats = perComponentFleet[globalIndex].Apply(stats);
            }

            return ScoreForPool(poolKey, stats);
        }

        /// <summary>Same weights <see cref="PickPrimaryLocalIndex"/> uses for an unbuffed part.</summary>
        static float ScoreForPool(string poolKey, in ShipComponentAbilityStats stats)
        {
            if (IsEnginePoolKey(poolKey)
                || string.Equals(poolKey, PropulsionPoolKey, StringComparison.OrdinalIgnoreCase))
                return ScoreEnginePrimary(stats);
            if (IsThrusterPoolKey(poolKey))
                return ScoreThrusterPrimary(stats);
            return ScoreGenericPrimary(stats);
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
        /// Moon-store component ids that won their pool’s Base and should replace the mesh.
        /// A purchase is included only when its fleet-adjusted score beats the hull and
        /// every other owned part of that type. Weapons are omitted — a bought gun unlocks
        /// a bullet type and does not take a barrel’s Base.
        /// </summary>
        public static void CollectStoreExtrasThatOwnPoolBase(
            IReadOnlyList<string> componentIds,
            IReadOnlyList<ShipComponentAbilityStats> perComponentStats,
            int storeExtraStartIndex,
            List<string> into,
            IReadOnlyList<ShipFamilySpecialBonuses> perComponentFleet = null)
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
                    pair.Key, pair.Value, perComponentStats, storeExtraStartIndex, perComponentFleet);
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

        /// <summary>
        /// How a gear card should read against the ship the player already flies.
        /// <see cref="Unclassified"/> is a weapon (every barrel keeps Base) or a card
        /// with no readable loadout. <see cref="Primary"/> is the current best of its
        /// type, or an unowned part that would out-score that best at this moon.
        /// <see cref="Each"/> is everything else of that type.
        /// </summary>
        public enum GearCardRole
        {
            Unclassified = 0,
            Primary = 1,
            Each = 2,
        }

        /// <summary>
        /// Fleet-adjusted primary score for one part. Same weights as
        /// <see cref="PickPrimaryLocalIndex"/>.
        /// </summary>
        public static float ScorePart(
            string poolKey,
            in ShipComponentAbilityStats raw,
            ShipFamilySpecialBonuses fleet)
        {
            ShipComponentAbilityStats stats = fleet.IsIdentity ? raw : fleet.Apply(raw);
            return ScoreForPool(poolKey, stats);
        }

        /// <summary>
        /// Store-card role for <paramref name="componentId"/>.
        /// Owned rows use <paramref name="ownedFleets"/> (parallel to the ids).
        /// The card itself is scored with <paramref name="purchaseFleet"/>, the moon
        /// on screen. A tie with the current best stays with that best, so the card
        /// is <see cref="GearCardRole.Each"/> unless it is already the winning row.
        /// </summary>
        public static GearCardRole ClassifyStoreCard(
            string componentId,
            in ShipComponentAbilityStats rawStats,
            ShipFamilySpecialBonuses purchaseFleet,
            IReadOnlyList<string> ownedIds,
            IReadOnlyList<ShipComponentAbilityStats> ownedStats,
            IReadOnlyList<ShipFamilySpecialBonuses> ownedFleets)
        {
            if (string.IsNullOrWhiteSpace(componentId) || ownedIds == null || ownedStats == null)
                return GearCardRole.Unclassified;
            if (ShipFamilyPartCalcProfileSet.IsCosmeticPartName(componentId))
                return GearCardRole.Unclassified;

            string poolKey = ResolveStackPoolKey(componentId);
            if (IsWeaponPoolKey(poolKey))
                return GearCardRole.Unclassified;

            bool anyOwned = TryFindBestOwned(
                poolKey, ownedIds, ownedStats, ownedFleets, out int bestIndex, out float best);
            if (!anyOwned)
                bestIndex = -1;

            if (bestIndex >= 0
                && string.Equals(ownedIds[bestIndex], componentId, StringComparison.OrdinalIgnoreCase))
                return GearCardRole.Primary;

            float cardScore = ScorePart(poolKey, rawStats, purchaseFleet);
            if (bestIndex < 0 || cardScore > best)
                return GearCardRole.Primary;

            return GearCardRole.Each;
        }

        /// <summary>
        /// Best owned piece of this card’s type, including when that piece is the card itself.
        /// False when the pool is empty. Weapons stay outside the contest.
        /// </summary>
        public static bool TryFindOwnedPrimary(
            string componentId,
            IReadOnlyList<string> ownedIds,
            IReadOnlyList<ShipComponentAbilityStats> ownedStats,
            IReadOnlyList<ShipFamilySpecialBonuses> ownedFleets,
            out int ownedIndex)
        {
            ownedIndex = -1;
            if (string.IsNullOrWhiteSpace(componentId) || ownedIds == null || ownedStats == null)
                return false;
            if (ShipFamilyPartCalcProfileSet.IsCosmeticPartName(componentId))
                return false;

            string poolKey = ResolveStackPoolKey(componentId);
            if (IsWeaponPoolKey(poolKey))
                return false;

            return TryFindBestOwned(poolKey, ownedIds, ownedStats, ownedFleets, out ownedIndex, out _);
        }

        /// <summary>
        /// Owned row this card would replace. True only when the card’s fleet-adjusted
        /// score is strictly higher than a different piece that currently holds the pool.
        /// The card that already is that piece, a tie, and an empty pool return false.
        /// </summary>
        public static bool TryFindDisplacedPrimary(
            string componentId,
            in ShipComponentAbilityStats rawStats,
            ShipFamilySpecialBonuses purchaseFleet,
            IReadOnlyList<string> ownedIds,
            IReadOnlyList<ShipComponentAbilityStats> ownedStats,
            IReadOnlyList<ShipFamilySpecialBonuses> ownedFleets,
            out int ownedIndex)
        {
            ownedIndex = -1;
            if (string.IsNullOrWhiteSpace(componentId) || ownedIds == null || ownedStats == null)
                return false;
            if (ShipFamilyPartCalcProfileSet.IsCosmeticPartName(componentId))
                return false;

            string poolKey = ResolveStackPoolKey(componentId);
            if (IsWeaponPoolKey(poolKey))
                return false;

            if (!TryFindBestOwned(poolKey, ownedIds, ownedStats, ownedFleets, out int bestIndex, out float best))
                return false;
            if (string.Equals(ownedIds[bestIndex], componentId, StringComparison.OrdinalIgnoreCase))
                return false;

            float cardScore = ScorePart(poolKey, rawStats, purchaseFleet);
            if (cardScore <= best)
                return false;

            ownedIndex = bestIndex;
            return true;
        }

        /// <summary>
        /// Best owned member of <paramref name="poolKey"/>. A tie keeps the earlier row.
        /// </summary>
        static bool TryFindBestOwned(
            string poolKey,
            IReadOnlyList<string> ownedIds,
            IReadOnlyList<ShipComponentAbilityStats> ownedStats,
            IReadOnlyList<ShipFamilySpecialBonuses> ownedFleets,
            out int bestIndex,
            out float bestScore)
        {
            bestIndex = -1;
            bestScore = float.NegativeInfinity;
            int count = Mathf.Min(ownedIds.Count, ownedStats.Count);
            for (int i = 0; i < count; i++)
            {
                string id = ownedIds[i];
                if (string.IsNullOrWhiteSpace(id))
                    continue;
                if (ShipFamilyPartCalcProfileSet.IsCosmeticPartName(id))
                    continue;
                if (!string.Equals(ResolveStackPoolKey(id), poolKey, StringComparison.OrdinalIgnoreCase))
                    continue;

                ShipFamilySpecialBonuses fleet = ShipFamilySpecialBonuses.Identity;
                if (ownedFleets != null && i < ownedFleets.Count)
                    fleet = ownedFleets[i];

                float score = ScorePart(poolKey, ownedStats[i], fleet);
                if (bestIndex < 0 || score > bestScore)
                {
                    bestScore = score;
                    bestIndex = i;
                }
            }

            return bestIndex >= 0;
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
