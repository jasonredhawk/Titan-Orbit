using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// [UNITY] Designer-tunable ramming damage balance. Edit this asset in the Inspector —
    /// no code changes needed for GlobalDamageMultiplier / SelfToAsteroidDamageRatio.
    /// Sole asset: <c>Assets/Resources/ShipRammingSettings.asset</c>
    /// (Create via Assets → Create → Titan Orbit → Ship Ramming Settings).
    /// Loaded at play by <see cref="Game.ShipRammingSettingsLoader"/> via <c>Resources.Load</c>
    /// so Editor and player builds share one file — no Data/ duplicate. Formulas live in
    /// <see cref="ShipComponentRammingSuggestions"/> so HUD and server stay matched.
    /// </summary>
    [CreateAssetMenu(
        fileName = "ShipRammingSettings",
        menuName = "Titan Orbit/Ship Ramming Settings",
        order = 53)]
    public class ShipRammingSettings : ScriptableObject
    {
        [Header("Overall strength")]
        [Tooltip(
            "Scales all ramming damage (asteroid, enemy ship, and self). " +
            "Damage = rating × totalMass × closingSpeed (impact) or × taxedAccel (grind). " +
            "0.5 = softer; 1 = full family rammingPower; raise to make rams meaner. " +
            "Not clamped — set freely (including below 0.01 or 0 to disable).")]
        public float GlobalDamageMultiplier = 0.5f;

        [Header("Self vs target")]
        [Tooltip(
            "Self hull chip vs damage dealt on the same hit. " +
            "Below 1 = you hurt the rock/enemy more than yourself; " +
            "above 1 = ramming is self-punishing. " +
            "Does not reduce asteroid damage by itself — lower GlobalDamageMultiplier for that.")]
        [Min(0f)]
        public float SelfToAsteroidDamageRatio = 2f;

        /// <summary>
        /// Sanitizes self-damage ratio after Inspector edits.
        /// <see cref="GlobalDamageMultiplier"/> is left as authored (not clamped).
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
