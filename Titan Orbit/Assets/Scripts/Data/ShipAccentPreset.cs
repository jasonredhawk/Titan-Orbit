using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Designer-authored Color2 / Color3 / Glow slots. Color1 is never stored —
    /// it always comes from <see cref="TeamColor1Palette"/> so team identity stays
    /// readable. Create extras from the Team Color 1 Palette inspector.
    /// </summary>
    [CreateAssetMenu(
        fileName = "AccentPreset",
        menuName = "Titan Orbit/Ship Accent Preset",
        order = 66)]
    public class ShipAccentPreset : ScriptableObject
    {
        [Tooltip("Shown on Customize Ship.")]
        public string displayName = "Carbon";

        [Tooltip("Secondary hull paint. Keep this a neutral metal so Color1 stays the team read.")]
        public Color color2 = new Color(0.12f, 0.12f, 0.13f, 1f);

        [Tooltip("Tertiary hull paint. Same rule as Color2 — no second team hue.")]
        public Color color3 = new Color(0.28f, 0.28f, 0.30f, 1f);

        [Tooltip("Glow / emission 1. Lighting, not a team color.")]
        public Color emission1 = new Color(1f, 0.72f, 0.32f, 1f);

        [Tooltip("Glow / emission 2.")]
        public Color emission2 = new Color(1f, 0.86f, 0.62f, 1f);

        [Tooltip("Glow / emission 3.")]
        public Color emission3 = new Color(1f, 0.94f, 0.78f, 1f);
    }
}
