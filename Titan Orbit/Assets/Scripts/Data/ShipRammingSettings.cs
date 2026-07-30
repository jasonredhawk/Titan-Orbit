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
            "0.5 = half as nasty as family rammingPower alone; 1 = full; raise to make rams meaner.")]
        [Min(0.01f)]
        public float GlobalDamageMultiplier = 0.5f;

        [Header("Self vs target")]
        [Tooltip(
            "Self hull chip vs damage dealt on the same hit. " +
            "Below 1 = you hurt the rock/enemy more than yourself; " +
            "above 1 = ramming is self-punishing. " +
            "Does not reduce asteroid damage by itself — lower GlobalDamageMultiplier for that.")]
        [Min(0f)]
        public float SelfToAsteroidDamageRatio = 2f;

        /// <summary>Keeps multipliers in a sane range after Inspector edits.</summary>
        public void ClampValues()
        {
            // --- Sanitize designer fields ---
            GlobalDamageMultiplier = Mathf.Max(0.01f, GlobalDamageMultiplier);
            SelfToAsteroidDamageRatio = Mathf.Max(0f, SelfToAsteroidDamageRatio);
        }

        void OnValidate() => ClampValues();
    }
}
