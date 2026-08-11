using System;
using System.Collections.Generic;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Multi-part stack aggregation: primary contributes 100% of its stats; each extra contributes
    /// <see cref="ShipComponentAbilityStats.extraStackWeight"/> of <b>its own</b> stats.
    /// <para>
    /// [TITAN-ORBIT] Pools are by part type, except Engines + Thrusters share one
    /// <see cref="PropulsionPoolKey"/> pool (same grouping as Move/Accel).
    /// Default weight is 1 (full sum); propulsion defaults to 0.1 when unset.
    /// </para>
    /// </summary>
    public static class ShipComponentStackAggregation
    {
        /// <summary>Shared pool key for engines and thrusters.</summary>
        public const string PropulsionPoolKey = "Propulsion";

        /// <summary>Default extra weight when a part did not author <c>extraStackWeight</c>.</summary>
        public const float DefaultExtraStackWeight = 1f;

        /// <summary>Default extra weight for engines/thrusters when unset (matches legacy 10% extras).</summary>
        public const float DefaultPropulsionExtraStackWeight = 0.1f;

        /// <summary>
        /// Default extra weight for weapon barrels when unset.
        /// [TITAN-ORBIT] Prefabs often nest several same-named Weapon/Missile meshes; full-sum
        /// extras created overgunned outliers. Primary barrel keeps 100%; each extra adds 25%.
        /// </summary>
        public const float DefaultWeaponExtraStackWeight = 0.25f;

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
            return type.Trim();
        }

        /// <summary>
        /// Resolves the weight used when this part is an <b>extra</b> in its pool.
        /// Authored <c>&gt; 0</c> wins; else propulsion → 0.1, weapons → 0.25, everything else → 1.
        /// </summary>
        public static float ResolveExtraStackWeight(in ShipComponentAbilityStats stats, string componentId)
        {
            if (stats.extraStackWeight > 0.0001f)
                return stats.extraStackWeight;

            if (ShipComponentAbilityStats.IsPropulsionComponent(componentId))
                return DefaultPropulsionExtraStackWeight;

            if (ShipComponentAbilityStats.IsWeaponComponent(componentId))
                return DefaultWeaponExtraStackWeight;

            return DefaultExtraStackWeight;
        }

        /// <summary>
        /// Suggested seed for Scan / ProfileSet: 0.1 engines/thrusters, 0.25 weapons, else 1.
        /// </summary>
        public static float GetSuggestedExtraStackWeight(string componentId)
        {
            if (ShipComponentAbilityStats.IsPropulsionComponent(componentId))
                return DefaultPropulsionExtraStackWeight;
            if (ShipComponentAbilityStats.IsWeaponComponent(componentId))
                return DefaultWeaponExtraStackWeight;
            return DefaultExtraStackWeight;
        }

        /// <summary>
        /// Same seed as <see cref="GetSuggestedExtraStackWeight"/> when you only have a part type
        /// (ProfileSet defaults), not a component id.
        /// </summary>
        public static float GetSuggestedExtraStackWeightForPartType(string partType)
        {
            if (ShipFamilyPartTypes.IsPropulsion(partType))
                return DefaultPropulsionExtraStackWeight;
            if (ShipFamilyPartTypes.IsWeapon(partType))
                return DefaultWeaponExtraStackWeight;
            return DefaultExtraStackWeight;
        }

        /// <summary>
        /// Rebuilds hull totals from per-part lists using weighted stack pools (formula B).
        /// Call before weapon max / family fallback passes.
        /// </summary>
        public static ShipComponentAbilityStats AggregateAllPools(
            IReadOnlyList<string> componentIds,
            IReadOnlyList<ShipComponentAbilityStats> perComponentStats)
        {
            var total = default(ShipComponentAbilityStats);
            if (componentIds == null || perComponentStats == null)
                return total;

            int count = Mathf.Min(componentIds.Count, perComponentStats.Count);
            if (count <= 0)
                return total;

            // --- Group indices by pool key ---
            var pools = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < count; i++)
            {
                string id = componentIds[i];
                if (string.IsNullOrWhiteSpace(id))
                    continue;
                if (ShipFamilyPartCalcProfileSet.IsCosmeticPartName(id))
                    continue;

                string key = ResolveStackPoolKey(id);
                if (!pools.TryGetValue(key, out List<int> list))
                {
                    list = new List<int>(4);
                    pools[key] = list;
                }

                list.Add(i);
            }

            foreach (KeyValuePair<string, List<int>> pair in pools)
            {
                ShipComponentAbilityStats poolStats = AggregatePoolWeighted(
                    pair.Key, pair.Value, componentIds, perComponentStats);
                total.AddInPlace(poolStats);
            }

            return total;
        }

        /// <summary>
        /// Weighted aggregate for one pool: primary ×1 + extras × ResolveExtraStackWeight.
        /// <see cref="ShipComponentAbilityStats.extraSpeedPercent"/> uses max (unweighted).
        /// </summary>
        public static ShipComponentAbilityStats AggregatePoolWeighted(
            string poolKey,
            List<int> memberIndices,
            IReadOnlyList<string> componentIds,
            IReadOnlyList<ShipComponentAbilityStats> perComponentStats)
        {
            var result = default(ShipComponentAbilityStats);
            if (memberIndices == null || memberIndices.Count == 0)
                return result;

            int primaryLocal = PickPrimaryLocalIndex(poolKey, memberIndices, perComponentStats);
            int primaryGlobal = memberIndices[primaryLocal];

            float maxOdPct = 0f;
            float maxOdPctPer = 0f;

            for (int m = 0; m < memberIndices.Count; m++)
            {
                int gi = memberIndices[m];
                string id = componentIds[gi];
                ShipComponentAbilityStats stats = perComponentStats[gi];
                bool isPrimary = gi == primaryGlobal;
                float weight = isPrimary ? 1f : ResolveExtraStackWeight(in stats, id);

                // Additive contribution (own stats × weight). Clear OD fractions — maxed below.
                ShipComponentAbilityStats contrib = ShipComponentAbilityStatsMath.Multiply(stats, weight);
                contrib.extraSpeedPercent = 0f;
                contrib.extraSpeedPercentPerAbilityLevel = 0f;
                result.AddInPlace(contrib);

                // OD fraction: max across members (designer knob — do not dilute with weight).
                if (stats.extraSpeedPercent > maxOdPct)
                    maxOdPct = stats.extraSpeedPercent;
                if (stats.extraSpeedPercentPerAbilityLevel > maxOdPctPer)
                    maxOdPctPer = stats.extraSpeedPercentPerAbilityLevel;
            }

            result.extraSpeedPercent = maxOdPct;
            result.extraSpeedPercentPerAbilityLevel = maxOdPctPer;
            result.extraStackWeight = 0f;
            return result;
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
