using System;
using UnityEngine;
using UnityEngine.Serialization;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Per-family multiplicative bonuses applied after prefab component sum + aggregation.
    /// Defaults are 1 (no change). Tune on each <see cref="ShipFamilyDefinition"/> —
    /// e.g. moveSpeedMul = 1.2 for a fast family, maxGemsMul = 1.5 for a cargo hauler,
    /// extraSpeedPercentMul / extraSpeedEnergyPercentMul scale engine-authored OVERDRIVE
    /// ExtraSpeed / ExtraSpeedEnergy (0.75 speed / 2.0 energy factor), thrustEnergyDrainMul = 0.8
    /// for efficient thrusters.
    /// Shared part calc profiles stay project-wide; this is how families differ at runtime.
    /// </summary>
    [Serializable]
    public struct ShipFamilySpecialBonuses
    {
        [Tooltip("Multiplier on aggregated move speed (1 = unchanged).")]
        public float moveSpeedMul;
        [Tooltip("Multiplier on aggregated acceleration cap.")]
        public float accelerationMul;
        [Tooltip("Multiplier on aggregated turn speed.")]
        public float turnSpeedMul;
        public float firePowerMul;
        public float fireRateMul;
        public float bulletSpeedMul;
        /// <summary>
        /// Multiplier on aggregated bullet travel range (base + per-level).
        /// [TITAN-ORBIT] Family identity lever — not a player attribute upgrade.
        /// </summary>
        public float bulletRangeMul;
        public float rammingMul;
        public float healthCapMul;
        public float healthRegenMul;
        public float energyCapMul;
        public float energyRegenMul;
        /// <summary>
        /// Multiplier on summed engine+thruster <c>thrustEnergyDrain</c> (OVERDRIVE energy cost base).
        /// Normal RMB thrust does not spend energy.
        /// 1 = ProfileSet / part authoring; 0.8 = 20% more efficient; 1.5 = hungrier.
        /// </summary>
        [Tooltip("× thrustEnergyDrain for OVERDRIVE cost on this family (1 = unchanged).")]
        public float thrustEnergyDrainMul;

        /// <summary>
        /// × engine-authored OVERDRIVE <c>extraSpeedPercent</c> (1 = use engine value as-is).
        /// </summary>
        [Tooltip("× engine ExtraSpeedPercent (1 = use engine-authored 0.75).")]
        [FormerlySerializedAs("overdrivePercentMul")]
        [FormerlySerializedAs("overdriveSpeedMul")]
        public float extraSpeedPercentMul;

        /// <summary>
        /// × engine-authored OVERDRIVE <c>extraSpeedEnergyPercent</c> (1 = use engine value as-is).
        /// </summary>
        [Tooltip("× engine ExtraSpeedEnergyPercent (1 = use engine-authored 2.0).")]
        public float extraSpeedEnergyPercentMul;

        public float maxGemsMul;
        public float maxPeopleMul;
        public float tractorDistanceMul;
        public float tractorPowerMul;

        /// <summary>Identity bonuses (all multipliers = 1).</summary>
        public static ShipFamilySpecialBonuses Identity => new ShipFamilySpecialBonuses
        {
            moveSpeedMul = 1f,
            accelerationMul = 1f,
            turnSpeedMul = 1f,
            firePowerMul = 1f,
            fireRateMul = 1f,
            bulletSpeedMul = 1f,
            bulletRangeMul = 1f,
            rammingMul = 1f,
            healthCapMul = 1f,
            healthRegenMul = 1f,
            energyCapMul = 1f,
            energyRegenMul = 1f,
            thrustEnergyDrainMul = 1f,
            extraSpeedPercentMul = 1f,
            extraSpeedEnergyPercentMul = 1f,
            maxGemsMul = 1f,
            maxPeopleMul = 1f,
            tractorDistanceMul = 1f,
            tractorPowerMul = 1f,
        };

        /// <summary>True when every multiplier is approximately 1 (or zero/unset → treat as 1).</summary>
        public bool IsIdentity
        {
            get
            {
                return ApproxOne(moveSpeedMul) && ApproxOne(accelerationMul) && ApproxOne(turnSpeedMul)
                    && ApproxOne(firePowerMul) && ApproxOne(fireRateMul) && ApproxOne(bulletSpeedMul)
                    && ApproxOne(bulletRangeMul)
                    && ApproxOne(rammingMul) && ApproxOne(healthCapMul) && ApproxOne(healthRegenMul)
                    && ApproxOne(energyCapMul) && ApproxOne(energyRegenMul)
                    && ApproxOne(thrustEnergyDrainMul)
                    && ApproxOne(extraSpeedPercentMul)
                    && ApproxOne(extraSpeedEnergyPercentMul)
                    && ApproxOne(maxGemsMul)
                    && ApproxOne(maxPeopleMul) && ApproxOne(tractorDistanceMul) && ApproxOne(tractorPowerMul);
            }
        }

        /// <summary>
        /// Applies multipliers to summed stats. Zero or negative authored muls are treated as 1
        /// so a fresh asset with unset floats does not zero the ship.
        /// </summary>
        public ShipComponentAbilityStats Apply(ShipComponentAbilityStats stats)
        {
            stats.firePower *= Mul(firePowerMul);
            stats.firePowerPerLevel *= Mul(firePowerMul);
            stats.fireRate *= Mul(fireRateMul);
            stats.fireRatePerLevel *= Mul(fireRateMul);
            stats.bulletSpeed *= Mul(bulletSpeedMul);
            stats.bulletSpeedPerLevel *= Mul(bulletSpeedMul);
            stats.bulletRange *= Mul(bulletRangeMul);
            stats.bulletRangePerLevel *= Mul(bulletRangeMul);
            stats.rammingPower *= Mul(rammingMul);
            stats.rammingPowerPerLevel *= Mul(rammingMul);
            stats.healthCap *= Mul(healthCapMul);
            stats.healthCapPerLevel *= Mul(healthCapMul);
            stats.healthRegen *= Mul(healthRegenMul);
            stats.healthRegenPerLevel *= Mul(healthRegenMul);
            stats.energyCap *= Mul(energyCapMul);
            stats.energyCapPerLevel *= Mul(energyCapMul);
            stats.energyRegen *= Mul(energyRegenMul);
            stats.energyRegenPerLevel *= Mul(energyRegenMul);
            stats.moveSpeed *= Mul(moveSpeedMul);
            stats.moveSpeedPerLevel *= Mul(moveSpeedMul);
            stats.accelerationCap *= Mul(accelerationMul);
            stats.accelerationCapPerLevel *= Mul(accelerationMul);
            stats.thrustEnergyDrain *= Mul(thrustEnergyDrainMul);
            stats.thrustEnergyDrainPerLevel *= Mul(thrustEnergyDrainMul);
            stats.extraSpeedPercent *= Mul(extraSpeedPercentMul);
            stats.extraSpeedPercentPerLevel *= Mul(extraSpeedPercentMul);
            stats.extraSpeedEnergyPercent *= Mul(extraSpeedEnergyPercentMul);
            stats.extraSpeedEnergyPercentPerLevel *= Mul(extraSpeedEnergyPercentMul);
            stats.turnSpeed *= Mul(turnSpeedMul);
            stats.turnSpeedPerLevel *= Mul(turnSpeedMul);
            stats.maxGems *= Mul(maxGemsMul);
            stats.maxGemsPerLevel *= Mul(maxGemsMul);
            stats.maxPeople *= Mul(maxPeopleMul);
            stats.maxPeoplePerLevel *= Mul(maxPeopleMul);
            stats.tractorBeamDistance *= Mul(tractorDistanceMul);
            stats.tractorBeamDistancePerLevel *= Mul(tractorDistanceMul);
            stats.tractorBeamPower *= Mul(tractorPowerMul);
            stats.tractorBeamPowerPerLevel *= Mul(tractorPowerMul);
            return stats;
        }

        /// <summary>
        /// Resolves OVERDRIVE speed/thrust/drain:
        /// Resolves OVERDRIVE speed/thrust/drain from engine-authored (or fallback) ability
        /// × this family's <see cref="extraSpeedPercentMul"/> / <see cref="extraSpeedEnergyPercentMul"/>.
        /// </summary>
        public void ResolveOverdrive(
            in ShipFamilyOverdriveAbility profileDefaults,
            out float speedMultiplier,
            out float thrustMultiplier,
            out float energyDrainMultiplier)
        {
            profileDefaults.ResolveMultipliers(
                Mul(extraSpeedPercentMul),
                Mul(extraSpeedEnergyPercentMul),
                out speedMultiplier,
                out thrustMultiplier,
                out energyDrainMultiplier);
        }

        static float Mul(float value) => value > 0.0001f ? value : 1f;

        static bool ApproxOne(float value)
        {
            float m = Mul(value);
            return Mathf.Abs(m - 1f) < 0.0001f;
        }
    }
}
