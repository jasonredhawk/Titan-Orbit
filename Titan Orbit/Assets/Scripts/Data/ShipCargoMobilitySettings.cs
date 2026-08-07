using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// [UNITY] Designer-tunable cargo + hull size → mobility tax.
    /// One mass number feeds Speed / Accel / Turn subtractive drag — no per-stat gem weights,
    /// no ×10 thrust visibility, no F/m for flight accel.
    /// <para>
    /// [TITAN-ORBIT] Mental model:
    /// <c>totalMass = gems×MassPerGem + people×MassPerPerson + componentSize×MassPerComponentSize</c>
    /// then <c>stat' = max(floor, untaxed − totalMass × WeightPerMass)</c>.
    /// Gems/people are <b>current</b> counts at drive/HUD time; componentSize is live hull size
    /// (box × attribute grow × tier, then hull scale — stored as HullMassReference).
    /// </para>
    /// Sole asset: <c>Assets/Resources/ShipCargoMobilitySettings.asset</c>
    /// (Create via Assets → Create → Titan Orbit → Ship Cargo Mobility Settings).
    /// Loaded at play by <see cref="Game.ShipCargoMobilitySettingsLoader"/> via <c>Resources.Load</c>.
    /// Formulas live in <see cref="ShipMobilityResolution"/> so HUD and motor stay matched.
    /// <para>
    /// Also owns <b>per-ship-level</b> mobility penalties (MaxSpeed / accel / turn). 0 = no level
    /// drag; defaults match the old hard-coded 11% move/turn and 0% accel curves.
    /// </para>
    /// </summary>
    [CreateAssetMenu(
        fileName = "ShipCargoMobilitySettings",
        menuName = "Titan Orbit/Ship Cargo Mobility Settings",
        order = 54)]
    public class ShipCargoMobilitySettings : ScriptableObject
    {
        // --- Per ship-level mobility drag (0 = off; applied before mass tax) ---

        [Header("Per ship-level mobility penalty (0 = no effect)")]
        [Tooltip(
            "Fraction of MaxSpeed lost per ship level after 1. " +
            "Example: 0.11 at level 7 (6 steps) removes 66% of the pre-penalty move value. " +
            "0 = no level drag on top speed. Default 0.11 matches the old hard-coded curve.")]
        [Range(0f, 0.5f)]
        public float levelMaxSpeedPenaltyFractionPerLevel = 0.11f;

        [Tooltip(
            "Fraction of acceleration lost per ship level after 1. " +
            "0 = no level drag on accel (legacy behavior — accel only grew with *PerLevel). " +
            "Raise toward 0.11 if high-tier ships should also ramp slower from level alone.")]
        [Range(0f, 0.5f)]
        public float levelAccelPenaltyFractionPerLevel = 0f;

        [Tooltip(
            "Fraction of turn rate lost per ship level after 1. " +
            "0 = no level drag on yaw. Default 0.11 matches the old hard-coded curve.")]
        [Range(0f, 0.5f)]
        public float levelTurnPenaltyFractionPerLevel = 0.11f;

        // --- Build totalMass first ---

        [Header("Mass contributors (build totalMass)")]
        [Tooltip(
            "How much one carried gem adds to totalMass. " +
            "Also used by ShipMassLogic for orbit/recoil/ramming cargo weight.")]
        [Min(0f)]
        public float massPerGem = 0.01f;

        [Tooltip(
            "How much one carried person adds to totalMass. " +
            "Also used by ShipMassLogic for orbit/recoil/ramming cargo weight. " +
            "Default higher than massPerGem so people haulers feel heavier.")]
        [Min(0f)]
        public float massPerPerson = 0.15f;

        [Tooltip(
            "How much one unit of ComponentSize (live hull box size → HullMassReference) " +
            "adds to totalMass. Bigger ships have more ComponentSize and pay more mass tax.")]
        [Min(0f)]
        public float massPerComponentSize = 1f;

        // --- Subtract totalMass × weight from each mobility stat ---

        [Header("Mobility drag per unit of totalMass (subtractive)")]
        [Tooltip(
            "MaxSpeed lost per unit of totalMass. " +
            "Example: totalMass 10 × 0.1 → −1 MaxSpeed from the untaxed chassis value.")]
        [Min(0f)]
        public float speedWeightPerMass = 0.1f;

        [Tooltip(
            "Acceleration lost per unit of totalMass (same units as chassis Accel / EngineThrust).")]
        [Min(0f)]
        public float accelWeightPerMass = 0.1f;

        [Tooltip(
            "Turn rate (°/s) lost per unit of totalMass.")]
        [Min(0f)]
        public float turnWeightPerMass = 0.5f;

        // --- Absolute floors after subtract ---

        [Header("Absolute floors after mass tax (safety clamp)")]
        [Tooltip(
            "Minimum MaxSpeed after subtractive mass tax. " +
            "0 allows mass tax to zero cruise; raise if heavy ships should keep some top speed.")]
        [Min(0f)]
        public float minSpeed = 0.1f;

        [Tooltip(
            "Minimum acceleration after subtractive mass tax. " +
            "0 allows mass tax to zero accel.")]
        [Min(0f)]
        public float minAccel = 0.1f;

        [Tooltip(
            "Minimum turn rate (°/s) after subtractive mass tax. " +
            "0 allows mass tax to zero out turn; raise if heavy ships should keep some yaw.")]
        [Min(0f)]
        public float minTurn = 1f;

        /// <summary>
        /// Keeps weights and floors in a sane range after Inspector edits.
        /// Called from OnValidate and from the loader before publishing the cache.
        /// </summary>
        public void ClampValues()
        {
            // --- Sanitize designer fields ---
            levelMaxSpeedPenaltyFractionPerLevel = Mathf.Clamp(levelMaxSpeedPenaltyFractionPerLevel, 0f, 0.5f);
            levelAccelPenaltyFractionPerLevel = Mathf.Clamp(levelAccelPenaltyFractionPerLevel, 0f, 0.5f);
            levelTurnPenaltyFractionPerLevel = Mathf.Clamp(levelTurnPenaltyFractionPerLevel, 0f, 0.5f);
            massPerGem = Mathf.Max(0f, massPerGem);
            massPerPerson = Mathf.Max(0f, massPerPerson);
            massPerComponentSize = Mathf.Max(0f, massPerComponentSize);
            speedWeightPerMass = Mathf.Max(0f, speedWeightPerMass);
            accelWeightPerMass = Mathf.Max(0f, accelWeightPerMass);
            turnWeightPerMass = Mathf.Max(0f, turnWeightPerMass);
            minSpeed = Mathf.Max(0f, minSpeed);
            minAccel = Mathf.Max(0f, minAccel);
            minTurn = Mathf.Max(0f, minTurn);
        }

        /// <summary>[UNITY] Clamp whenever a designer edits this asset in the Inspector.</summary>
        void OnValidate() => ClampValues();
    }
}
