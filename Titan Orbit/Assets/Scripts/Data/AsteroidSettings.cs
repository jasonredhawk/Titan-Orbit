using TitanOrbit.Core;
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
    /// Pipeline: each asteroid rolls a designer <b>Size</b> in [MinSize, MaxSize] with
    /// <see cref="SizeSmallBias"/> (1 = even mix, 2+ = more small rocks), then
    /// HP = Size × HealthPerSize, gems = Size × GemsPerSize, and visual LocalTransform scale
    /// lerps from VisualScaleAtMinSize → VisualScaleAtMaxSize. Example: Size 50,
    /// HealthPerSize 3, GemsPerSize 0.5 → 150 HP and 25 gem capacity.
    /// Contact <see cref="Friction"/> controls how sticky rams/grinds feel against the rock.
    /// <see cref="GrindPulseIntervalSeconds"/> is how often a thrusting hull chips the rock
    /// (0.25 = 4 Hz; each pulse spawns one gem worth that pulse's ship damage).
    /// <see cref="BounceRestitution"/> is the wall coefficient for ship↔asteroid rebound
    /// (rocks stay put; incoming speed reflects along the contact normal).
    /// <see cref="CollisionMassPerSize"/> is kept on the asset but does not drive rebound.
    /// Cosmetic tumble uses <see cref="MinSpinSpeed"/>–<see cref="MaxSpinSpeed"/>
    /// (<see cref="Game.AsteroidSpinVisualProxy"/>) — presentation only, not sim physics.
    /// Death fireballs use Fire/V1: <see cref="deathExplosionVfxNeutral"/> plus team-colored
    /// FireImpacts (client only). Blast pitch still follows visual size.
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

        [Tooltip(
            "How strongly map gen prefers smaller rocks. Size = lerp(Min, Max, pow(u, this)). " +
            "1 = even mix (old uniform roll). 2 = many small, some mid, rare giants. " +
            "4+ = almost all small. Old assets that deserialize as 0 become 2.")]
        [Range(1f, 6f)]
        public float SizeSmallBias = 2f;

        [Header("Hit points")]
        [Tooltip(
            "Health Cap = Size × this. Size 50 × 3 = 150 HP. " +
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

        /// <summary>
        /// Seconds between grind damage pulses while a ship thrusts into this rock (0.25 = 4 Hz).
        /// Damage per pulse multiplies by this interval, then one gem spawns with that pulse's
        /// expelled cargo — four pulses per second means four gems per second, no banking.
        /// </summary>
        [Header("Grind pacing")]
        [Tooltip(
            "Seconds between grind damage pulses while thrusting into a rock. " +
            "0.25 = 4 pulses per second (4 gems per second). Damage per pulse multiplies by " +
            "this interval, then one gem spawns with that pulse's expelled cargo. " +
            "Values below 0.05 fall back to 0.25 (protects old assets that serialized as 0).")]
        public float GrindPulseIntervalSeconds = 0.25f;

        [Header("Collision bounce")]
        [Tooltip(
            "Unused by live bounce (rocks are immovable walls). Kept so existing AsteroidSettings " +
            "assets do not churn. Virtual mass = Size × this if a later system needs it.")]
        [Min(0.01f)]
        public float CollisionMassPerSize = 1f;

        [Tooltip(
            "One bounce coefficient for ships, asteroids, planets, moons, and moon shields " +
            "(0 = inelastic stick along the normal, 1 = perfectly elastic). PhysX materials " +
            "are restitution 0 so ShipCollisionBounceSystem owns rebound.")]
        [Range(0f, 1f)]
        public float BounceRestitution = 0.55f;

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

        [Header("Death explosion (presentation)")]
        [Tooltip(
            "Fire/V1 ModularFireImpact — unclaimed rocks (no territory tint). " +
            "Client visuals only — scale and blast pitch follow the asteroid's visual size.")]
        public GameObject deathExplosionVfxNeutral;

        [Tooltip("Fire/V1 RedFireImpact — Team A (red) territory rocks.")]
        public GameObject deathExplosionVfxRed;

        [Tooltip("Fire/V1 BlueFireImpact — Team B (blue) territory rocks.")]
        public GameObject deathExplosionVfxBlue;

        [Tooltip("Fire/V1 GreenFireImpact — Team C (green) territory rocks.")]
        public GameObject deathExplosionVfxGreen;

        [Tooltip("Fire/V1 YellowFireImpact — Team D (orange) territory rocks. V1 has no orange burst.")]
        public GameObject deathExplosionVfxYellow;

        [Tooltip("Fire/V1 PurpleFireImpact — Team E (purple) territory rocks.")]
        public GameObject deathExplosionVfxPurple;

        [Tooltip(
            "Multiplies LocalTransform scale onto the death burst so the fireball wraps the rock. " +
            "1.15 is slightly larger than the mesh. Values below 0.05 fall back to 1.15.")]
        public float deathExplosionScaleMultiplier = 1.15f;

        [Tooltip("AudioSource pitch on the smallest rocks (VisualScaleAtMinSize). Higher = thinner blast.")]
        [Min(0.01f)]
        public float deathExplosionPitchAtMinSize = 1.55f;

        [Tooltip("AudioSource pitch on the largest rocks (VisualScaleAtMaxSize). Lower = deeper boom.")]
        [Min(0.01f)]
        public float deathExplosionPitchAtMaxSize = 0.5f;

        /// <summary>Keeps ranges ordered and ratios non-negative after Inspector edits.</summary>
        public void ClampValues()
        {
            MinSize = Mathf.Max(0.01f, MinSize);
            MaxSize = Mathf.Max(MinSize, MaxSize);
            // Old AsteroidSettings.asset files lack this field → Unity deserializes 0 (uniform).
            // Treat unset as 2 so existing maps pick up the small-heavy default.
            if (SizeSmallBias < 1f)
                SizeSmallBias = 2f;
            SizeSmallBias = Mathf.Clamp(SizeSmallBias, 1f, 6f);
            HealthPerSize = Mathf.Max(0.01f, HealthPerSize);
            GemsPerSize = Mathf.Max(0f, GemsPerSize);
            VisualScaleAtMinSize = Mathf.Max(0.01f, VisualScaleAtMinSize);
            VisualScaleAtMaxSize = Mathf.Max(0.01f, VisualScaleAtMaxSize);
            Friction = Mathf.Max(0f, Friction);
            // Old AsteroidSettings.asset files lack this field → Unity deserializes 0 and would
            // zero grind DPS. 0.25 = 4 Hz, one gem per pulse.
            if (GrindPulseIntervalSeconds < 0.05f)
                GrindPulseIntervalSeconds = 0.25f;
            CollisionMassPerSize = Mathf.Max(0.01f, CollisionMassPerSize);
            BounceRestitution = Mathf.Clamp01(BounceRestitution);
            MinSpinSpeed = Mathf.Max(0f, MinSpinSpeed);
            MaxSpinSpeed = Mathf.Max(MinSpinSpeed, MaxSpinSpeed);
            if (deathExplosionScaleMultiplier < 0.05f)
                deathExplosionScaleMultiplier = 1.15f;
            // Old assets deserialize new pitch fields as 0.
            if (deathExplosionPitchAtMinSize < 0.05f)
                deathExplosionPitchAtMinSize = 1.55f;
            if (deathExplosionPitchAtMaxSize < 0.05f)
                deathExplosionPitchAtMaxSize = 0.5f;
        }

        /// <summary>
        /// Virtual collision mass from designer Size. Live bounce does not use this —
        /// rocks are immovable walls. Kept for assets and retired seam helpers.
        /// </summary>
        public float ComputeCollisionMass(float size)
        {
            ClampValues();
            return Mathf.Max(0.5f, size * CollisionMassPerSize);
        }

        void OnValidate() => ClampValues();

        /// <summary>Health Cap from designer Size (floored to at least 1).</summary>
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

        /// <summary>
        /// Fire/V1 burst for this rock tint. <see cref="TeamId.None"/> is ModularFireImpact.
        /// Missing team slots fall back to the neutral prefab.
        /// </summary>
        public GameObject GetDeathExplosionVfx(TeamId team)
        {
            GameObject picked;
            switch (team)
            {
                case TeamId.TeamA:
                    picked = deathExplosionVfxRed;
                    break;
                case TeamId.TeamB:
                    picked = deathExplosionVfxBlue;
                    break;
                case TeamId.TeamC:
                    picked = deathExplosionVfxGreen;
                    break;
                case TeamId.TeamD:
                    picked = deathExplosionVfxYellow;
                    break;
                case TeamId.TeamE:
                    picked = deathExplosionVfxPurple;
                    break;
                default:
                    picked = deathExplosionVfxNeutral;
                    break;
            }

            if (picked != null)
                return picked;
            return deathExplosionVfxNeutral;
        }

        /// <summary>
        /// Death-burst world scale from the rock's uniform LocalTransform scale.
        /// </summary>
        public float ComputeDeathExplosionScale(float visualScale)
        {
            ClampValues();
            return Mathf.Max(0.05f, visualScale * deathExplosionScaleMultiplier);
        }

        /// <summary>
        /// Death-blast <see cref="AudioSource.pitch"/> — small rocks stay high, large rocks drop.
        /// </summary>
        public float ComputeDeathExplosionPitch(float visualScale)
        {
            ClampValues();
            float minScale = VisualScaleAtMinSize;
            float maxScale = Mathf.Max(minScale + 0.001f, VisualScaleAtMaxSize);
            float t = Mathf.InverseLerp(minScale, maxScale, visualScale);
            return Mathf.Lerp(deathExplosionPitchAtMinSize, deathExplosionPitchAtMaxSize, t);
        }
    }
}
