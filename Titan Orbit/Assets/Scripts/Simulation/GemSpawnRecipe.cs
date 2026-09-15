using TitanOrbit.Data;
using Unity.Mathematics;
using Random = Unity.Mathematics.Random;

namespace TitanOrbit.Simulation
{
    /// <summary>
    /// Compact deterministic gem spawn recipe. Server Instantiates a local entity and broadcasts
    /// this payload; clients hydrate the same crystal from the same RNG (no gem ghosts).
    /// </summary>
    public struct GemSpawnRecipe
    {
        /// <summary>Burst launch (asteroid destroy, combat/ram spill). Soft mining nudge when false.</summary>
        public const byte FlagBurst = 1;

        /// <summary>Yellow territory-bonus tint.</summary>
        public const byte FlagBonus = 2;

        public float3 Position;
        public float Value;
        public uint Salt;
        public float SpawnServerTime;
        public byte BurstIndex;
        public byte Flags;
        public float BurstIntensity;
        public int ExcludePickupNetworkId;
        public float ExcludePickupUntilServerTime;
        public float3 LaunchDir;
        public float3 AddVelocity;
        public float LaunchSpeedMul;

        public bool Burst => (Flags & FlagBurst) != 0;
        public bool IsBonusGem => (Flags & FlagBonus) != 0;

        public static byte PackFlags(bool burst, bool isBonusGem)
        {
            byte flags = 0;
            if (burst)
                flags |= FlagBurst;
            if (isBonusGem)
                flags |= FlagBonus;
            return flags;
        }
    }

    /// <summary>
    /// Pose / kinematics resolved from a <see cref="GemSpawnRecipe"/> + explosion settings.
    /// Identical on server and client for the same recipe.
    /// </summary>
    public struct GemSpawnResolved
    {
        public int SpawnId;
        public float3 Position;
        public float Scale;
        public float3 Velocity;
        public float3 AngularVelocity;
        public float Value;
        public bool IsBonusGem;
        public byte BurstIndex;
        public float SpawnServerTime;
        public int ExcludePickupNetworkId;
        public float ExcludePickupUntilServerTime;
    }

    /// <summary>
    /// Shared gem spawn RNG. Must stay identical on server Instantiates and client hydrate.
    /// </summary>
    public static class GemSpawnMath
    {
        /// <summary>
        /// Stable session id from recipe fields (not a monotonic counter). 0 is reserved.
        /// </summary>
        public static int ComputeSpawnId(in GemSpawnRecipe recipe)
        {
            uint h = math.hash(new uint4(
                math.hash(recipe.Position),
                recipe.Salt,
                math.asuint(recipe.SpawnServerTime),
                (uint)recipe.BurstIndex
                ^ ((uint)recipe.Flags << 8)
                ^ math.asuint(recipe.Value)
                ^ (uint)recipe.ExcludePickupNetworkId * 73856093u));
            int id = (int)h;
            return id == 0 ? 1 : id;
        }

        /// <summary>
        /// Builds a recipe from the historical <c>GemSpawning.Spawn</c> argument list.
        /// </summary>
        public static GemSpawnRecipe Create(
            float3 position,
            float value,
            uint salt,
            bool burst,
            float spawnServerTime,
            byte burstIndex = 0,
            bool isBonusGem = false,
            float burstIntensity = 1f,
            int excludePickupNetworkId = 0,
            float excludePickupUntilServerTime = 0f,
            float3 launchDir = default,
            float3 addVelocity = default,
            float launchSpeedMul = 1f)
        {
            return new GemSpawnRecipe
            {
                Position = position,
                Value = value,
                Salt = salt,
                SpawnServerTime = spawnServerTime,
                BurstIndex = burstIndex,
                Flags = GemSpawnRecipe.PackFlags(burst, isBonusGem),
                BurstIntensity = burstIntensity,
                ExcludePickupNetworkId = excludePickupNetworkId,
                ExcludePickupUntilServerTime = excludePickupUntilServerTime,
                LaunchDir = launchDir,
                AddVelocity = addVelocity,
                LaunchSpeedMul = launchSpeedMul,
            };
        }

