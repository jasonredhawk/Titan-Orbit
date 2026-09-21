using TitanOrbit.Core;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// [UNITY] Designer knobs for the cosmetic ship-death breakup. Loaded from
    /// <c>Resources/ShipDeathDebrisSettings</c> when present; otherwise field defaults.
    /// Presentation only — no sim / PhysicsCollider debris. Client sphere bounces are visual.
    /// </summary>
    [CreateAssetMenu(
        fileName = "ShipDeathDebrisSettings",
        menuName = "Titan Orbit/Ship Death Debris Settings",
        order = 53)]
    public class ShipDeathDebrisSettings : ScriptableObject
    {
        public const string ResourcesLoadName = "ShipDeathDebrisSettings";

        [Header("Clusters")]
        [Tooltip("Fewest rigid chunks the hull can split into (clamped by part count).")]
        [Range(1, 12)]
        public int ClusterCountMin = 2;

        [Tooltip("Most rigid chunks the hull can split into. Nearby components stay in the same chunk.")]
        [Range(1, 12)]
        public int ClusterCountMax = 5;

        [Header("Launch")]
        [Tooltip("Base outward speed (world units/sec) for each chunk.")]
        [Min(0.1f)]
        public float RadialSpeed = 2.4f;

        [Tooltip("Per-death intensity min. Low rolls look like a lazy breakup.")]
        [Range(0.15f, 1f)]
        public float DeathIntensityMin = 0.5f;

        [Tooltip("Per-death intensity max. High rolls throw chunks harder and faster.")]
        [Range(0.5f, 2.5f)]
        public float DeathIntensityMax = 1.55f;

        [Tooltip("Random multiplier min on each chunk's outward speed.")]
        [Range(0.15f, 1f)]
        public float RadialSpeedRandomMin = 0.35f;

        [Tooltip("Random multiplier max on each chunk's outward speed.")]
        [Range(0.5f, 2.5f)]
        public float RadialSpeedRandomMax = 1.75f;

        [Tooltip("How much a chunk's travel can yaw off the crash heading (keep low so a chase-kill still reads as forward).")]
        [Range(0f, 1.5f)]
        public float DirectionChaos = 0.28f;

        [Tooltip("Extra speed along the crash heading (ship flight + kill hit) at full packed power.")]
        [Min(0f)]
        public float CrashSpeed = 3.8f;

        [Tooltip("How strongly the last-hit direction aims the crash versus the ship's own flight.")]
        [Range(0f, 2.5f)]
        public float KillAimWeight = 1.25f;

        [Tooltip("How much chunks still peel off the crash heading (0 = all ride the hit, 1 = old radial burst).")]
        [Range(0f, 1.5f)]
        public float BreakSpread = 0.42f;

        [Tooltip("How much packed kill-impulse adds on the contact-side chunks at full power.")]
        [Min(0f)]
        public float ImpulseSpeed = 3.6f;

        [Tooltip("Blast falloff radius as a multiple of hull radius (impact-side chunks get more push).")]
        [Min(0.25f)]
        public float BlastRadiusHullMul = 1.6f;

        [Header("Spin")]
        [Tooltip("Max chunk tumble (degrees/sec) on the dominant axis at full spin mul.")]
        [Min(0f)]
        public float MaxSpinDegreesPerSecond = 210f;

        [Tooltip("Random spin multiplier min. Some chunks almost slide.")]
        [Range(0.05f, 1f)]
        public float ClusterSpinRandomMin = 0.15f;

        [Tooltip("Random spin multiplier max. Some chunks tumble hard.")]
        [Range(0.5f, 2.5f)]
        public float ClusterSpinRandomMax = 1.35f;

        [Header("Slowdown")]
        [Tooltip("Linear damping per second (0 = coast). 0.72 is ~20% less than the old 0.9 so chunks travel farther at the same launch speed.")]
        [Range(0f, 4f)]
        public float LinearDrag = 0.72f;

        [Tooltip("Angular damping per second.")]
        [Range(0f, 4f)]
        public float AngularDrag = 0.35f;

        [Header("Client visual collision")]
        [Tooltip("How hard a chunk bounces off a ship or asteroid (0 = slide, 1 = reverse). Client math only — no server / PhysicsCollider.")]
        [Range(0f, 1f)]
        public float VisualBounce = 0.42f;

        [Tooltip("Extra tumble (deg/s per unit of graze speed) when a chunk glances a hull.")]
        [Min(0f)]
        public float VisualHitSpin = 14f;

        [Header("VFX")]
        [Tooltip("Fire/V2 RedFireImpactV2 — Team A. Initial burst plus repeating seam flashes.")]
        public GameObject ExplosionVfxRed;

        [Tooltip("Fire/V2 BlueFireImpactV2 — Team B.")]
        public GameObject ExplosionVfxBlue;

        [Tooltip("Fire/V2 GreenFireImpactV2 — Team C.")]
        public GameObject ExplosionVfxGreen;

        [Tooltip("Fire/V2 YellowFireImpactV2 — Team D.")]
        public GameObject ExplosionVfxYellow;

        [Tooltip("Fire/V2 PurpleFireImpactV2 — Team E.")]
        public GameObject ExplosionVfxPurple;

        [Tooltip("World scale of the initial Fire/V2 burst at a 1× hull.")]
        [Min(0.05f)]
        public float BurstScale = 2.2f;

        [Tooltip("Extra burst scale at full packed power.")]
        [Min(0f)]
        public float BurstScaleFromPower = 1.4f;

        [Tooltip("Max repeating Fire/V2 flashes at torn contacts. Each is a one-shot retrigger, not a looping particle.")]
        [Range(0, 12)]
        public int MaxSeamBursts = 6;

        [Tooltip("Smallest component fire (10–20% of the old seam flash).")]
        [Min(0.02f)]
        public float SeamScaleMin = 0.08f;

        [Tooltip("Largest component fire (about 20% of the old seam flash).")]
        [Min(0.02f)]
        public float SeamScaleMax = 0.18f;

        [Tooltip("Earliest first component fire after the big death burst.")]
        [Min(0f)]
        public float SeamFirstDelayMin = 0.45f;

        [Tooltip("Latest first component fire — later emitters wait for a second-wave beat.")]
        [Min(0f)]
        public float SeamFirstDelayMax = 1.85f;

        [Tooltip("Shortest time between repeats on one contact.")]
        [Min(0.2f)]
        public float SeamRepeatMin = 1.15f;

        [Tooltip("Longest time between repeats on one contact.")]
        [Min(0.2f)]
        public float SeamRepeatMax = 2.55f;

        [Tooltip("Each repeat waits this much longer (1 = steady, 1.25 = dying-down epic).")]
        [Range(1f, 2f)]
        public float SeamRepeatGrow = 1.28f;

        [Tooltip("How long each Fire/V2 one-shot stays rented. Keep short so repeats do not stack.")]
        [Min(0.2f)]
        public float SeamBurstDuration = 0.55f;

        /// <summary>Resources asset, or a runtime instance with Inspector defaults.</summary>
        public static ShipDeathDebrisSettings LoadOrDefault()
        {
            var loaded = Resources.Load<ShipDeathDebrisSettings>(ResourcesLoadName);
            return loaded != null ? loaded : CreateInstance<ShipDeathDebrisSettings>();
        }

        /// <summary>Team Fire/V2 impact, or null when that slot is empty.</summary>
        public GameObject GetExplosionVfx(TeamId team)
        {
            switch (team)
            {
                case TeamId.TeamA: return ExplosionVfxRed;
                case TeamId.TeamB: return ExplosionVfxBlue;
                case TeamId.TeamC: return ExplosionVfxGreen;
                case TeamId.TeamD: return ExplosionVfxYellow;
                case TeamId.TeamE: return ExplosionVfxPurple;
                default: return ExplosionVfxRed;
            }
        }
    }
}
