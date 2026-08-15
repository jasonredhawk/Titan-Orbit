using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// [UNITY] Designer-tunable asteroid gem burst, asteroid respawn delay, and gem lifetime.
    /// Sole asset: <c>Assets/Resources/GemExplosionSettings.asset</c> (Create via Assets → Create →
    /// Titan Orbit → Gem Explosion Settings, or TitanOrbit → Create Gem Explosion Settings Asset).
    /// Loaded at play by <see cref="Game.GemExplosionSettingsLoader"/> via <c>Resources.Load</c>
    /// so Editor and player builds share one file — no Data/ duplicate.
    /// Defaults match the mature NGO-era feel (speed ~2.2, drag 0.5, tumble ±1.5,
    /// asteroid respawn 30s, gem lifetime 20s with 3s shrink).
    /// </summary>
    [CreateAssetMenu(
        fileName = "GemExplosionSettings",
        menuName = "Titan Orbit/Gem Explosion Settings",
        order = 52)]
    public class GemExplosionSettings : ScriptableObject
    {
        [Header("Gem count (asteroid destroy)")]
        [Tooltip("Minimum gems spawned when an asteroid is destroyed (clamped by remaining value).")]
        [Range(1, 10)]
        public int MinGemCount = 1;

        [Tooltip("Maximum gems spawned when an asteroid is destroyed (clamped by remaining value). Client visuals rent from GemVisualPool (prewarm 32) — keep near 1–3 for gameplay density; pool grows if dumps exceed idle stock.")]
        [Range(1, 10)]
        public int MaxGemCount = 3;

        [Tooltip("Max value per spawned gem entity. Default 88 = full chromatic piano (value 1..88, white + black keys). Larger leftover dumps spawn more gems so each piece stays on the audible ladder.")]
        [Min(1f)]
        public float MaxGemUnitValue = 88f;

        [Header("Burst launch (original NGO GemSpawner)")]
        [Tooltip("Base outward speed in world units/sec. Scene-tuned original was often 1.5; code default 2.2.")]
        [Min(0.1f)]
        public float AsteroidExplosionSpeed = 2.2f;

        [Tooltip("Spawn offset radius from asteroid center.")]
        [Min(0.1f)]
        public float AsteroidExplosionRadius = 1.4f;

        [Tooltip("Random multiplier min on explosion speed (original Random.Range 0.45–1).")]
        [Range(0.05f, 1f)]
        public float SpeedRandomMin = 0.45f;

        [Tooltip("Random multiplier max on explosion speed.")]
        [Range(0.05f, 2f)]
        public float SpeedRandomMax = 1f;

        [Header("Slowdown (original Gem.slowdownDrag → Rigidbody.linearDamping)")]
        [Tooltip("Linear damping per second — original Gem used 0.5 on Rigidbody.linearDamping.")]
        [Min(0f)]
        public float LinearDamping = 0.5f;

        [Tooltip("When speed falls below this, gem stops (original stopSpeedThreshold 0.05).")]
        [Min(0f)]
        public float StopSpeedThreshold = 0.05f;

        [Header("Tumble (original GemSpawner angularVelocity)")]
        [Tooltip("Random angular speed range per axis in rad/s (original ±1.5).")]
        [Min(0f)]
        public float AngularSpeedMax = 1.5f;

        [Tooltip("Angular damping per second (prefab angular damping was ~0.05 — keep light).")]
        [Min(0f)]
        public float AngularDamping = 0.05f;

        [Header("Mining nugget (non-burst)")]
        [Tooltip("Small outward kick when mining spawns a gem chunk (not asteroid destroy).")]
        [Min(0f)]
        public float MiningNudgeSpeedMin = 0.4f;

        [Min(0f)]
        public float MiningNudgeSpeedMax = 0.9f;

        [Header("Asteroid respawn (original AsteroidRespawnManager)")]
        [Tooltip("Seconds after destroy before a fresh asteroid spawns at the same pose (original 30).")]
        [Min(1f)]
        public float AsteroidRespawnDelaySeconds = 30f;

        [Header("Gem lifetime (original Gem.lifetimeSeconds / shrinkDuration)")]
        [Tooltip("Seconds before an uncollected gem despawns on the server (original 20).")]
        [Min(1f)]
        public float GemLifetimeSeconds = 20f;

        [Tooltip("Last seconds of life: visual scale shrinks to zero (original 3). Presentation only.")]
        [Min(0f)]
        public float GemShrinkDurationSeconds = 3f;

        [Header("Damage expulsion (ship cargo spill)")]
        [Tooltip(
            "Seconds after a ship spills gems from damage before THAT ship may tractor them. " +
            "Absorb-zone pickup uses a shorter grace (GemEconomyConstants.SelfPickupAbsorbBlockSeconds) " +
            "so exploded nuggets are not stuck uncollectable. Other ships can take them immediately. " +
            "0 = no penalty.")]
        [Min(0f)]
        public float SelfPickupBlockSeconds = 2f;

        /// <summary>Clamps min/max so Max ≥ Min and both stay in 1–10; keeps unit value ≥ 1.</summary>
        public void ClampCounts()
        {
            MinGemCount = Mathf.Clamp(MinGemCount, 1, 10);
            MaxGemCount = Mathf.Clamp(MaxGemCount, 1, 10);
            if (MaxGemCount < MinGemCount)
                MaxGemCount = MinGemCount;
            // [TITAN-ORBIT] Piano-width default is 88 (chromatic); never allow a zero/negative unit cap.
            MaxGemUnitValue = Mathf.Max(1f, MaxGemUnitValue);
            if (SpeedRandomMax < SpeedRandomMin)
                SpeedRandomMax = SpeedRandomMin;
            AsteroidRespawnDelaySeconds = Mathf.Max(1f, AsteroidRespawnDelaySeconds);
            GemLifetimeSeconds = Mathf.Max(1f, GemLifetimeSeconds);
            GemShrinkDurationSeconds = Mathf.Max(0f, GemShrinkDurationSeconds);
            if (GemShrinkDurationSeconds > GemLifetimeSeconds)
                GemShrinkDurationSeconds = GemLifetimeSeconds;
            SelfPickupBlockSeconds = Mathf.Max(0f, SelfPickupBlockSeconds);
        }

        void OnValidate() => ClampCounts();
    }
}
