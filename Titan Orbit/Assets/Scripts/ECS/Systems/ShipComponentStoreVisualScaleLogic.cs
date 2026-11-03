using System;
using System.Collections.Generic;
using TitanOrbit.Data;
using Unity.Entities;
using UnityEngine;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Orbit-menu GEAR purchases change combat stats and may swap the winning mesh
    /// (<see cref="ShipComponentVisualSwapLogic"/>). They do <b>not</b> resize the
    /// ship's existing parts. <see cref="Compute"/> and <see cref="ComputeForShip"/>
    /// always return <see cref="Identity"/> so a bought wing, engine, or cockpit stays
    /// at the hull's authored size (attribute upgrades still scale through
    /// <see cref="ShipComponentAttributeScaleLogic"/>).
    /// <para>
    /// This type still owns the equipment-buffer helpers those other systems need:
    /// catalog id collection, source-family lookup, and the dirty key for mesh-swap
    /// and hull rebakes.
    /// </para>
    /// </summary>
    public static class ShipComponentStoreVisualScaleLogic
    {
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
        /// Per-part scale from equipped GEAR. Always <see cref="Identity"/> — a purchase
        /// must not grow wings, weapons, engines, or any other hull part.
        /// </summary>
        /// <param name="em">Unused. Kept so hull sync and the hybrid applier keep one call site.</param>
        /// <param name="shipEntity">Unused. See <paramref name="em"/>.</param>
        public static StoreVisualScaleFactors ComputeForShip(EntityManager em, Entity shipEntity)
        {
            _ = em;
            _ = shipEntity;
            return Identity;
        }

        /// <summary>
        /// Per-part scale from a list of purchased component ids. Always <see cref="Identity"/>.
        /// </summary>
        /// <param name="chassisId">Unused. Signature kept for callers and tests.</param>
        /// <param name="shipLevel">Unused. See <paramref name="chassisId"/>.</param>
        /// <param name="extraComponentIds">Unused. See <paramref name="chassisId"/>.</param>
        /// <param name="extraFamilyConfigIndices">Unused. See <paramref name="chassisId"/>.</param>
        public static StoreVisualScaleFactors Compute(
            string chassisId,
            int shipLevel,
            IReadOnlyList<string> extraComponentIds,
            IReadOnlyList<int> extraFamilyConfigIndices = null)
        {
            _ = chassisId;
            _ = shipLevel;
            _ = extraComponentIds;
            _ = extraFamilyConfigIndices;
            return Identity;
        }

        /// <summary>
        /// Copies ship-component catalog ids from the equipment buffer into
        /// <paramref name="extraIds"/> (list created when null).
        /// Family slots are discarded — callers that apply fleet bonuses use
        /// <see cref="CollectExtraComponents"/>.
        /// </summary>
        public static void CollectExtraComponentIds(
            EntityManager em,
            Entity shipEntity,
            out List<string> extraIds)
        {
            CollectExtraComponents(em, shipEntity, out extraIds, out _);
        }

        /// <summary>
        /// Equipped moon-store ship components, in buffer order, plus the family
        /// slot stamped at purchase. Ids that have no catalog row are skipped so
        /// the two lists stay aligned with
        /// <see cref="ShipFamilyStatsCalculator.AppendExtraComponentsAndAggregate"/>.
        /// <paramref name="familyConfigIndices"/> is −1 never — 0 means home (1×).
        /// </summary>
        public static void CollectExtraComponents(
            EntityManager em,
            Entity shipEntity,
            out List<string> extraIds,
            out List<int> familyConfigIndices)
        {
            extraIds = new List<string>(4);
            familyConfigIndices = new List<int>(4);
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
                if (string.IsNullOrWhiteSpace(id))
                    continue;
                // Same gate as the stat append, so a missing catalog row cannot
                // shift the fleet index onto the next part.
                if (!ShipFamilyStatsCalculator.TryResolveComponentStats(null, id, out _))
                    continue;
                extraIds.Add(id);
                familyConfigIndices.Add(e.SourceFamilyConfigIndex);
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

        static float ClampGroup(float value) =>
            Mathf.Clamp(value, 1f, MaxGroupScale);
    }
}
