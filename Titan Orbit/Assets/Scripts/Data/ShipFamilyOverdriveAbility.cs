using System;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// OVERDRIVE ability math helpers + project default fractions.
    /// Runtime values are authored on <b>engine</b> components
    /// (<see cref="ShipComponentAbilityStats.extraSpeedPercent"/> /
    /// <see cref="ShipComponentAbilityStats.extraSpeedEnergyPercent"/>);
    /// ProfileSet <c>overdriveAbility</c> is a legacy fallback only.
    /// <list type="bullet">
    /// <item><see cref="extraSpeedPercent"/> — 0.75 = +75% speed/thrust (1.75×)</item>
    /// <item><see cref="extraSpeedEnergyPercent"/> — 2.0 = energy extra is 2× the speed fraction
    /// (+150% energy when speed is +75% → drain 2.5×)</item>
    /// </list>
    /// Families scale both via <see cref="ShipFamilySpecialBonuses.extraSpeedPercentMul"/> and
    /// <see cref="ShipFamilySpecialBonuses.extraSpeedEnergyPercentMul"/>.
    /// </summary>
    [Serializable]
    public struct ShipFamilyOverdriveAbility
    {
        /// <summary>
        /// Extra top-speed / thrust while overdrive is active as a fraction
        /// (0.75 = +75% → MaxSpeed × 1.75).
        /// </summary>
        [Tooltip("Extra speed/thrust as a fraction (0.75 = +75% → 1.75×).")]
        [Range(0f, 3f)]
        public float extraSpeedPercent;

        /// <summary>
        /// How many times the speed fraction is added as energy cost
        /// (2.0 with 0.75 speed → +1.50 energy → drain × 2.5).
        /// </summary>
        [Tooltip("Energy extra factor vs speed fraction (2.0 → energy extra = 2 × extraSpeedPercent).")]
        [Range(0f, 10f)]
        public float extraSpeedEnergyPercent;

        /// <summary>Default +75% speed (0.75).</summary>
        public const float DefaultExtraSpeedPercent = 0.75f;

        /// <summary>Default energy factor (2 → energy extra = 2 × speed fraction).</summary>
        public const float DefaultExtraSpeedEnergyPercent = 2f;

        /// <summary>Project defaults when ProfileSet has never been authored.</summary>
        public static ShipFamilyOverdriveAbility Default => new ShipFamilyOverdriveAbility
        {
            extraSpeedPercent = DefaultExtraSpeedPercent,
            extraSpeedEnergyPercent = DefaultExtraSpeedEnergyPercent,
        };

        /// <summary>
        /// Clamps fields. Migrates legacy authored values that used 0–100 style
        /// (e.g. 75 → 0.75) when the asset still has the old scale.
        /// </summary>
        public ShipFamilyOverdriveAbility Resolved()
        {
            float speed = extraSpeedPercent;
            // Legacy: 75 meant +75%. New: 0.75.
            if (speed > 3.01f)
                speed *= 0.01f;
            if (speed <= 0.0001f)
                speed = DefaultExtraSpeedPercent;

            float energy = extraSpeedEnergyPercent;
            if (energy <= 0.0001f)
                energy = DefaultExtraSpeedEnergyPercent;

            return new ShipFamilyOverdriveAbility
            {
                extraSpeedPercent = speed,
                extraSpeedEnergyPercent = energy,
            };
        }

        /// <summary>
        /// Applies family muls, then returns motor speed/thrust/drain multipliers.
        /// speed = 1 + (extraSpeedPercent × speedMul);
        /// drain = 1 + (extraSpeedPercent × speedMul) × (extraSpeedEnergyPercent × energyMul).
        /// </summary>
        public void ResolveMultipliers(
            float extraSpeedPercentMul,
            float extraSpeedEnergyPercentMul,
            out float speedMultiplier,
            out float thrustMultiplier,
            out float energyDrainMultiplier)
        {
            ShipFamilyOverdriveAbility bas = Resolved();
            float speedMul = extraSpeedPercentMul > 0.0001f ? extraSpeedPercentMul : 1f;
            float energyMul = extraSpeedEnergyPercentMul > 0.0001f ? extraSpeedEnergyPercentMul : 1f;

            float speedFraction = Mathf.Max(0f, bas.extraSpeedPercent * speedMul);
            float energyFactor = Mathf.Max(0f, bas.extraSpeedEnergyPercent * energyMul);

            speedMultiplier = 1f + speedFraction;
            thrustMultiplier = speedMultiplier;
            energyDrainMultiplier = 1f + speedFraction * energyFactor;
        }
    }
}
