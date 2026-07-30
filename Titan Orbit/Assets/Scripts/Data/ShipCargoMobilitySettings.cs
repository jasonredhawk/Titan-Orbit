using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// [UNITY] Designer-tunable cargo capacity → mobility tax. Ships with large gem/people holds
    /// (from summed chassis components) automatically lose MaxSpeed, acceleration, and turn —
    /// no Fighter/Miner/Transport role enum required.
    /// <para>
    /// [TITAN-ORBIT] Both MaxSpeed and acceleration are taxed whenever capacity &gt; 0.
    /// There is no "only tax speed when people &gt; gems" branch. The weight fields only set
    /// how hard each cargo type pulls on each stat: people weigh more on MaxSpeed; gems weigh
    /// more on acceleration; turn has separate gem/people weights (same defaults for now).
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
        // --- Per ship-level mobility drag (0 = off; applied before capacity tax) ---

        [Header("Per ship-level mobility penalty (0 = no effect)")]
        [Tooltip(
            "Fraction of MaxSpeed lost per ship level after 1. " +
            "Example: 0.11 at level 7 (6 steps) removes 66% of the pre-penalty move value. " +
            "0 = no level drag on top speed. Default 0.11 matches the old hard-coded curve.")]
        [Range(0f, 0.5f)]
        public float levelMaxSpeedPenaltyFractionPerLevel = 0.11f;

        [Tooltip(
            "Fraction of acceleration (EngineThrust) lost per ship level after 1. " +
            "0 = no level drag on accel (legacy behavior — accel only grew with *PerLevel). " +
            "Raise toward 0.11 if high-tier ships should also ramp slower from level alone.")]
        [Range(0f, 0.5f)]
        public float levelAccelPenaltyFractionPerLevel = 0f;

        [Tooltip(
            "Fraction of turn rate lost per ship level after 1. " +
            "0 = no level drag on yaw. Default 0.11 matches the old hard-coded curve.")]
        [Range(0f, 0.5f)]
        public float levelTurnPenaltyFractionPerLevel = 0.11f;

        // --- MaxSpeed: always taxed from gems + people; people weight is stronger ---

        [Header("MaxSpeed tax (capacity at apply + current load each tick; people weight stronger)")]
        [Tooltip(
            "Used twice: (1) GemCapacity at chassis apply, (2) CurrentGems each motor tick. " +
            "Collecting gems lowers live MaxSpeed (and the speedometer). " +
            "Keep smaller than speedWeightPerPerson.")]
        [Min(0f)]
        public float speedWeightPerGem = 0.002f;

        [Tooltip(
            "Used twice: (1) PeopleCapacity at apply, (2) CurrentPeople each motor tick. " +
            "Higher than speedWeightPerGem so people drag cruise harder than gems.")]
        [Min(0f)]
        public float speedWeightPerPerson = 0.015f;

        // --- Acceleration: always taxed from gems + people; gems weight is stronger ---

        [Header("Acceleration capacity tax (always taxed; gems weight stronger)")]
        [Tooltip(
            "ALWAYS added into the accel penalty: gems × this + people × accelWeightPerPerson. " +
            "Not a condition — people holds still slow ramp-up. Keep larger than accelWeightPerPerson.")]
        [Min(0f)]
        public float accelWeightPerGem = 0.008f;

        [Tooltip(
            "ALWAYS added into the accel penalty (with gems). " +
            "Smaller than accelWeightPerGem so gem barges ramp slower than people haulers at similar counts.")]
        [Min(0f)]
        public float accelWeightPerPerson = 0.004f;

        // --- Turn: always taxed; separate weights (same defaults for now) ---

        [Header("Turn tax (capacity at apply + current load each tick; separate weights)")]
        [Tooltip(
            "Used twice: (1) GemCapacity at apply, (2) CurrentGems each motor tick. " +
            "Same default as turnWeightPerPerson — tune independently later.")]
        [Min(0f)]
        public float turnWeightPerGem = 0.008f;

        [Tooltip(
            "Used twice: (1) PeopleCapacity at apply, (2) CurrentPeople each motor tick. " +
            "Same default as turnWeightPerGem — tune independently later.")]
        [Min(0f)]
        public float turnWeightPerPerson = 0.008f;

        // --- Floors (safety clamp — not on/off switches) ---

        [Header("Capacity-tax multiplier floors (safety clamp)")]
        [Tooltip(
            "Safety clamp only — NOT an on/off switch. After capacity tax, MaxSpeed multiplier is " +
            "max(this, 1/(1+penalty)). Example: 0.25 means even a huge hold cannot go below " +
            "25% of untaxed MaxSpeed. Raise toward 1 to soften extreme freighters; lower to allow heavier tax.")]
        [Range(0.05f, 1f)]
        public float minSpeedMultiplier = 0.25f;

        [Tooltip(
            "Safety clamp for EngineThrust / acceleration after capacity tax. Same meaning as minSpeedMultiplier.")]
        [Range(0.05f, 1f)]
        public float minAccelMultiplier = 0.25f;

        [Tooltip(
            "Safety clamp for RotationSpeed after capacity tax. Same meaning as minSpeedMultiplier.")]
        [Range(0.05f, 1f)]
        public float minTurnMultiplier = 0.25f;

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
            speedWeightPerGem = Mathf.Max(0f, speedWeightPerGem);
            speedWeightPerPerson = Mathf.Max(0f, speedWeightPerPerson);
            accelWeightPerGem = Mathf.Max(0f, accelWeightPerGem);
            accelWeightPerPerson = Mathf.Max(0f, accelWeightPerPerson);
            turnWeightPerGem = Mathf.Max(0f, turnWeightPerGem);
            turnWeightPerPerson = Mathf.Max(0f, turnWeightPerPerson);
            minSpeedMultiplier = Mathf.Clamp(minSpeedMultiplier, 0.05f, 1f);
            minAccelMultiplier = Mathf.Clamp(minAccelMultiplier, 0.05f, 1f);
            minTurnMultiplier = Mathf.Clamp(minTurnMultiplier, 0.05f, 1f);
        }

        /// <summary>[UNITY] Clamp whenever a designer edits this asset in the Inspector.</summary>
        void OnValidate() => ClampValues();
    }
}
