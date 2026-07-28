using TitanOrbit.Generation;
using Unity.Mathematics;

namespace TitanOrbit.Simulation
{
    /// <summary>
    /// Shared asteroid gem-burst math for server spawn and client immediate presentation.
    /// Tunables come from <see cref="Data.GemExplosionSettings"/> (Editor ScriptableObject).
    /// Defaults match mature NGO <c>GemSpawner</c> / <c>Gem</c> (speed 2.2, damping 0.5, tumble ±1.5).
    /// <see cref="ResolveGemCountForUnitCap"/> keeps each spawned gem ≤ MaxGemUnitValue (88)
    /// so pickup SFX stays on the chromatic piano ladder. Multi-gem value splits use
    /// <see cref="GemChordValues"/> (C-major dyad / triad / maj7) instead of equal copies.
    /// </summary>
    public static class GemExplosionMath
    {
        public const int AbsoluteMinGemCount = 1;
        public const int AbsoluteMaxGemCount = 10;
        public const float DefaultExplosionSpeed = 2.2f;
        public const float DefaultExplosionRadius = 1.4f;
        public const float DefaultSpeedRandomMin = 0.45f;
        public const float DefaultSpeedRandomMax = 1f;
        public const float DefaultLinearDamping = 0.5f;
        public const float DefaultStopSpeedThreshold = 0.05f;
        public const float DefaultAngularSpeedMax = 1.5f;
        public const float DefaultAngularDamping = 0.05f;

        /// <summary>
        /// How many gem entities to spawn for leftover asteroid value.
        /// Count is random in [minCount, maxCount], also capped by floor(remaining) so tiny asteroids
        /// cannot spawn more gems than whole value units (same spirit as original 1–3 logic).
        /// </summary>
        public static int ResolveGemCount(float remaining, int minCount, int maxCount, ref Random rng)
        {
            int minC = math.clamp(minCount, AbsoluteMinGemCount, AbsoluteMaxGemCount);
            int maxC = math.clamp(maxCount, AbsoluteMinGemCount, AbsoluteMaxGemCount);
            if (maxC < minC)
                maxC = minC;

            int maxByValue = math.max(1, math.min(maxC, (int)math.floor(remaining)));
            int lo = math.min(minC, maxByValue);
            int hi = maxByValue;
            if (lo >= hi)
                return hi;

            // NextInt is [inclusive, exclusive)
            return rng.NextInt(lo, hi + 1);
        }

        /// <summary>
        /// Like <see cref="ResolveGemCount"/>, but raises the count when needed so each equal-split
        /// gem stays at or below <paramref name="maxUnitValue"/> (the musical piano-width cap).
        /// <para>
        /// [TITAN-ORBIT] Keeps world pickups on the 88-key chromatic SFX ladder — a 200-value dump
        /// becomes several ≤88 gems instead of one inaudibly low note.
        /// </para>
        /// </summary>
        /// <param name="remaining">Total gem value to split across entities.</param>
        /// <param name="minCount">Designer minimum burst count.</param>
        /// <param name="maxCount">Designer maximum burst count (before unit-cap raise).</param>
        /// <param name="maxUnitValue">Max value per gem entity (default 88 = full piano).</param>
        /// <param name="rng">Deterministic spawn RNG.</param>
        /// <returns>Gem entity count in [1, AbsoluteMaxGemCount].</returns>
        public static int ResolveGemCountForUnitCap(
            float remaining,
            int minCount,
            int maxCount,
            float maxUnitValue,
            ref Random rng)
        {
            // --- Random aesthetic count (same as before) ---
            int count = ResolveGemCount(remaining, minCount, maxCount, ref rng);

            // --- Raise count so equal split respects the unit cap ---
            // [TITAN-ORBIT] maxUnitValue matches GemMusicalPitch.PianoKeyCount (88) by default.
            float unit = math.max(0.0001f, maxUnitValue);
            if (remaining > unit)
            {
                // ceil(remaining / unit) — how many pieces we need if each is at most `unit`.
                int minNeeded = (int)math.ceil(remaining / unit);
                minNeeded = math.max(1, minNeeded);
                minNeeded = math.min(minNeeded, AbsoluteMaxGemCount);
                if (minNeeded > count)
                    count = minNeeded;
            }

            return math.clamp(count, AbsoluteMinGemCount, AbsoluteMaxGemCount);
        }

