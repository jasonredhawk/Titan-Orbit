using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// [UNITY] Designer knobs for the cosmetic ship-death breakup. Loaded from
    /// <c>Resources/ShipDeathDebrisSettings</c> when present; otherwise field defaults.
    /// Presentation only — no sim / collider debris.
    /// </summary>
    [CreateAssetMenu(
        fileName = "ShipDeathDebrisSettings",
        menuName = "Titan Orbit/Ship Death Debris Settings",
        order = 53)]
    public class ShipDeathDebrisSettings : ScriptableObject
    {
        public const string ResourcesLoadName = "ShipDeathDebrisSettings";

        [Header("Launch")]
        [Tooltip("Base outward speed (world units/sec) for each prefab component.")]
        [Min(0.1f)]
        public float RadialSpeed = 2.6f;

        [Tooltip("Random multiplier min on radial speed.")]
        [Range(0.2f, 1f)]
        public float RadialSpeedRandomMin = 0.65f;

        [Tooltip("Random multiplier max on radial speed.")]
        [Range(0.5f, 2.5f)]
        public float RadialSpeedRandomMax = 1.15f;

        [Tooltip("How much packed kill-impulse adds to radial speed at full power.")]
        [Min(0f)]
        public float ImpulseSpeed = 3.2f;

        [Tooltip("Blast falloff radius as a multiple of hull radius (impact-side parts get more push).")]
        [Min(0.25f)]
        public float BlastRadiusHullMul = 1.6f;

        [Header("Spin")]
        [Tooltip("Max random angular speed per axis (degrees/sec).")]
        [Min(0f)]
        public float MaxSpinDegreesPerSecond = 150f;

        [Header("Slowdown")]
        [Tooltip("Linear damping per second (0 = coast).")]
        [Range(0f, 4f)]
        public float LinearDrag = 0.9f;

        [Tooltip("Angular damping per second.")]
        [Range(0f, 4f)]
        public float AngularDrag = 0.35f;

        [Header("VFX")]
        [Tooltip("World scale of the Fireballs burst at a 1× hull.")]
        [Min(0.05f)]
        public float BurstScale = 2.2f;

        [Tooltip("Extra burst scale at full packed power.")]
        [Min(0f)]
        public float BurstScaleFromPower = 1.4f;

        [Tooltip("Max looping burn attachments (largest pieces). MEGA hulls stay cheap.")]
        [Range(0, 16)]
        public int MaxBurnAttachments = 8;

        [Tooltip("World scale of each burn loop on a 1× piece.")]
        [Min(0.05f)]
        public float BurnScale = 0.85f;

        [Tooltip("Earliest a piece can catch fire after the hull breaks (seconds).")]
        [Min(0f)]
        public float BurnStartDelayMin = 0.2f;

        [Tooltip("Latest a piece can catch fire after the hull breaks (seconds).")]
        [Min(0f)]
        public float BurnStartDelayMax = 4.5f;

        /// <summary>Resources asset, or a runtime instance with Inspector defaults.</summary>
        public static ShipDeathDebrisSettings LoadOrDefault()
        {
            var loaded = Resources.Load<ShipDeathDebrisSettings>(ResourcesLoadName);
            return loaded != null ? loaded : CreateInstance<ShipDeathDebrisSettings>();
        }
    }
}
