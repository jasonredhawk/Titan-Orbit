using System;
using UnityEngine;
using UnityEngine.Serialization;

namespace TitanOrbit.Data
{
    /// <summary>
    /// OVERDRIVE ability math helpers + project default fractions.
    /// Runtime values are authored on <b>engine</b> components
    /// (<see cref="ShipComponentAbilityStats.extraSpeedPercent"/> /
    /// <see cref="ShipComponentAbilityStats.extraSpeedEnergyDrain"/>).
    /// <list type="bullet">
    /// <item><see cref="extraSpeedPercent"/> — 0.5 = +50% speed/thrust (1.5×)</item>
    /// <item><see cref="extraSpeedEnergyDrain"/> — absolute OD energy/sec from this engine
    /// (e.g. 2 = spend 2 energy per second while OVERDRIVE is active). Not multiplied by speed %.</item>
    /// </list>
    /// Families scale both via <see cref="ShipFamilySpecialBonuses.extraSpeedPercentMul"/> and
    /// <see cref="ShipFamilySpecialBonuses.extraSpeedEnergyDrainMul"/>.
    /// </summary>
    [Serializable]
    public struct ShipFamilyOverdriveAbility
    {
        /// <summary>
        /// Extra top-speed / thrust while overdrive is active as a fraction
        /// (0.5 = +50% → MaxSpeed × 1.5).
        /// </summary>
        [Tooltip("Extra speed/thrust as a fraction (0.5 = +50% → 1.5×).")]
        [Range(0f, 3f)]
        public float extraSpeedPercent;

        /// <summary>
        /// Absolute OVERDRIVE energy spend per second from this engine (not a multiplier).
        /// Example: 2 = drain 2 energy/sec while burst is active.
        /// </summary>
        [Tooltip("OD energy/sec while active (absolute rate, e.g. 2). Not × ExtraSpeedPercent.")]
        [FormerlySerializedAs("extraSpeedEnergyPercent")]
        [Range(0f, 50f)]
        public float extraSpeedEnergyDrain;

        /// <summary>Default +75% speed (0.75).</summary>
        public const float DefaultExtraSpeedPercent = 0.75f;

        /// <summary>Default OD energy/sec when unset (2 energy per second).</summary>
        public const float DefaultExtraSpeedEnergyDrain = 2f;

        /// <summary>Project defaults when ProfileSet has never been authored.</summary>
        public static ShipFamilyOverdriveAbility Default => new ShipFamilyOverdriveAbility
        {
            extraSpeedPercent = DefaultExtraSpeedPercent,
            extraSpeedEnergyDrain = DefaultExtraSpeedEnergyDrain,
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

            float drain = extraSpeedEnergyDrain;
            if (drain <= 0.0001f)
                drain = DefaultExtraSpeedEnergyDrain;

            return new ShipFamilyOverdriveAbility
            {
                extraSpeedPercent = speed,
                extraSpeedEnergyDrain = drain,
            };
        }

        /// <summary>
        /// Speed/thrust mul = 1 + (extraSpeedPercent × speedMul).
        /// Absolute OD energy/sec = ExtraSpeedEnergyDrain × energyMul (no multiply by speed %).
        /// </summary>
        public void ResolveSpeedAndDrainRate(
            float extraSpeedPercentMul,
            float extraSpeedEnergyDrainMul,
            out float speedMultiplier,
            out float thrustMultiplier,
            out float energyDrainPerSecond)
        {
            ShipFamilyOverdriveAbility bas = Resolved();
            float speedMul = extraSpeedPercentMul > 0.0001f ? extraSpeedPercentMul : 1f;
            float energyMul = extraSpeedEnergyDrainMul > 0.0001f ? extraSpeedEnergyDrainMul : 1f;

            float speedFraction = Mathf.Max(0f, bas.extraSpeedPercent * speedMul);

            speedMultiplier = 1f + speedFraction;
            thrustMultiplier = speedMultiplier;
            // [TITAN-ORBIT] Absolute rate as authored — not ExtraSpeedPercent × drain.
            energyDrainPerSecond = Mathf.Max(0f, bas.extraSpeedEnergyDrain * energyMul);
        }
    }
}
