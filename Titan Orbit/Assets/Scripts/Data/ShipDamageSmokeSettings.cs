using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Designer asset for client-only hull-damage smoke on ship GameObject proxies.
    /// Create via Assets → Create → Titan Orbit → Ship Damage Smoke Settings.
    /// Assign on each <see cref="ShipFamilyDefinition.damageSmokeSettings"/> so families can share
    /// one profile today and swap unique VFX later. Consumed by
    /// <see cref="TitanOrbit.Game.ShipDamageSmokeVisualApplier"/> — no server / Relay traffic.
    /// </summary>
    [CreateAssetMenu(
        fileName = "ShipDamageSmokeSettings",
        menuName = "Titan Orbit/Ship Damage Smoke Settings",
        order = 45)]
    public class ShipDamageSmokeSettings : ScriptableObject
    {
        /// <summary>
        /// [UNITY] Resources name for the shared default asset
        /// (<c>Assets/Resources/ShipDamageSmokeSettings.asset</c>).
        /// </summary>
        public const string DefaultResourcesName = "ShipDamageSmokeSettings";

        // -------------------------------------------------------------------------
        // Master toggle
        // -------------------------------------------------------------------------

        [Header("Enable")]
        [Tooltip("When off, ships of families using this asset spawn no damage smoke.")]
        public bool enabled = true;

        // -------------------------------------------------------------------------
        // Prefab / placement
        // -------------------------------------------------------------------------

        [Header("Prefab / Placement")]
        [Tooltip("Particle prefab Instantiated on the hull root (usually Resources/ShipDamageSmoke).")]
        public GameObject smokePrefab;

        [Tooltip("Local offset from the ship proxy root (aft + up clears the mesh).")]
        public Vector3 localOffset = new Vector3(0f, 0.25f, -0.35f);

        [Tooltip("Local euler for the emitter. ~-90° X makes Archanor smoke billow upward.")]
        public Vector3 localEuler = new Vector3(-90f, 0f, 0f);

        [Tooltip(
            "Target world scale of the emitter at full smoke intensity. " +
            "Code divides by the ship proxy lossy scale so ~0.15 hulls stay readable.")]
        [Min(0.01f)]
        public float maxWorldScale = 0.75f;

        // -------------------------------------------------------------------------
        // Health window (hull fraction 0 = empty, 1 = full)
        // -------------------------------------------------------------------------

        [Header("Health Window")]
        [Tooltip(
            "Hull health fraction (0–1) at or below which smoke begins. " +
            "0.5 = smoke only when the ship is at or below half HP.")]
        [Range(0.01f, 1f)]
        public float smokeStartsAtHealthFraction = 0.5f;

        [Tooltip(
            "Hull health fraction (0–1) at or below which smoke reaches full intensity. " +
            "Usually 0 (empty hull). Must be ≤ smokeStartsAtHealthFraction.")]
        [Range(0f, 1f)]
        public float smokeFullAtHealthFraction = 0f;

        // -------------------------------------------------------------------------
        // Emission (min at start threshold → max at full damage)
        // -------------------------------------------------------------------------

        [Header("Emission (min → max with damage)")]
        [Tooltip("Particles per second when smoke just starts (at smokeStartsAtHealthFraction).")]
        [Min(0f)]
        public float minEmissionRate = 4f;

        [Tooltip("Particles per second at full smoke (at smokeFullAtHealthFraction).")]
        [Min(0f)]
        public float maxEmissionRate = 18f;

        [Tooltip("Extra particles per world-unit traveled while moving (trail density).")]
        [Min(0f)]
        public float maxRateOverDistance = 1.2f;

        // -------------------------------------------------------------------------
        // Lifetime (seconds)
        // -------------------------------------------------------------------------

        [Header("Lifetime (min → max with damage)")]
        [Tooltip("Particle lifetime (seconds) when smoke just starts.")]
        [Min(0.05f)]
        public float minLifetime = 0.575f;

        [Tooltip("Particle lifetime (seconds) at full smoke — longer = longer trail.")]
        [Min(0.05f)]
        public float maxLifetime = 1.44f;

        // -------------------------------------------------------------------------
        // Particle size
        // -------------------------------------------------------------------------

        [Header("Particle Size (min → max with damage)")]
        [Tooltip("Start size when smoke just starts.")]
        [Min(0.01f)]
        public float minStartSize = 0.63f;

        [Tooltip("Start size at full smoke.")]
        [Min(0.01f)]
        public float maxStartSize = 1.55f;

        // -------------------------------------------------------------------------
        // Blend / motion
        // -------------------------------------------------------------------------

        [Header("Blend / Motion")]
        [Tooltip("How fast intensity eases toward the HP-based target (cosmetic only).")]
        [Min(0.01f)]
        public float intensityTransitionSpeed = 1.15f;

        [Tooltip("Hull speed (world units/s) at which rate-over-distance trail boost is full.")]
        [Min(0.01f)]
        public float trailSpeedReference = 10f;

        /// <summary>
        /// Loads the shared default from Resources (player builds). Editor can create one via the menu.
        /// </summary>
        public static ShipDamageSmokeSettings LoadDefault()
        {
            return Resources.Load<ShipDamageSmokeSettings>(DefaultResourcesName);
        }

        /// <summary>
        /// Maps current hull fraction to 0–1 smoke intensity using the health window.
        /// </summary>
        /// <param name="healthFraction">Current Health / MaxHealth, clamped 0–1.</param>
        /// <returns>0 when above the start threshold or disabled; 1 at/below full threshold.</returns>
        public float EvaluateIntensity(float healthFraction)
        {
            if (!enabled)
                return 0f;

            float health = Mathf.Clamp01(healthFraction);
            float startAt = Mathf.Clamp(smokeStartsAtHealthFraction, 0.01f, 1f);
            float fullAt = Mathf.Clamp(smokeFullAtHealthFraction, 0f, startAt);

            // Still healthy enough — no smoke.
            if (health > startAt)
                return 0f;

            // Remap health from startAt → fullAt into intensity 0 → 1.
            float span = Mathf.Max(0.001f, startAt - fullAt);
            return Mathf.Clamp01((startAt - health) / span);
        }

        /// <summary>
        /// Ensures prefab and numeric knobs are usable after a fresh/empty asset create.
        /// Does not override intentional designer values that are already set.
        /// </summary>
        public void EnsureRuntimeDefaults()
        {
            if (smokePrefab == null)
                smokePrefab = Resources.Load<GameObject>("ShipDamageSmoke");

            if (maxWorldScale < 0.01f)
                maxWorldScale = 0.75f;
            if (maxEmissionRate <= 0f)
                maxEmissionRate = 18f;
            if (minLifetime < 0.05f)
                minLifetime = 0.575f;
            if (maxLifetime < 0.05f)
                maxLifetime = 1.44f;
            if (minStartSize < 0.01f)
                minStartSize = 0.63f;
            if (maxStartSize < 0.01f)
                maxStartSize = 1.55f;
            if (intensityTransitionSpeed < 0.01f)
                intensityTransitionSpeed = 1.15f;
            if (trailSpeedReference < 0.01f)
                trailSpeedReference = 10f;

            // Keep the health window ordered (full ≤ start).
            if (smokeFullAtHealthFraction > smokeStartsAtHealthFraction)
                smokeFullAtHealthFraction = smokeStartsAtHealthFraction;
        }
    }
}
