using TitanOrbit.Data;
using Unity.Burst;
using Unity.Mathematics;

namespace TitanOrbit.Simulation
{
    /// <summary>
    /// Tracer size is fire power ÷ the Bullet VFX Bank reference (default 4).
    /// The bank global scale is applied when the tracer is drawn.
    /// Bullet type, bullet speed, and the old upgrade blend do not change it.
    /// </summary>
    [BurstCompile]
    public static class BulletVisualScale
    {
        /// <summary>Reference damage for "no upgrade" visual baseline (matches default cannon).</summary>
        public const float DefaultReferenceBulletDamage = 8f;

        /// <summary>
        /// Fallback ship-bullet tracer reference when <see cref="BulletVfxBank"/> is missing.
        /// The live value is <see cref="BulletVfxBank.BulletReferenceFirePower"/> on
        /// Resources/BulletVfxBank (default 4). Missiles, lasers, turrets, and ramming do not use it.
        /// </summary>
        public const float FixedBulletReferenceDamage = 4f;

        /// <summary>
        /// Reference the shot math divides by. Reads the Bullet VFX Bank field so the
        /// Inspector value is the one in play. Falls back to <see cref="FixedBulletReferenceDamage"/>.
        /// </summary>
        public static float ResolveFixedBulletReferenceDamage()
        {
            BulletVfxBank bank = BulletVfxBank.LoadDefault();
            if (bank == null)
                return FixedBulletReferenceDamage;
            return bank.BulletReferenceFirePower;
        }

        /// <summary>
        /// Kept for call-site compatibility only — speed no longer drives visual size.
        /// </summary>
        public const float DefaultReferenceBulletSpeed = 20f;

        /// <summary>
        /// Default bank upgrade growth when the VFX bank has not refreshed the cache yet (0.5 = half-step).
        /// </summary>
        public const float DefaultUpgradeVisualScaleMultiplier = 0.5f;

        /// <summary>
        /// [LEGACY] Same meaning as <see cref="DefaultUpgradeVisualScaleMultiplier"/>.
        /// Prefer the bank field / <see cref="ActiveUpgradeVisualScaleMultiplier"/>.
        /// </summary>
        public const float DamageVisualGrowthFactor = DefaultUpgradeVisualScaleMultiplier;

        // --- Burst-safe bank upgrade cache ---
        // [BURST] Ordinary mutable C# statics (e.g. `public static float X = …`) trigger BC1040
        // when read from [BurstCompile] methods — Linux / IL2CPP player builds fail AOT compile.
        // SharedStatic is the Burst-supported way to share a float written from managed bank load
        // (BulletSimulationSystem, BulletVfxDriver, EcsWorldVisualizer) and read here.
        private struct ActiveUpgradeVisualScaleKey { }

        private static readonly SharedStatic<float> s_ActiveUpgradeVisualScaleMultiplier =
            SharedStatic<float>.GetOrCreate<ActiveUpgradeVisualScaleKey>();

        /// <summary>
        /// True after static ctor or a managed setter has written the SharedStatic default/bank value.
        /// Managed-only — Burst paths read SharedStatic.Data directly.
        /// </summary>
        private static bool s_ActiveUpgradeInitialized;

        /// <summary>
        /// Live <b>bank-wide</b> upgrade growth from <c>BulletVfxBank.UpgradeVisualScaleMultiplier</c>.
        /// Written on bank load. Per-category upgrade is passed into
        /// <see cref="ComputeUpgradeScaleMultiplier"/> each shot (default 1 = 100%).
        /// </summary>
        public static float ActiveUpgradeVisualScaleMultiplier
        {
            get
            {
                // [STANDARD] Lazy default so Editor / first call before bank load still gets 0.5.
                EnsureActiveUpgradeInitialized();
                return s_ActiveUpgradeVisualScaleMultiplier.Data;
            }
            set
            {
                s_ActiveUpgradeVisualScaleMultiplier.Data = value;
                s_ActiveUpgradeInitialized = true;
            }
        }

        /// <summary>
        /// Type initializer — seeds SharedStatic to the designer default before any shot math runs.
        /// </summary>
        static BulletVisualScale()
        {
            s_ActiveUpgradeVisualScaleMultiplier.Data = DefaultUpgradeVisualScaleMultiplier;
            s_ActiveUpgradeInitialized = true;
        }

        /// <summary>
        /// Ensures the Burst SharedStatic holds the default when managed code reads the property
        /// before the static constructor has been observed (defensive; static ctor normally runs first).
        /// </summary>
        private static void EnsureActiveUpgradeInitialized()
        {
            if (s_ActiveUpgradeInitialized)
                return;
            s_ActiveUpgradeVisualScaleMultiplier.Data = DefaultUpgradeVisualScaleMultiplier;
            s_ActiveUpgradeInitialized = true;
        }

