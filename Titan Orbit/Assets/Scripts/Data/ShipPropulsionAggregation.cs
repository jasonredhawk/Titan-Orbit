using System.Collections.Generic;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Thruster move speed and acceleration rules shared by <see cref="Entities.Starship"/> and editor previews.
    /// Engines no longer contribute propulsion (energy only). Thrusters: one base move speed (best part) plus
    /// each additional thruster's moveSpeedPerLevel; acceleration caps sum across all thrusters.
    /// </summary>
    public static class ShipPropulsionAggregation
    {
        /// <summary>Per-level terms are ~25% of base (20–30% band). Used when balancing engine energy after scan.</summary>
        public const float PerLevelFractionOfBase = 0.25f;

        /// <summary>Per level after 1, mobility loses this fraction of the base stat (matches Starship).</summary>
        public const float ShipLevelMobilityPenaltyFractionPerLevel = 0.11f;

        public struct Result
        {
            public float topMoveSpeed;
            public float sumAcceleration;
        }

        public static float ApplyShipLevelMobilityScale(float baseStat, int levelsAfterFirst)
        {
            if (levelsAfterFirst <= 0 || baseStat <= 0f)
                return baseStat;
            return baseStat - (baseStat * ShipLevelMobilityPenaltyFractionPerLevel) * levelsAfterFirst;
        }

        /// <summary>
        /// Computes thruster top speed and total acceleration from per-component stats at a ship level.
        /// </summary>
        public static Result ComputeThrusterPropulsion(
            IReadOnlyList<string> componentIds,
            IReadOnlyList<ShipComponentAbilityStats> perComponentStats,
            int shipLevel)
        {
            var result = new Result();
            if (componentIds == null || perComponentStats == null)
                return result;

            int count = Mathf.Min(componentIds.Count, perComponentStats.Count);
            if (count == 0)
                return result;

            int levelsAfterFirst = Mathf.Max(0, shipLevel - 1);
            int primaryIndex = -1;
            float bestPrimaryMove = 0f;

            for (int i = 0; i < count; i++)
            {
                if (!ShipComponentAbilityStats.IsThrusterComponent(componentIds[i]))
                    continue;

                ShipComponentAbilityStats comp = perComponentStats[i];
                if (comp.moveSpeed > bestPrimaryMove)
                {
                    bestPrimaryMove = comp.moveSpeed;
                    primaryIndex = i;
                }
            }

            if (primaryIndex >= 0)
            {
                float primaryMove = ApplyShipLevelMobilityScale(
                    perComponentStats[primaryIndex].moveSpeed,
                    levelsAfterFirst);
                float extraMove = 0f;

                for (int i = 0; i < count; i++)
                {
                    if (!ShipComponentAbilityStats.IsThrusterComponent(componentIds[i]))
                        continue;
                    if (i == primaryIndex)
                        continue;
                    extraMove += Mathf.Max(0f, perComponentStats[i].moveSpeedPerLevel);
                }

                result.topMoveSpeed = Mathf.Max(0.1f, primaryMove + extraMove);
            }

            for (int i = 0; i < count; i++)
            {
                if (!ShipComponentAbilityStats.IsThrusterComponent(componentIds[i]))
                    continue;

                ShipComponentAbilityStats comp = perComponentStats[i];
                result.sumAcceleration += Mathf.Max(0f, comp.accelerationCap + comp.accelerationCapPerLevel * levelsAfterFirst);
            }

            return result;
        }

        /// <summary>
        /// Sustained energy drain per second when firing (fireRate × damagePerBullet; damage equals fire power at runtime).
        /// </summary>
        public static float ComputeWeaponSustainedEnergyDrain(ShipComponentAbilityStats weaponStats, int firePowerUpgrades = 0)
        {
            float firePower = weaponStats.firePower + weaponStats.firePowerPerLevel * Mathf.Max(0, firePowerUpgrades);
            float fireRate = Mathf.Max(0.01f, weaponStats.fireRate + weaponStats.fireRatePerLevel * Mathf.Max(0, firePowerUpgrades));
            return firePower * fireRate;
        }

        /// <summary>
        /// Sets engine energy so total regen is slightly below summed weapon sustained drain.
        /// </summary>
        public static void BalanceEngineEnergyForComponents(
            IList<ShipFamilyComponentEntry> components,
            float regenToDrainRatio = 0.85f,
            float capacitySecondsAtFullDrain = 4f)
        {
            if (components == null || components.Count == 0)
                return;

            float totalWeaponDrain = 0f;
            var engineEntries = new List<ShipFamilyComponentEntry>();

            for (int i = 0; i < components.Count; i++)
            {
                ShipFamilyComponentEntry entry = components[i];
                if (entry == null || string.IsNullOrWhiteSpace(entry.componentId))
                    continue;

                string partType = ShipComponentAbilityStats.ResolvePartTypeForSuggestedStats(entry.componentId);
                if (string.Equals(partType, "Weapon", System.StringComparison.OrdinalIgnoreCase))
                    totalWeaponDrain += ComputeWeaponSustainedEnergyDrain(entry.stats);

                if (string.Equals(partType, "Engine", System.StringComparison.OrdinalIgnoreCase))
                    engineEntries.Add(entry);
            }

            if (engineEntries.Count == 0 || totalWeaponDrain <= 0f)
                return;

            float targetTotalRegen = totalWeaponDrain * regenToDrainRatio;
            float targetTotalCap = totalWeaponDrain * capacitySecondsAtFullDrain;
            float perEngineRegen = targetTotalRegen / engineEntries.Count;
            float perEngineCap = targetTotalCap / engineEntries.Count;

            for (int i = 0; i < engineEntries.Count; i++)
            {
                ShipFamilyComponentEntry entry = engineEntries[i];
                entry.stats.energyRegen = perEngineRegen;
                entry.stats.energyRegenPerLevel = perEngineRegen * PerLevelFractionOfBase;
                entry.stats.energyCap = perEngineCap;
                entry.stats.energyCapPerLevel = perEngineCap * PerLevelFractionOfBase;
                entry.stats = ShipComponentAbilityStats.KeepOnlyAuthoringFields(
                    entry.stats,
                    entry.statCategory,
                    entry.componentId);
            }
        }
    }
}
