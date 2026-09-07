using System;
using System.Collections.Generic;
using TitanOrbit.Data;
using Unity.Entities;
using UnityEngine;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Turns moon-dock store component purchases into per-part-type visual scale factors.
    /// When you buy a cockpit or engine (often from another ship family at a captured moon),
    /// this helper compares that part's evaluated stats to the chassis primary of the same
    /// type and grows your existing matching meshes — it does not spawn the purchased mesh
    /// and it does not rewrite combat numbers (those stay on Extra Level merge).
    /// <para>
    /// [TITAN-ORBIT] Scale amount is a <b>stat-increase ratio</b>, not prefab
    /// <c>localScale</c>. Weaker extras never shrink (grow-only). Several extras of one
    /// type take the <b>max</b> factor. MEGA hulls return identity (no family part groups).
    /// Paired with <see cref="ShipComponentAttributeScaleLogic"/> (meshes + collider bake)
    /// and <see cref="Game.ShipComponentAttributeScaleApplier"/> (hybrid proxy).
    /// </para>
    /// </summary>
    public static class ShipComponentStoreVisualScaleLogic
    {
        /// <summary>
        /// Ignore category magnitudes below this so a zero Health cockpit cannot divide
        /// into a huge ratio.
        /// </summary>
        const float MagnitudeEpsilon = 0.0001f;

        /// <summary>
        /// Treat ratios this close to 1 as "no grow" so float noise does not dirty collider bakes.
        /// </summary>
        const float GrowEpsilon = 0.001f;

        /// <summary>
        /// [TITAN-ORBIT] Hard ceiling so a wildly stronger foreign cockpit cannot explode
        /// the mesh (playtest clamp — not a combat stat cap).
        /// </summary>
        public const float MaxGroupScale = 3f;

        /// <summary>
        /// Per attribute-scale group multipliers. Default (all zeros) means "unset" —
        /// callers must treat that as <see cref="Identity"/> (see <see cref="Resolve"/>).
        /// </summary>
        public struct StoreVisualScaleFactors
        {
            public float Cockpit;
            public float Wing;
            public float Weapon;
            public float Engine;
            public float Thruster;
            public float Tail;
            public float Part;

            /// <summary>True when any group is meaningfully above 1.</summary>
            public bool HasAnyGrow =>
                Cockpit > 1f + GrowEpsilon
                || Wing > 1f + GrowEpsilon
                || Weapon > 1f + GrowEpsilon
                || Engine > 1f + GrowEpsilon
                || Thruster > 1f + GrowEpsilon
                || Tail > 1f + GrowEpsilon
                || Part > 1f + GrowEpsilon;
        }

        /// <summary>No store extras — every group stays at authored / attribute size.</summary>
        public static StoreVisualScaleFactors Identity => new StoreVisualScaleFactors
        {
            Cockpit = 1f,
            Wing = 1f,
            Weapon = 1f,
            Engine = 1f,
            Thruster = 1f,
            Tail = 1f,
            Part = 1f,
        };

        /// <summary>
        /// Turns a default (all-zero) struct into identity, and clamps each group to
        /// <c>[1, MaxGroupScale]</c> so Apply cannot zero a mesh.
        /// </summary>
        public static StoreVisualScaleFactors Resolve(StoreVisualScaleFactors factors)
        {
            // --- Unset struct (C# default) → identity ---
            bool unset = factors.Cockpit <= 0f
                && factors.Wing <= 0f
                && factors.Weapon <= 0f
                && factors.Engine <= 0f
                && factors.Thruster <= 0f
                && factors.Tail <= 0f
                && factors.Part <= 0f;
            if (unset)
                return Identity;

            factors.Cockpit = ClampGroup(factors.Cockpit);
            factors.Wing = ClampGroup(factors.Wing);
            factors.Weapon = ClampGroup(factors.Weapon);
            factors.Engine = ClampGroup(factors.Engine);
            factors.Thruster = ClampGroup(factors.Thruster);
            factors.Tail = ClampGroup(factors.Tail);
            factors.Part = ClampGroup(factors.Part);
            return factors;
        }

        /// <summary>
        /// Cheap dirty key from the ghosted equipment buffer (component ids + item levels).
        /// Used by hull sync and the hybrid applier so idle frames skip work.
        /// </summary>
        /// <param name="em">World that owns the ship ghost.</param>
        /// <param name="shipEntity">Ship with an optional <see cref="EquippedEquipmentElement"/> buffer.</param>
        /// <returns>0 when there is no buffer or no ship-component rows.</returns>
        public static int ComputeEquipmentScaleKey(EntityManager em, Entity shipEntity)
        {
            if (shipEntity == Entity.Null || !em.Exists(shipEntity))
                return 0;
            if (!em.HasBuffer<EquippedEquipmentElement>(shipEntity))
                return 0;

            return ComputeEquipmentScaleKey(em.GetBuffer<EquippedEquipmentElement>(shipEntity));
        }

        /// <summary>
        /// Hashes ship-component rows so a purchase or discard changes the key.
        /// Support items (drones / rockets / mines) are ignored.
        /// </summary>
        public static int ComputeEquipmentScaleKey(DynamicBuffer<EquippedEquipmentElement> buffer)
        {
            // --- Hash equipped catalog ids ---
            unchecked
            {
                int hash = 17;
                int counted = 0;
                for (int i = 0; i < buffer.Length; i++)
                {
                    EquippedEquipmentElement e = buffer[i];
                    if ((StoreItemType)e.ItemType != StoreItemType.ShipComponent)
                        continue;

                    string id = e.ComponentId.ToString();
                    if (string.IsNullOrWhiteSpace(id))
                        continue;

                    hash = hash * 31 + id.GetHashCode();
                    hash = hash * 31 + e.ItemLevel;
                    counted++;
                }

                return counted == 0 ? 0 : hash;
            }
        }

        /// <summary>
        /// Reads the ship's equipment buffer and chassis, then returns per-group grow factors.
        /// MEGA hulls and missing chassis return <see cref="Identity"/>.
        /// </summary>
        /// <param name="em">Server or client EntityManager (ghost buffer is replicated).</param>
        /// <param name="shipEntity">Ship ghost.</param>
        public static StoreVisualScaleFactors ComputeForShip(EntityManager em, Entity shipEntity)
        {
            if (shipEntity == Entity.Null || !em.Exists(shipEntity))
                return Identity;

            // [TITAN-ORBIT] MEGA parts are unique catalog modules, not family Cockpit/Engine groups.
            if (em.HasComponent<MegaShipState>(shipEntity)
                && em.GetComponentData<MegaShipState>(shipEntity).IsMega)
                return Identity;

            if (!em.HasComponent<ShipState>(shipEntity))
                return Identity;

            var ship = em.GetComponentData<ShipState>(shipEntity);
            if (!ShipStatApplyLogic.TryResolveChassisId(
                    em,
                    shipEntity,
                    ship.Team,
                    ship.ShipLevel,
                    ship.BranchIndex,
                    out string chassisId,
                    allowFallback: true)
                || string.IsNullOrEmpty(chassisId))
                return Identity;

            CollectExtraComponentIds(em, shipEntity, out List<string> extraIds);
            if (extraIds == null || extraIds.Count == 0)
                return Identity;

            return Compute(chassisId, ship.ShipLevel, extraIds);
        }

        /// <summary>
        /// Core formula: for each extra component id, grow the matching part-type group by
        /// the average shared-category stat ratio versus the chassis primary of that type.
        /// </summary>
        /// <param name="chassisId">Current hull id (e.g. AstroEagle_01) — baseline comes from this prefab.</param>
        /// <param name="shipLevel">Live ship level used for Extra Level evaluation on both sides.</param>
        /// <param name="extraComponentIds">Moon-store equipped catalog ids (may be another family).</param>
        public static StoreVisualScaleFactors Compute(
            string chassisId,
            int shipLevel,
            IReadOnlyList<string> extraComponentIds)
        {
            var factors = Identity;
            if (string.IsNullOrWhiteSpace(chassisId)
                || extraComponentIds == null
                || extraComponentIds.Count == 0)
                return factors;

            if (!TryGetChassisRawParts(chassisId, out ShipFamilyStatsCalculator.SumResult chassisParts)
                || chassisParts.MatchedComponentIds == null
                || chassisParts.MatchedComponentIds.Count == 0)
                return factors;

            int level = Mathf.Max(1, shipLevel);
            for (int i = 0; i < extraComponentIds.Count; i++)
            {
                string extraId = extraComponentIds[i];
                if (string.IsNullOrWhiteSpace(extraId))
                    continue;

                // --- Source family catalog (not the ship's suffix remap) ---
                if (!TryFindSourceComponent(extraId, out ShipFamilyComponentEntry entry) || entry == null)
                    continue;

                string partType = NormalizePartType(entry.componentId);
                if (string.IsNullOrEmpty(partType)
                    || string.Equals(partType, ShipFamilyPartTypes.Ignore, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(partType, ShipFamilyPartTypes.Unmapped, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!TryGetChassisPrimaryForPartType(
                        chassisParts, partType, out string baselineId, out ShipComponentAbilityStats baselineRaw))
                    continue;

                ShipComponentAbilityStats purchased = ShipComponentStoreData.GetEffectiveStatsAtShipLevel(
                    entry.stats, level, entry.componentId);
                ShipComponentAbilityStats baseline = ShipComponentStoreData.GetEffectiveStatsAtShipLevel(
                    baselineRaw, level, baselineId);

                float ratio = ComputeSharedCategoryRatio(entry, purchased, baselineId, baseline);
                if (ratio <= 1f + GrowEpsilon)
                    continue;

                ApplyRatioToGroup(ref factors, partType, ratio);
            }

            return Resolve(factors);
        }

        /// <summary>
        /// Copies ship-component catalog ids from the equipment buffer into
        /// <paramref name="extraIds"/> (list created when null).
        /// </summary>
        public static void CollectExtraComponentIds(
            EntityManager em,
            Entity shipEntity,
            out List<string> extraIds)
        {
            extraIds = new List<string>(4);
            if (shipEntity == Entity.Null || !em.Exists(shipEntity))
                return;
            if (!em.HasBuffer<EquippedEquipmentElement>(shipEntity))
                return;

            var buffer = em.GetBuffer<EquippedEquipmentElement>(shipEntity);
            for (int i = 0; i < buffer.Length; i++)
            {
                EquippedEquipmentElement e = buffer[i];
                if ((StoreItemType)e.ItemType != StoreItemType.ShipComponent)
                    continue;
                string id = e.ComponentId.ToString();
                if (!string.IsNullOrWhiteSpace(id))
                    extraIds.Add(id);
            }
        }

        /// <summary>
        /// Prefers the family whose <c>familyId</c> prefixes the component id so
        /// <c>CosmicShark_Cockpit_1</c> does not resolve to AstroEagle via suffix fallback.
        /// </summary>
        public static bool TryFindSourceComponent(string componentId, out ShipFamilyComponentEntry entry)
        {
            entry = null;
            if (string.IsNullOrWhiteSpace(componentId))
                return false;

            var config = PlanetShipFamilyConfig.LoadDefault();
            if (config?.families != null)
            {
                string id = componentId.Trim();
                for (int i = 0; i < config.families.Count; i++)
                {
                    ShipFamilyDefinition family = config.families[i]?.shipFamilyDefinition;
                    if (family == null || string.IsNullOrWhiteSpace(family.familyId))
                        continue;

                    // --- Exact family prefix first (cross-family store buys) ---
                    if (!id.StartsWith(family.familyId.Trim() + "_", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (family.TryGetComponentEntry(id, out entry) && entry != null)
                        return true;
                }
            }

            // Same-family short ids (Cockpit_1) still resolve through the planet-config walk.
            return BulletBankProfileUtility.TryFindComponentInAnyFamily(componentId, out entry)
                && entry != null;
        }

        /// <summary>
        /// Average of purchased/baseline magnitudes for categories both parts author.
        /// Returns 1 when there is no shared category or no positive baseline.
        /// </summary>
        static float ComputeSharedCategoryRatio(
            ShipFamilyComponentEntry purchasedEntry,
            in ShipComponentAbilityStats purchasedStats,
            string baselineComponentId,
            in ShipComponentAbilityStats baselineStats)
        {
            purchasedEntry.EnsureStatCategories();
            IReadOnlyList<ShipComponentStatCategory> purchasedCats = purchasedEntry.statCategories;
            if (purchasedCats == null || purchasedCats.Count == 0)
                purchasedCats = ShipFamilyComponentPartKey.InferDefaultStatCategories(purchasedEntry.componentId);

            List<ShipComponentStatCategory> baselineCats =
                ShipFamilyComponentPartKey.InferDefaultStatCategories(baselineComponentId);

            float sum = 0f;
            int n = 0;
            for (int i = 0; i < purchasedCats.Count; i++)
            {
                ShipComponentStatCategory category = purchasedCats[i];
                if (!ContainsCategory(baselineCats, category))
                    continue;

                float bought = SumCategoryMagnitude(purchasedStats, category, purchasedEntry.componentId);
                float mine = SumCategoryMagnitude(baselineStats, category, baselineComponentId);
                if (mine <= MagnitudeEpsilon)
                    continue;

                sum += bought / mine;
                n++;
            }

            if (n <= 0)
                return 1f;
            return sum / n;
        }

        /// <summary>
        /// Sums the authored fields for one stat category after Extra Level evaluation
        /// (base fields only — PerExtra is already baked in).
        /// </summary>
        static float SumCategoryMagnitude(
            in ShipComponentAbilityStats stats,
            ShipComponentStatCategory category,
            string componentId)
        {
            ShipComponentAbilityStats kept = ShipComponentAbilityStats.KeepOnlyAuthoringFields(
                stats, category, componentId);
            switch (category)
            {
                case ShipComponentStatCategory.Offense:
                    return kept.firePower + kept.bulletSpeed + kept.bulletRange
                        + kept.fireRate + kept.rammingPower;
                case ShipComponentStatCategory.Health:
                    return kept.healthCap + kept.healthRegen;
                case ShipComponentStatCategory.Energy:
                    return kept.energyCap + kept.energyRegen;
                case ShipComponentStatCategory.Movement:
                    return kept.moveSpeed + kept.accelerationCap + kept.turnSpeed;
                case ShipComponentStatCategory.Capacity:
                    return kept.maxGems + kept.maxPeople
                        + kept.tractorBeamDistance + kept.tractorBeamPower;
                default:
                    return 0f;
            }
        }

        /// <summary>
        /// Highest-valued chassis part of the same visual scale group as <paramref name="partType"/>.
        /// Engines do not pick a thruster (and vice versa) even though they share a combat pool.
        /// Weapons of any subtype share one group.
        /// </summary>
        static bool TryGetChassisPrimaryForPartType(
            in ShipFamilyStatsCalculator.SumResult chassisParts,
            string partType,
            out string baselineId,
            out ShipComponentAbilityStats baselineStats)
        {
            baselineId = null;
            baselineStats = default;
            if (chassisParts.MatchedComponentIds == null || chassisParts.PerComponentStats == null)
                return false;

            int count = Mathf.Min(
                chassisParts.MatchedComponentIds.Count,
                chassisParts.PerComponentStats.Count);
            var members = new List<int>(4);
            for (int i = 0; i < count; i++)
            {
                string id = chassisParts.MatchedComponentIds[i];
                if (string.IsNullOrWhiteSpace(id))
                    continue;
                if (ShipFamilyPartCalcProfileSet.IsCosmeticPartName(id))
                    continue;
                if (!IsSameScaleGroup(partType, NormalizePartType(id)))
                    continue;
                members.Add(i);
            }

            if (members.Count == 0)
                return false;

            // Generic score (not the shared Propulsion pool) so Engine ≠ Thruster.
            int local = ShipComponentStackAggregation.PickPrimaryLocalIndex(
                partType, members, chassisParts.PerComponentStats);
            int global = members[local];
            baselineId = chassisParts.MatchedComponentIds[global];
            baselineStats = chassisParts.PerComponentStats[global];
            return !string.IsNullOrWhiteSpace(baselineId);
        }

        /// <summary>
        /// Chassis prefab parts at authored scale, no store extras and no Extra Level yet.
        /// </summary>
        static bool TryGetChassisRawParts(string chassisId, out ShipFamilyStatsCalculator.SumResult parts)
        {
            parts = default;
            var config = PlanetShipFamilyConfig.LoadDefault();
            if (config == null || string.IsNullOrEmpty(chassisId))
                return false;

            var tier = config.GetTierEntryForChassisId(chassisId);
            if (tier?.prefab == null)
                return false;
            if (!ShipStatApplyLogic.TryResolveFamilyForChassisId(chassisId, out ShipFamilyDefinition family)
                || family == null)
                return false;

            parts = ShipFamilyStatsCalculator.SumFromPrefabHierarchy(
                tier.prefab, family, shipLevel: 1, applyPropulsionAndWeaponRules: false);
            return parts.MatchedComponentIds != null && parts.MatchedComponentIds.Count > 0;
        }

        /// <summary>Writes the ratio onto the attribute-scale group for this part type (max-stack).</summary>
        static void ApplyRatioToGroup(ref StoreVisualScaleFactors factors, string partType, float ratio)
        {
            float clamped = ClampGroup(ratio);
            if (string.Equals(partType, ShipFamilyPartTypes.Cockpit, StringComparison.OrdinalIgnoreCase))
                factors.Cockpit = Mathf.Max(factors.Cockpit, clamped);
            else if (string.Equals(partType, ShipFamilyPartTypes.Wing, StringComparison.OrdinalIgnoreCase))
                factors.Wing = Mathf.Max(factors.Wing, clamped);
            else if (ShipFamilyPartTypes.IsWeapon(partType))
                factors.Weapon = Mathf.Max(factors.Weapon, clamped);
            else if (ShipFamilyPartTypes.IsEngineProfile(partType))
                factors.Engine = Mathf.Max(factors.Engine, clamped);
            else if (ShipFamilyPartTypes.IsThrusterProfile(partType))
                factors.Thruster = Mathf.Max(factors.Thruster, clamped);
            else if (ShipFamilyPartTypes.IsTurn(partType))
                factors.Tail = Mathf.Max(factors.Tail, clamped);
            else
                factors.Part = Mathf.Max(factors.Part, clamped);
        }

        /// <summary>Canonical Part Profile id from a component / prefab name.</summary>
        static string NormalizePartType(string componentId)
        {
            string raw = ShipComponentAbilityStats.ResolvePartTypeForSuggestedStats(componentId);
            return ShipFamilyPartTypes.Normalize(raw, componentId);
        }

        /// <summary>
        /// True when two part types share one visual scale group.
        /// Weapon subtypes share Weapons; Engine and Thruster stay separate.
        /// </summary>
        static bool IsSameScaleGroup(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
                return false;
            if (ShipFamilyPartTypes.IsWeapon(a) && ShipFamilyPartTypes.IsWeapon(b))
                return true;
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        static bool ContainsCategory(
            IReadOnlyList<ShipComponentStatCategory> list,
            ShipComponentStatCategory category)
        {
            if (list == null)
                return false;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] == category)
                    return true;
            }

            return false;
        }

        static float ClampGroup(float value) =>
            Mathf.Clamp(value, 1f, MaxGroupScale);
    }
}