        /// <summary>
        /// Fire power ÷ reference fire power. The reference is the Bullet VFX Bank field (default 4).
        /// Speed, the bank upgrade knob, and category upgrade are not inputs.
        /// Bank global scale is applied later in <see cref="Entities.BulletVisualFactory"/>.
        /// </summary>
        /// <param name="bulletDamage">This shot’s fire power.</param>
        /// <param name="bulletSpeed">Unused — retained for signature stability.</param>
        /// <param name="referenceBulletDamage">Unleveled catalog fire power for this shot.</param>
        /// <param name="referenceBulletSpeed">Unused — retained for signature stability.</param>
        /// <param name="categoryUpgradeVisualScaleMultiplier">Unused — retained for signature stability.</param>
        [BurstCompile]
        public static float ComputeUpgradeScaleMultiplier(
            float bulletDamage,
            float bulletSpeed,
            float referenceBulletDamage = DefaultReferenceBulletDamage,
            float referenceBulletSpeed = DefaultReferenceBulletSpeed,
            float categoryUpgradeVisualScaleMultiplier = 1f)
        {
            _ = bulletSpeed;
            _ = referenceBulletSpeed;
            _ = categoryUpgradeVisualScaleMultiplier;
            return bulletDamage / referenceBulletDamage;
        }

        /// <summary>
        /// Most side-by-side shells one Titan shot will draw.
        /// A high-power sniper would otherwise rent dozens of tracers. Past this, the
        /// extras are dropped and each drawn shell keeps the uncapped share.
        /// </summary>
        public const int TitanSplitMaxVisuals = 12;

        /// <summary>
        /// How many visuals a Titan split shot wants, and how many to actually spawn.
        /// <paramref name="trueCount"/> is floor(fire power ÷ <paramref name="referenceFirePower"/>), at least 1.
        /// That reference is <see cref="BulletVfxBank.BulletReferenceFirePower"/>.
        /// <paramref name="spawnCount"/> is that count clamped to <see cref="TitanSplitMaxVisuals"/>.
        /// </summary>
        [BurstCompile]
        public static void ResolveTitanSplitCounts(
            float firepower,
            float referenceFirePower,
            out int trueCount,
            out int spawnCount)
        {
            trueCount = 1;
            float reference = math.max(0.01f, referenceFirePower);
            if (firepower >= reference)
            {
                int count = (int)math.floor((firepower + 0.001f) / reference);
                if (count > 1)
                    trueCount = count;
            }

            spawnCount = trueCount > TitanSplitMaxVisuals ? TitanSplitMaxVisuals : trueCount;
        }

        /// <summary>
        /// Center-to-center gap for one step in a Titan split row.
        /// Drawn shell size ÷ <see cref="BulletVfxBank.BulletReferenceFirePower"/>.
        /// The same reference sets the shell count, so one Inspector edit moves the whole row.
        /// </summary>
        [BurstCompile]
        public static float ComputeTitanSplitShellSpacing(float drawnShellScale, float referenceFirePower)
        {
            float reference = math.max(0.01f, referenceFirePower);
            return drawnShellScale / reference;
        }

        /// <summary>
        /// Root scale for one shell in a split shot. <paramref name="fullShotScale"/> is
        /// the single-bullet size (fire power ÷ the Bullet VFX Bank reference).
        /// Dividing by <paramref name="trueCount"/> gives each shell an equal share,
        /// so a 14-power pair is larger than a 10-power pair.
        /// </summary>
        [BurstCompile]
        public static float ComputeTitanSplitShellScale(float fullShotScale, int trueCount)
        {
            int n = trueCount < 1 ? 1 : trueCount;
            return fullShotScale / n;
        }

        /// <summary>
        /// Per-shot tracer scale before the bank global: fire power ÷ reference fire power.
        /// </summary>
        /// <param name="categoryUpgradeVisualScaleMultiplier">Unused. See <see cref="ComputeUpgradeScaleMultiplier"/>.</param>
        [BurstCompile]
        public static float ComputePerShotScale(
            float cannonBulletScale,
            float bulletDamage,
            float bulletSpeed,
            float referenceBulletDamage = DefaultReferenceBulletDamage,
            float referenceBulletSpeed = DefaultReferenceBulletSpeed,
            float categoryUpgradeVisualScaleMultiplier = 1f)
        {
            _ = cannonBulletScale;
            return ComputeUpgradeScaleMultiplier(
                bulletDamage,
                bulletSpeed,
                referenceBulletDamage,
                referenceBulletSpeed,
                categoryUpgradeVisualScaleMultiplier);
        }
    }
}