        /// <summary>Equal split of remaining value across <paramref name="count"/> gems (sums to remaining).</summary>
        public static float ValuePerGem(float remaining, int count, int gemIndex)
        {
            if (count <= 0)
                return 0f;
            if (gemIndex < 0 || gemIndex >= count)
                return 0f;

            // Give remainder crumbs to the last gem so the sum is exact.
            float baseValue = remaining / count;
            if (gemIndex < count - 1)
                return baseValue;
            return remaining - baseValue * (count - 1);
        }

        /// <summary>Random unit XZ direction (or +Z if degenerate).</summary>
        public static float3 RandomUnitXZ(ref Random rng)
        {
            float3 dir = math.normalize(new float3(rng.NextFloat(-1f, 1f), 0f, rng.NextFloat(-1f, 1f)));
            if (math.lengthsq(dir) < 0.01f)
                return new float3(0f, 0f, 1f);
            return dir;
        }

        /// <summary>Original GemSpawner launch: dir * speed * Random(speedMin, speedMax).</summary>
        public static float3 BurstVelocity(
            float3 dir,
            float explosionSpeed,
            float speedRandomMin,
            float speedRandomMax,
            ref Random rng)
        {
            float lo = math.min(speedRandomMin, speedRandomMax);
            float hi = math.max(speedRandomMin, speedRandomMax);
            float speed = explosionSpeed * rng.NextFloat(lo, hi);
            return dir * speed;
        }

        /// <summary>
        /// Keeps burst motion radially outward from the asteroid center on XZ.
        /// <para>
        /// [TITAN-ORBIT] Spawn offset and launch velocity already share one unit dir on the server.
        /// Client presentation integrates ghosted Velocity between LT samples (same damping);
        /// this helper is for spawn validation / debug radial clamps, not inventing a local burst.
        /// Uses <see cref="ToroidalMapEcs.ShortestOffsetXZ"/> so a gem that sits on the
        /// opposite wrap copy of the burst center still aims outward (not the long way across the map).
        /// </para>
        /// </summary>
        /// <param name="position">Current gem logical (or display) position.</param>
        /// <param name="burstCenter">Asteroid center used for the explosion (same space as position).</param>
        /// <param name="velocity">Current velocity; Y is forced to 0.</param>
        /// <param name="mapW">Toroidal map width (from MapState / session meta).</param>
        /// <param name="mapH">Toroidal map height.</param>
        /// <returns>Velocity of the same speed aimed away from <paramref name="burstCenter"/> on XZ.</returns>
        public static float3 EnsureOutwardBurstVelocity(
            float3 position,
            float3 burstCenter,
            float3 velocity,
            float mapW,
            float mapH)
        {
            // --- Radial from asteroid center to gem (shortest path on torus) ---
            float3 radial = ToroidalMapEcs.ShortestOffsetXZ(burstCenter, position, mapW, mapH);
            float radialLenSq = math.lengthsq(radial);
            if (radialLenSq < 1e-6f)
            {
                // Still on the center — keep existing XZ direction, or +Z if velocity is zero.
                float3 fallback = new float3(velocity.x, 0f, velocity.z);
                if (math.lengthsq(fallback) < 1e-8f)
                    return new float3(0f, 0f, 0f);
                return fallback;
            }

            float3 outward = math.normalize(radial);
            float3 planarVel = new float3(velocity.x, 0f, velocity.z);
            float speed = math.length(planarVel);
            if (speed < 1e-6f)
                return float3.zero;

            // Same speed, always away from the rock — kills inward / opposite-side glitches.
            return outward * speed;
        }

