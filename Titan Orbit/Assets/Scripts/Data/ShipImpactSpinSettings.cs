using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Designer-tunable glancing collision yaw and 0-HP wreck spin / leftover-firepower gem dribble.
    /// Sole asset: <c>Assets/Resources/ShipImpactSpinSettings.asset</c>.
    /// Burst jobs copy fields into <c>ShipImpactSpinTuning</c>.
    /// </summary>
    [CreateAssetMenu(
        fileName = "ShipImpactSpinSettings",
        menuName = "Titan Orbit/Ship Impact Spin Settings",
        order = 55)]
    public class ShipImpactSpinSettings : ScriptableObject
    {
        [Header("Glancing gate (living collisions)")]
        [Tooltip("Ignore contacts whose tangential slide is below this when closing is also low.")]
        [Min(0f)]
        public float MinGlancingSpeed = 1.25f;

        [Tooltip("Ignore contacts whose approach along the normal is below this when glancing is also low.")]
        [Min(0f)]
        public float MinClosingSpeed = 2f;

        [Header("Yaw strength")]
        [Tooltip("Overall collision spin. 0.25 ≈ a hard glancing wreck hit ~30–40 deg/s before random.")]
        [Min(0f)]
        public float CollisionYawScale = 0.22f;

        [Tooltip("Overall bullet / ram / mine / burn spin. Scales with damage and leftover HP.")]
        [Min(0f)]
        public float BulletYawScale = 0.18f;

        [Tooltip("Closing speed (u/s) that counts as a full-strength ram. Light taps scale down from this.")]
        [Min(0.1f)]
        public float CollisionClosingRef = 16f;

        [Tooltip("Damage that counts as a full-strength combat spin before health/random.")]
        [Min(0.1f)]
        public float DamageRef = 28f;

        [Tooltip("Head-on multiplier (0 = no spin on a straight hit, 1 = same as a glance).")]
        [Range(0f, 1f)]
        public float HeadOnSpinMul = 0.08f;

        [Tooltip("Spin multiplier at full hull. Lower = more stable when healthy.")]
        [Min(0f)]
        public float FullHealthSpinMul = 0.16f;

        [Tooltip("Spin multiplier at 0 HP. Higher = wreck hits whip the hull.")]
        [Min(0f)]
        public float ZeroHealthSpinMul = 1.85f;

        [Tooltip("Random magnitude spread. 0.4 = each hit is 60%–140% of the computed yaw.")]
        [Range(0f, 0.9f)]
        public float RandomSpread = 0.4f;

        [Tooltip("Hard cap on |yaw rate| (deg/sec).")]
        [Min(1f)]
        public float MaxYawRateDegPerSec = 150f;

        [Header("Decay / recover")]
        [Tooltip("Living-ship yaw half-life in seconds.")]
        [Min(0.05f)]
        public float DecayHalfLifeSeconds = 0.4f;

        [Tooltip("Wreck yaw half-life. Longer so 0-HP shots keep the hull tumbling.")]
        [Min(0.05f)]
        public float WreckDecayHalfLifeSeconds = 1.1f;

        [Tooltip("Aim recover: rotationSpeed *= 1 / (1 + |yaw| / this). Higher = easier to fight.")]
        [Min(1f)]
        public float LivingRecoverRefDegPerSec = 90f;

        [Header("Wreck")]
        [Tooltip("Unused for death. 0-HP ships stay alive until gems are also empty.")]
        [Min(0.1f)]
        public float WreckMaxSeconds = 3f;

        [Tooltip("Cargo spilled per leftover firepower (1 = 1:1). Capped by remaining hold.")]
        [Min(0f)]
        public float LeftoverFirepowerGemRate = 1f;

        public void ClampValues()
        {
            MinGlancingSpeed = Mathf.Max(0f, MinGlancingSpeed);
            MinClosingSpeed = Mathf.Max(0f, MinClosingSpeed);
            CollisionYawScale = Mathf.Max(0f, CollisionYawScale);
            BulletYawScale = Mathf.Max(0f, BulletYawScale);
            CollisionClosingRef = Mathf.Max(0.1f, CollisionClosingRef);
            DamageRef = Mathf.Max(0.1f, DamageRef);
            HeadOnSpinMul = Mathf.Clamp01(HeadOnSpinMul);
            FullHealthSpinMul = Mathf.Max(0f, FullHealthSpinMul);
            ZeroHealthSpinMul = Mathf.Max(0f, ZeroHealthSpinMul);
            RandomSpread = Mathf.Clamp(RandomSpread, 0f, 0.9f);
            MaxYawRateDegPerSec = Mathf.Max(1f, MaxYawRateDegPerSec);
            DecayHalfLifeSeconds = Mathf.Max(0.05f, DecayHalfLifeSeconds);
            WreckDecayHalfLifeSeconds = Mathf.Max(0.05f, WreckDecayHalfLifeSeconds);
            LivingRecoverRefDegPerSec = Mathf.Max(1f, LivingRecoverRefDegPerSec);
            WreckMaxSeconds = Mathf.Max(0.1f, WreckMaxSeconds);
            LeftoverFirepowerGemRate = Mathf.Max(0f, LeftoverFirepowerGemRate);
        }

        void OnValidate() => ClampValues();
    }
}
