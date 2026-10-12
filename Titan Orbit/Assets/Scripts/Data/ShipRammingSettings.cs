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
    /// grind DPS = rating (the RAM chip);
    /// ram burst = rating × (1 + currentSpeed / this ship's stock cruise).
    /// Current speed is flight speed at the hit, including OVERDRIVE. Stock cruise is max
    /// speed before Move Speed ability purchases. At that speed the hit is 2× the chip.
    /// Going faster raises the ram.
    /// Hull mass and cargo do not scale HP. They still change flight and bounce.
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
        /// 1 = the RAM chip number is grind HP per second. A stock-cruise ram is 2× that chip.
        /// </summary>
        [Header("Overall strength")]
        [Tooltip(
            "Scales family rammingPower into the combat rating. " +
            "1 = the RAM chip (e.g. 5.8) is grind HP per second. " +
            "A ram at this ship's stock cruise (before Move Speed purchases) is 2× the chip. " +
            "0.5 = half strength; raise to make all rams and grinds meaner. " +
            "Not clamped — 0 disables ramming. Hull mass does not scale this.")]
        public float GlobalDamageMultiplier = 1f;

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
            SelfToAsteroidDamageRatio = Mathf.Max(0f, SelfToAsteroidDamageRatio);
        }

        /// <summary>[UNITY] Inspector edit — keep authored values legal.</summary>
        void OnValidate() => ClampValues();
    }
}
