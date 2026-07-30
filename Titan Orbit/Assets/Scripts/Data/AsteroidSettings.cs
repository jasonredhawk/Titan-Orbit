using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// [UNITY] Designer-tunable asteroid body feel: size range, hit points per size, and gems per size.
    /// Edit this asset in the Inspector — no code changes needed for balance tweaks.
    /// Sole asset: <c>Assets/Resources/AsteroidSettings.asset</c>
    /// (Create via Assets → Create → Titan Orbit → Asteroid Settings).
    /// Loaded at play by <see cref="Game.AsteroidSettingsLoader"/> via <c>Resources.Load</c>
    /// so Editor and player builds share one file — no Data/ duplicate.
    /// <para>
    /// Pipeline: each asteroid rolls a designer <b>Size</b> in [MinSize, MaxSize], then
    /// HP = Size × HealthPerSize, gems = Size × GemsPerSize, and visual LocalTransform scale
    /// lerps from VisualScaleAtMinSize → VisualScaleAtMaxSize. Example: Size 50,
    /// HealthPerSize 3, GemsPerSize 0.5 → 150 HP and 25 gem capacity.
    /// Contact <see cref="Friction"/> controls how sticky rams/grinds feel against the rock.
    /// Cosmetic tumble uses <see cref="MinSpinSpeed"/>–<see cref="MaxSpinSpeed"/>
    /// (<see cref="Game.AsteroidSpinVisualProxy"/>) — presentation only, not sim physics.
    /// </para>
    /// </summary>
    [CreateAssetMenu(
        fileName = "AsteroidSettings",
        menuName = "Titan Orbit/Asteroid Settings",
        order = 54)]
    public class AsteroidSettings : ScriptableObject
    {
        [Header("Designer size range")]
        [Tooltip(
            "Lower bound for the rolled asteroid Size (not the mesh LocalTransform scale). " +
            "Size drives HP and gems via the ratios below. Default 1 matches the old gem-value floor.")]
        [Min(0.01f)]
        public float MinSize = 1f;

        [Tooltip(
            "Upper bound for the rolled asteroid Size. Default 70 matches the old max gem value. " +
            "Example: MaxSize 50 with HealthPerSize 3 → largest rocks have 150 HP.")]
        [Min(0.01f)]
        public float MaxSize = 70f;

        [Header("Hit points")]
        [Tooltip(
            "Max Health = Size × this. Size 50 × 3 = 150 HP. " +
            "Combat (bullets / ramming) drains Health; mining drains gems separately.")]
        [Min(0.01f)]
        public float HealthPerSize = 1f;

        [Header("Gem capacity")]
        [Tooltip(
            "Max gem value = Size × this. Size 50 × 0.5 = 25 gems. " +
            "Mining empties RemainingGems; destroy spill uses whatever is left.")]
        [Min(0f)]
        public float GemsPerSize = 1f;

        [Header("Visual scale (LocalTransform)")]
        [Tooltip(
            "Uniform mesh scale when Size equals MinSize (before per-axis jitter). " +
            "Legacy map gen used 0.35 for the smallest rocks.")]
        [Min(0.01f)]
        public float VisualScaleAtMinSize = 0.35f;

        [Tooltip(
            "Uniform mesh scale when Size equals MaxSize (before per-axis jitter). " +
            "Legacy map gen used 3.5 for the largest rocks.")]
        [Min(0.01f)]
        public float VisualScaleAtMaxSize = 3.5f;

        [Header("Contact friction (ram / grind)")]
        [Tooltip(
            "How sticky ship↔asteroid contact feels. 0 = ice (slides off easily), " +
            "1.5 = default grippy grind, 3+ = very sticky. " +
            "Applied to the asteroid PhysX material (Maximum combine so the ship's low hull " +
            "friction 0.05 does not cancel it) and to tangential slide after contacts / " +
            "cross-seam resolves. Raise this if the hull slips off while ramming.")]
        [Min(0f)]
        public float Friction = 1.5f;

        [Header("Visual spin (presentation)")]
        [Tooltip(
            "Lower bound for cosmetic tumble rate in degrees per second. " +
            "Each hybrid asteroid proxy rolls a random speed in [MinSpinSpeed, MaxSpinSpeed] " +
            "and a random 3D axis. Default 20 matches the old hardcoded floor. " +
            "Set both to 0 to freeze all rocks. Client visuals only — not physics / NetCode.")]
        [Min(0f)]
        public float MinSpinSpeed = 20f;

        [Tooltip(
            "Upper bound for cosmetic tumble rate in degrees per second. " +
            "Clamped to ≥ MinSpinSpeed. Default 50 matches the old hardcoded ceiling. " +
            "Client visuals only — not physics / NetCode.")]
        [Min(0f)]
        public float MaxSpinSpeed = 50f;

        /// <summary>Keeps ranges ordered and ratios non-negative after Inspector edits.</summary>
        public void ClampValues()
        {
            MinSize = Mathf.Max(0.01f, MinSize);
            MaxSize = Mathf.Max(MinSize, MaxSize);
            HealthPerSize = Mathf.Max(0.01f, HealthPerSize);
            GemsPerSize = Mathf.Max(0f, GemsPerSize);
            VisualScaleAtMinSize = Mathf.Max(0.01f, VisualScaleAtMinSize);
            VisualScaleAtMaxSize = Mathf.Max(0.01f, VisualScaleAtMaxSize);
            Friction = Mathf.Max(0f, Friction);
            MinSpinSpeed = Mathf.Max(0f, MinSpinSpeed);
            MaxSpinSpeed = Mathf.Max(MinSpinSpeed, MaxSpinSpeed);
        }

        void OnValidate() => ClampValues();

        /// <summary>Max Health from designer Size (floored to at least 1).</summary>
        public float ComputeMaxHealth(float size)
        {
            ClampValues();
            return Mathf.Max(1f, size * HealthPerSize);
        }

        /// <summary>Gem capacity from designer Size (floored to economy minimum).</summary>
        public float ComputeGemValue(float size)
        {
            ClampValues();
            float gems = size * GemsPerSize;
            return Mathf.Max(0.25f, gems);
        }

        /// <summary>
        /// Base uniform visual scale for this Size (no jitter). Lerps Min→Max visual by Size t.
        /// </summary>
        public float ComputeVisualScale(float size)
        {
            ClampValues();
            float span = Mathf.Max(0.001f, MaxSize - MinSize);
            float t = Mathf.Clamp01((size - MinSize) / span);
            return Mathf.Lerp(VisualScaleAtMinSize, VisualScaleAtMaxSize, t);
        }
    }
}
