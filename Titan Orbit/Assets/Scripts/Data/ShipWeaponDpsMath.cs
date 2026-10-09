using System.Collections.Generic;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// All-gun sustained DPS and energy-sustain scoring for Titan Orbit.
    /// <para>
    /// Extra Level pools keep only the <b>primary</b> weapon for hull averages (HUD
    /// /hit, Extra Level <c>(N−1)</c> does not apply to guns). Combat still fires
    /// every chassis mount. Power bars, STATS chips, and upgrade-tree sort need the
    /// <b>sum of those barrels</b>: each one Extra-Levels on its own Base, then
    /// <c>firePower × fireRate</c>, then add. A 4-gun hull is four products, not
    /// one primary product. A moon-store weapon is not a fifth barrel — it only
    /// unlocks a bullet type. Selecting that type scales this DPS by the bank's
    /// fire-power and fire-rate multipliers.
    /// </para>
    /// <para>
    /// Combat spends <c>firePower</c> energy per shot, so all-gun DPS is also
    /// energy drain per second. A fat battery or fast regen lets a ship hold
    /// that output longer — upgrade-tree power score uses that sustain, not
    /// peak DPS alone. Paired with <see cref="ShipFamilyPowerScoreBreakdown"/>
    /// and <see cref="ShipComponentExtraLevelMath"/>.
    /// </para>
    /// </summary>
    public static class ShipWeaponDpsMath
    {
        /// <summary>
        /// Reference fight length for sustain scoring (seconds). Long enough to
        /// empty a typical weapon battery, short enough that burst still matters.
        /// </summary>
        public const float PowerScoreReferenceFightSeconds = 8f;

        /// <summary>
        /// Sum of every chassis barrel's Extra-Leveled <c>firePower × fireRate</c>.
        /// Non-weapons and cosmetic names are skipped. Each gun uses ship + Fire
        /// Power ability only (no component-count term).
        /// Moon-store rows at or after <paramref name="storeExtraStartIndex"/> are
        /// skipped: a purchased weapon unlocks a bullet type, it does not add a barrel.
        /// </summary>
        /// <param name="componentIds">Scale-adjusted part ids (prefab scan order, then store extras).</param>
        /// <param name="perComponentStats">Matching Base / PerExtra at starting scale.</param>
        /// <param name="shipLevel">Chassis tier (1-based).</param>
        /// <param name="attrs">HUD ability purchases (Fire Power steps each gun).</param>
        /// <param name="storeExtraStartIndex">
        /// First moon-store row. <see cref="int.MaxValue"/> when the list is chassis-only.
        /// </param>
        /// <returns>Chassis-gun DPS. 0 when the hull is unarmed.</returns>
        public static float SumAllGunDps(
            IReadOnlyList<string> componentIds,
            IReadOnlyList<ShipComponentAbilityStats> perComponentStats,
            int shipLevel,
            in ShipAbilityLevelCounts attrs,
            int storeExtraStartIndex = int.MaxValue)
        {
            if (componentIds == null || perComponentStats == null)
                return 0f;

            int n = Mathf.Min(componentIds.Count, perComponentStats.Count);
            // Store extras are appended after chassis parts. Stop before them so a
            // bought gun cannot raise the DPS the chip paints.
            int chassisCount = storeExtraStartIndex >= 0
                ? Mathf.Min(n, storeExtraStartIndex)
                : n;
            float dps = 0f;
            for (int i = 0; i < chassisCount; i++)
            {
                if (!TryEvaluateGun(componentIds[i], perComponentStats[i], shipLevel, in attrs,
                        out float firePower, out float fireRate))
                    continue;
                dps += ShipFamilyPowerScoreBreakdown.ComputeSustainedDps(firePower, fireRate);
            }

            return dps;
        }

        /// <summary>
        /// All-gun DPS after one more Fire Power purchase. Rate of Fire does not
        /// grow with that ability — only each gun's Fire Power PerExtra steps.
        /// </summary>
        public static float SumAllGunDpsAtNextFirePower(
            IReadOnlyList<string> componentIds,
            IReadOnlyList<ShipComponentAbilityStats> perComponentStats,
            int shipLevel,
            in ShipAbilityLevelCounts attrs,
            int storeExtraStartIndex = int.MaxValue)
        {
            ShipAbilityLevelCounts next = attrs;
            next.FirePower = attrs.FirePower + 1;
            return SumAllGunDps(
                componentIds, perComponentStats, shipLevel, in next, storeExtraStartIndex);
        }

        /// <summary>
        /// Family offense multipliers apply after the per-gun Extra Level sum
        /// (same as <see cref="ShipFamilySpecialBonuses.Apply"/> on the hull total).
        /// </summary>
        public static float ApplyFamilyOffenseMuls(float allGunDps, ShipFamilyDefinition family)
        {
            if (family == null)
                return Mathf.Max(0f, allGunDps);

            // Zero / unset family muls mean "no change" (same as SpecialBonuses.Apply).
            float fpMul = family.specialBonuses.firePowerMul > 0.01f ? family.specialBonuses.firePowerMul : 1f;
            float rateMul = family.specialBonuses.fireRateMul > 0.01f ? family.specialBonuses.fireRateMul : 1f;
            return Mathf.Max(0f, allGunDps) * fpMul * rateMul;
        }

        /// <summary>
        /// Average DPS over a short fight when energy drain equals all-gun DPS
        /// (one energy per point of Fire Power per shot).
        /// <para>
        /// If regen ≥ drain, the ship fires full DPS the whole fight.
        /// Otherwise the battery empties at <c>drain − regen</c>, then output
        /// falls to regen (each energy point is one damage).
        /// </para>
        /// </summary>
        /// <param name="dps">Peak all-gun DPS / energy drain per second.</param>
        /// <param name="energyCap">Hull energy battery.</param>
        /// <param name="energyRegen">Energy per second while firing.</param>
        /// <param name="fightSeconds">Window for the average (default 8 s).</param>
        /// <returns>Sustain-adjusted DPS used by upgrade-tree sort.</returns>
        public static float ComputeSustainAdjustedDps(
            float dps,
            float energyCap,
            float energyRegen,
            float fightSeconds = PowerScoreReferenceFightSeconds)
        {
            float peak = Mathf.Max(0f, dps);
            if (peak <= 0.0001f)
                return 0f;

            float t = Mathf.Max(0.1f, fightSeconds);
            float regen = Mathf.Max(0f, energyRegen);
            float cap = Mathf.Max(0f, energyCap);

            // --- Infinite sustain ---
            if (regen + 0.0001f >= peak)
                return peak;

            // --- Burst until empty, then limp at regen ---
            float netDrain = peak - regen;
            float emptyIn = cap / netDrain;
            if (emptyIn >= t)
                return peak;

            float burstDamage = emptyIn * peak;
            float limpDamage = (t - emptyIn) * regen;
            return (burstDamage + limpDamage) / t;
        }

        /// <summary>
        /// Extra-Levels one gun (ship + ability, no N) when the id is a weapon
        /// with Fire Power. Cosmetic / non-weapon ids return false.
        /// </summary>
        static bool TryEvaluateGun(
            string componentId,
            in ShipComponentAbilityStats stats,
            int shipLevel,
            in ShipAbilityLevelCounts attrs,
            out float firePower,
            out float fireRate)
        {
            firePower = 0f;
            fireRate = 0f;
            if (string.IsNullOrWhiteSpace(componentId))
                return false;
            if (ShipFamilyPartCalcProfileSet.IsCosmeticPartName(componentId))
                return false;
            if (!ShipComponentAbilityStatsMath.IsWeaponComponent(componentId))
                return false;
            if (stats.firePower <= 0.01f && stats.firePowerPerExtraLevel <= 0.01f)
                return false;

            firePower = ShipComponentExtraLevelMath.Evaluate(
                stats.firePower,
                stats.firePowerPerExtraLevel,
                shipLevel,
                attrs.FirePower,
                componentCount: 1,
                includeExtraComponentLevels: false);
            fireRate = ShipComponentExtraLevelMath.Evaluate(
                stats.fireRate,
                stats.fireRatePerExtraLevel,
                shipLevel,
                abilityLevel: 0,
                componentCount: 1,
                includeExtraComponentLevels: false);
            return firePower > 0.0001f || fireRate > 0.0001f;
        }
    }
}