        /// <summary>
        /// Overload using cached <see cref="ToroidalMapEcs"/> map size when latched.
        /// Prefer the explicit mapW/mapH overload when MapState / session meta is available.
        /// When size is unset, uses Euclidean radial (no invented period).
        /// </summary>
        public static float3 EnsureOutwardBurstVelocity(float3 position, float3 burstCenter, float3 velocity)
        {
            if (ToroidalMapEcs.TryGetMapSize(out float mapW, out float mapH))
                return EnsureOutwardBurstVelocity(position, burstCenter, velocity, mapW, mapH);

            // --- No period latched yet: planar Euclidean outward (skip wrap math) ---
            float3 radial = position - burstCenter;
            radial.y = 0f;
            if (math.lengthsq(radial) < 1e-8f)
                return float3.zero;
            float3 outward = math.normalize(radial);
            float3 planarVel = new float3(velocity.x, 0f, velocity.z);
            float speed = math.length(planarVel);
            if (speed < 1e-6f)
                return float3.zero;
            return outward * speed;
        }

        /// <summary>Original GemSpawner tumble: Random(-max, max) per axis.</summary>
        public static float3 BurstAngularVelocity(float angularSpeedMax, ref Random rng)
        {
            float m = math.max(0f, angularSpeedMax);
            return new float3(
                rng.NextFloat(-m, m),
                rng.NextFloat(-m, m),
                rng.NextFloat(-m, m));
        }

        /// <summary>
        /// PhysX-style linear damping step matching original <c>Rigidbody.linearDamping = 0.5</c> feel:
        /// <c>v *= 1 / (1 + damping * dt)</c>, then hard-stop below threshold.
        /// </summary>
        public static float3 IntegrateLinearVelocity(float3 velocity, float linearDamping, float stopSpeedThreshold, float dt)
        {
            float damp = math.max(0f, linearDamping);
            velocity *= 1f / (1f + damp * dt);
            if (math.lengthsq(velocity) < stopSpeedThreshold * stopSpeedThreshold)
                return float3.zero;
            return velocity;
        }

        /// <summary>Light angular damping for coasting tumble.</summary>
        public static float3 IntegrateAngularVelocity(float3 angularVelocity, float angularDamping, float dt)
        {
            float damp = math.max(0f, angularDamping);
            angularVelocity *= 1f / (1f + damp * dt);
            if (math.lengthsq(angularVelocity) < 0.0001f)
                return float3.zero;
            return angularVelocity;
        }

        /// <summary>
        /// Original Gem shrink: full scale until the last <paramref name="shrinkDurationSeconds"/> of life,
        /// then linear 1→0. Returns 1 when lifetime/shrink are disabled or the gem is still early in life.
        /// </summary>
        /// <param name="spawnServerTime">Server ElapsedTime when the gem spawned.</param>
        /// <param name="nowServerTime">Current server (or client-synced) ElapsedTime.</param>
        /// <param name="lifetimeSeconds">Total life before despawn (original 20).</param>
        /// <param name="shrinkDurationSeconds">End-of-life shrink window (original 3).</param>
        /// <returns>Multiplier in [0, 1] applied to the gem's full visual scale.</returns>
        public static float LifetimeScaleMultiplier(
            float spawnServerTime,
            float nowServerTime,
            float lifetimeSeconds,
            float shrinkDurationSeconds)
        {
            float lifetime = math.max(0.01f, lifetimeSeconds);
            float elapsed = nowServerTime - spawnServerTime;
            if (elapsed < 0f)
                return 1f;
            if (elapsed >= lifetime)
                return 0f;

            float shrink = math.clamp(shrinkDurationSeconds, 0f, lifetime);
            if (shrink <= 0.001f)
                return 1f;

            float remaining = lifetime - elapsed;
            if (remaining >= shrink)
                return 1f;

            return math.saturate(remaining / shrink);
        }
    }
}
