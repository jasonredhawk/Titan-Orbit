using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// [UNITY] Designer-tunable ramming / grind damage balance. Edit this asset in the Inspector —
    /// no code changes needed for the sliders below.
    /// Sole asset: <c>Assets/Resources/ShipRammingSettings.asset</c>
    /// (Create via Assets → Create → Titan Orbit → Ship Ramming Settings).
    /// Loaded at play by <see cref="Game.ShipRammingSettingsLoader"/> via <c>Resources.Load</c>
    /// so Editor and player builds share one file — no Data/ duplicate.
    /// <para>
    /// [TITAN-ORBIT] Formulas live in <see cref="ShipComponentRammingSuggestions"/> so the HUD
    /// and server <c>ShipRammingCollisionDamageSystem</c> cannot drift:
    /// grind DPS = rating × (totalMass / MassReference);
    /// ram burst = grind DPS × (1 + closingSpeed / RamClosingSpeedForDouble).
    /// </para>
    /// </summary>
    [CreateAssetMenu(
        fileName = "ShipRammingSettings",
        menuName = "Titan Orbit/Ship Ramming Settings",
        order = 53)]
    public class ShipRammingSettings : ScriptableObject
    {
        /// <summary>
        /// Scales summed family <c>rammingPower</c> into the combat rating.
        /// 1 = the RAM chip number is the grind DPS at <see cref="MassReference"/>.
        /// </summary>
        [Header("Overall strength")]
        [Tooltip(
            "Scales family rammingPower into the combat rating. " +
            "1 = the RAM chip (e.g. 5.8) is grind DPS when totalMass equals MassReference. " +
            "0.5 = half strength; raise to make all rams and grinds meaner. " +
            "Not clamped — 0 disables ramming.")]
        public float GlobalDamageMultiplier = 1f;

        /// <summary>
        /// Mobility totalMass at which grind DPS equals the RAM chip × GlobalDamageMultiplier.
        /// Heavier hulls and cargo scale above that; lighter hulls scale below.
        /// </summary>
        [Tooltip(
            "Mobility totalMass (gems + people + ComponentSize) at which grind DPS equals " +
            "the RAM chip. Default 10 matches a typical empty hull after HullMassScale. " +
            "A ship at mass 20 then grinds at 2× the chip; mass 5 grinds at half. " +
            "Must stay above 0.")]
        [Min(0.01f)]
        public float MassReference = 10f;

        /// <summary>
        /// Closing speed (world u/s) at which a ram burst is 2× that ship's grind DPS.
        /// Formula: ramMul = 1 + closingSpeed / this.
        /// </summary>
        [Tooltip(
            "Approach speed (world units/s) that doubles grind DPS into a ram burst. " +
            "ram = grindDPS × (1 + closing / this). At 0 closing the multiplier is 1× " +
            "(impact is still gated at 0.35 u/s). At this speed the hit is 2× grind; " +
            "at 2× this speed it is 3×. Default 10.")]
        [Min(0.01f)]
        public float RamClosingSpeedForDouble = 10f;

        /// <summary>
        /// Self hull chip vs damage dealt on the same hit.
        /// Below 1 = you hurt the rock/enemy more than yourself.
        /// </summary>
        [Header("Self vs target")]
        [Tooltip(
            "Self hull chip vs damage dealt on the same hit. " +
            "Below 1 = you hurt the rock/enemy more than yourself; " +
            "above 1 = ramming is self-punishing. " +
            "Does not reduce asteroid damage by itself — lower GlobalDamageMultiplier for that.")]
        [Min(0f)]
        public float SelfToAsteroidDamageRatio = 2f;

        /// <summary>
        /// Sanitizes designer fields after Inspector edits.
        /// <see cref="GlobalDamageMultiplier"/> is left as authored (not clamped)
        /// so 0 can disable ramming without a code change.
        /// Grind pulse interval lives on <see cref="AsteroidSettings"/> (rock-contact pacing).
        /// </summary>
        public void ClampValues()
        {
            // --- Sanitize designer fields ---
            // [TITAN-ORBIT] GlobalDamageMultiplier is intentional free-range — designers may set
            // 0 (off), very small, or very large without a floor/ceiling rewrite.
            // MassReference and RamClosingSpeedForDouble are divisors — a 0 would explode damage.
            SelfToAsteroidDamageRatio = Mathf.Max(0f, SelfToAsteroidDamageRatio);
            MassReference = Mathf.Max(0.01f, MassReference);
            RamClosingSpeedForDouble = Mathf.Max(0.01f, RamClosingSpeedForDouble);
        }

        /// <summary>[UNITY] Inspector edit — keep authored values legal.</summary>
        void OnValidate() => ClampValues();
    }
}
