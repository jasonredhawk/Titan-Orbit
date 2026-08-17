using System;
using UnityEngine;
using UnityEngine.Serialization;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Per-family multiplicative bonuses applied after prefab component sum + aggregation.
    /// Defaults are 1 (no change). Tune on each <see cref="ShipFamilyDefinition"/> —
    /// e.g. moveSpeedMul = 1.2 for a fast family, maxGemsMul = 1.5 for a cargo hauler,
    /// extraSpeedPercentMul / extraSpeedEnergyDrainMul scale engine-authored OVERDRIVE
    /// ExtraSpeedPercent and ExtraSpeedEnergyDrain (absolute OD energy/sec).
    /// <see cref="cameraHeightMul"/> is presentation-only (CameraFollowEcs) — it does not
    /// go through <see cref="Apply"/>. Shared part calc profiles stay project-wide; this is
    /// how families differ at runtime.
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
        /// × engine-authored OVERDRIVE <c>extraSpeedPercent</c> (1 = use engine value as-is).
        /// </summary>
        [Tooltip("× engine ExtraSpeedPercent (1 = use engine-authored 0.75).")]
        [FormerlySerializedAs("overdrivePercentMul")]
        [FormerlySerializedAs("overdriveSpeedMul")]
        public float extraSpeedPercentMul;

        /// <summary>
        /// × engine-authored OVERDRIVE <c>extraSpeedEnergyDrain</c> (1 = use engine value as-is).
        /// Scales absolute OD energy/sec — not multiplied by ExtraSpeedPercent.
        /// </summary>
        [Tooltip("× engine ExtraSpeedEnergyDrain (1 = use engine-authored 2).")]
        [FormerlySerializedAs("extraSpeedEnergyPercentMul")]
        public float extraSpeedEnergyDrainMul;

        public float maxGemsMul;
        public float maxPeopleMul;
        public float tractorDistanceMul;
        public float tractorPowerMul;

        /// <summary>
        /// × <see cref="CameraFollowSettings"/> world-Y height (1 = unchanged).
        /// Greater than 1 zooms the gameplay camera out; less than 1 zooms in.
        /// [TITAN-ORBIT] Presentation only — CameraFollowEcs reads this; sim stats do not.
        /// </summary>
        [Tooltip("× gameplay camera height (1 = unchanged). >1 zooms out, <1 zooms in.")]
        public float cameraHeightMul;

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
            extraSpeedPercentMul = 1f,
            extraSpeedEnergyDrainMul = 1f,
            maxGemsMul = 1f,
            maxPeopleMul = 1f,
            tractorDistanceMul = 1f,
            tractorPowerMul = 1f,
            cameraHeightMul = 1f,
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
                    && ApproxOne(extraSpeedPercentMul)
                    && ApproxOne(extraSpeedEnergyDrainMul)
                    && ApproxOne(maxGemsMul)
                    && ApproxOne(maxPeopleMul) && ApproxOne(tractorDistanceMul) && ApproxOne(tractorPowerMul)
                    && ApproxOne(cameraHeightMul);
            }
        }

        /// <summary>
        /// Applies multipliers to summed stats. Zero or negative authored muls are treated as 1
        /// so a fresh asset with unset floats does not zero the ship.
        /// </summary>
        public ShipComponentAbilityStats Apply(ShipComponentAbilityStats stats)
        {
            stats.firePower *= Mul(firePowerMul);
            stats.firePowerPerExtraLevel *= Mul(firePowerMul);
            stats.fireRate *= Mul(fireRateMul);
            stats.fireRatePerExtraLevel *= Mul(fireRateMul);
            stats.bulletSpeed *= Mul(bulletSpeedMul);
            stats.bulletSpeedPerExtraLevel *= Mul(bulletSpeedMul);
            stats.bulletRange *= Mul(bulletRangeMul);
            stats.bulletRangePerExtraLevel *= Mul(bulletRangeMul);
            stats.rammingPower *= Mul(rammingMul);
            stats.rammingPowerPerExtraLevel *= Mul(rammingMul);
            stats.healthCap *= Mul(healthCapMul);
            stats.healthCapPerExtraLevel *= Mul(healthCapMul);
            stats.healthRegen *= Mul(healthRegenMul);
            stats.healthRegenPerExtraLevel *= Mul(healthRegenMul);
            stats.energyCap *= Mul(energyCapMul);
            stats.energyCapPerExtraLevel *= Mul(energyCapMul);
            stats.energyRegen *= Mul(energyRegenMul);
            stats.energyRegenPerExtraLevel *= Mul(energyRegenMul);
            stats.moveSpeed *= Mul(moveSpeedMul);
            stats.moveSpeedPerExtraLevel *= Mul(moveSpeedMul);
            stats.accelerationCap *= Mul(accelerationMul);
            stats.accelerationCapPerExtraLevel *= Mul(accelerationMul);
            stats.extraSpeedPercent *= Mul(extraSpeedPercentMul);
            stats.extraSpeedPercentPerExtraLevel *= Mul(extraSpeedPercentMul);
            stats.extraSpeedEnergyDrain *= Mul(extraSpeedEnergyDrainMul);
            stats.extraSpeedEnergyDrainPerExtraLevel *= Mul(extraSpeedEnergyDrainMul);
            stats.turnSpeed *= Mul(turnSpeedMul);
            stats.turnSpeedPerExtraLevel *= Mul(turnSpeedMul);
            stats.maxGems *= Mul(maxGemsMul);
            stats.maxGemsPerExtraLevel *= Mul(maxGemsMul);
            stats.maxPeople *= Mul(maxPeopleMul);
            stats.maxPeoplePerExtraLevel *= Mul(maxPeopleMul);
            stats.tractorBeamDistance *= Mul(tractorDistanceMul);
            stats.tractorBeamDistancePerExtraLevel *= Mul(tractorDistanceMul);
            stats.tractorBeamPower *= Mul(tractorPowerMul);
            stats.tractorBeamPowerPerExtraLevel *= Mul(tractorPowerMul);
            return stats;
        }

        /// <summary>
        /// Resolves OVERDRIVE speed/thrust and absolute OD energy/sec from one ability pair
        /// × this family's ExtraSpeed muls. Drain = ExtraSpeedEnergyDrain (after mul) — not × speed %.
        /// </summary>
        public void ResolveOverdrive(
            in ShipFamilyOverdriveAbility profileDefaults,
            out float speedMultiplier,
            out float thrustMultiplier,
            out float energyDrainPerSecond)
        {
            profileDefaults.ResolveSpeedAndDrainRate(
                Mul(extraSpeedPercentMul),
                Mul(extraSpeedEnergyDrainMul),
                out speedMultiplier,
                out thrustMultiplier,
                out energyDrainPerSecond);
        }

        /// <summary>
        /// Camera height multiplier for CameraFollowEcs. Zero / unset authored values
        /// become 1 so a fresh asset does not pin the camera to the ship.
        /// </summary>
        public float ResolveCameraHeightMul() => Mul(cameraHeightMul);

        static float Mul(float value) => value > 0.0001f ? value : 1f;

        static bool ApproxOne(float value)
        {
            float m = Mul(value);
            return Mathf.Abs(m - 1f) < 0.0001f;
        }
    }
}