        /// <summary>
        /// Rolls offset, scale, and launch from the recipe. False when value is not worth a gem.
        /// </summary>
        public static bool TryResolve(
            in GemSpawnRecipe recipe,
            GemExplosionSettings settings,
            out GemSpawnResolved resolved)
        {
            resolved = default;
            if (recipe.Value <= 0f)
                return false;

            settings ??= GemExplosionSettingsCache.ResolveOrDefault();
            var rng = Random.CreateFromIndex(math.hash(recipe.Position) + recipe.Salt + 17u);

            float3 planarLaunch = new float3(recipe.LaunchDir.x, 0f, recipe.LaunchDir.z);
            bool useForward = math.lengthsq(planarLaunch) > 0.01f;
            float3 spawnDir = useForward
                ? math.normalize(planarLaunch)
                : GemExplosionMath.RandomUnitXZ(ref rng);
            if (math.lengthsq(spawnDir) < 0.01f)
                spawnDir = new float3(0f, 0f, 1f);

            float radius = recipe.Burst ? settings.AsteroidExplosionRadius : 0.8f;
            float along = useForward ? 0f : radius * rng.NextFloat(0.3f, 1f);
            float3 offset = spawnDir * along;
            float scale = math.clamp(math.sqrt(recipe.Value) * 0.2f, 0.2f, 0.5f);
            float speedMul = math.max(0.01f, recipe.LaunchSpeedMul <= 0f ? 1f : recipe.LaunchSpeedMul);

            float3 vel;
            float3 ang;
            if (recipe.Burst)
            {
                vel = GemExplosionMath.BurstVelocity(
                    spawnDir,
                    settings.AsteroidExplosionSpeed,
                    settings.SpeedRandomMin,
                    settings.SpeedRandomMax,
                    recipe.BurstIntensity,
                    ref rng);
                vel *= speedMul;
                vel += recipe.AddVelocity;
                vel.y = 0f;
                ang = GemExplosionMath.BurstAngularVelocity(settings.AngularSpeedMax, ref rng);
            }
            else
            {
                float speed = rng.NextFloat(settings.MiningNudgeSpeedMin, settings.MiningNudgeSpeedMax);
                ang = GemExplosionMath.BurstAngularVelocity(settings.AngularSpeedMax * 0.35f, ref rng);
                vel = spawnDir * (speed * speedMul) + recipe.AddVelocity;
                vel.y = 0f;
            }

            resolved = new GemSpawnResolved
            {
                SpawnId = ComputeSpawnId(recipe),
                Position = recipe.Position + offset,
                Scale = scale,
                Velocity = vel,
                AngularVelocity = ang,
                Value = recipe.Value,
                IsBonusGem = recipe.IsBonusGem,
                BurstIndex = recipe.BurstIndex,
                SpawnServerTime = recipe.SpawnServerTime,
                ExcludePickupNetworkId = recipe.ExcludePickupNetworkId,
                ExcludePickupUntilServerTime = recipe.ExcludePickupUntilServerTime,
            };
            return true;
        }
    }

    /// <summary>
    /// Expands an asteroid-destroy leftover into per-gem recipes (chord split + per-index salt).
    /// </summary>
    public static class GemBurstExpansion
    {
        static readonly float[] ChordScratch = new float[GemExplosionMath.AbsoluteMaxGemCount];

        /// <summary>
        /// Writes up to <see cref="GemExplosionMath.AbsoluteMaxGemCount"/> recipes into
        /// <paramref name="dst"/>. Returns how many were written.
        /// </summary>
        public static int FillRecipes(
            float3 origin,
            float remaining,
            uint seed,
            float spawnServerTime,
            bool isBonusGem,
            GemExplosionSettings settings,
            GemSpawnRecipe[] dst)
        {
            if (dst == null || remaining < 0.25f)
                return 0;

            settings ??= GemExplosionSettingsCache.ResolveOrDefault();
            var rng = Random.CreateFromIndex(seed);
            int count = GemExplosionMath.ResolveGemCountForUnitCap(
                remaining,
                settings.MinGemCount,
                settings.MaxGemCount,
                settings.MaxGemUnitValue,
                ref rng);

            GemChordValues.Fill(remaining, count, settings.MaxGemUnitValue, ChordScratch);

            int written = 0;
            for (int i = 0; i < count && written < dst.Length; i++)
            {
                float value = ChordScratch[i];
                if (value < 0.25f)
                    continue;

                dst[written++] = GemSpawnMath.Create(
                    origin,
                    value,
                    seed + (uint)(i + 1) * 97u,
                    burst: true,
                    spawnServerTime,
                    burstIndex: (byte)i,
                    isBonusGem: isBonusGem);
            }

            return written;
        }
    }
}
