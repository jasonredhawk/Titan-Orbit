using Unity.Burst;
using Unity.Mathematics;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Shared tick / frame advance for authoritative bullets and cosmetic tracers.
    /// Substep count comes from <see cref="BulletCollision"/> so a long
    /// <c>|vel|*dt</c> cannot skip a small body between samples.
    /// </summary>
    [BurstCompile]
    public static class BulletFlight
    {
        /// <summary>
        /// End of this step and how many equal segments to sweep.
        /// </summary>
        // Not a [BurstCompile] entry point. A direct-call export passes these float3
        // arguments by value, and Linux / WebGL AOT rejects that (BC1064 / BC1067).
        // Burst jobs still compile this body when they call it.
        public static void GetStep(
            in float3 from,
            in float3 velocity,
            float dt,
            out float3 to,
            out int substeps)
        {
            GetStep(from, velocity, dt, float.MaxValue, out to, out substeps);
        }

        /// <summary>
        /// Same as <see cref="GetStep(in float3, in float3, float, out float3, out int)"/>, but the
        /// Euclidean step cannot exceed <paramref name="maxTravel"/>. Planetary-defense bolts
        /// use Lifetime 0 + MaxDistance; a client frame longer than the 60 Hz sim step was
        /// reaching a hull the server had already expired.
        /// </summary>
        /// <param name="maxTravel">Remaining flight budget (MaxDistance − Traveled).</param>
        // Same as the overload above: no direct-call export (BC1064 / BC1067).
        public static void GetStep(
            in float3 from,
            in float3 velocity,
            float dt,
            float maxTravel,
            out float3 to,
            out int substeps)
        {
            if (maxTravel <= RangeStopEpsilon)
            {
                to = from;
                substeps = 1;
                return;
            }

            to = from + velocity * dt;
            float stepDistance = math.distance(from, to);
            if (stepDistance > maxTravel && stepDistance > 1e-6f)
                to = from + (to - from) * (maxTravel / stepDistance);
            substeps = BulletCollision.ComputeAdvanceSubstepCount(math.min(stepDistance, maxTravel));
        }

        /// <summary>
        /// <see cref="GetStep"/> parks the pose when remaining travel is at or below this.
        /// Expire with the same threshold or the tracer freezes at max range.
        /// </summary>
        public const float RangeStopEpsilon = 1e-5f;

        /// <summary>
        /// Arrive slop so float error cannot leave <c>Traveled</c> just shy of MaxDistance.
        /// </summary>
        public const float RangeArriveSlop = 1e-4f;

        /// <summary>
        /// True when the shot has no remaining travel budget. Matches server
        /// <c>BulletAdvanceJob</c> so client tracers cannot freeze after the last clamp.
        /// </summary>
        [BurstCompile]
        public static bool IsRangeExpired(float traveled, float maxDistance)
        {
            return (maxDistance - traveled) <= RangeStopEpsilon ||
                   traveled >= maxDistance - RangeArriveSlop;
        }

        /// <summary>
        /// End of substep <paramref name="index"/> (0-based) along [from, to].
        /// </summary>
        public static float3 SubstepEnd(float3 from, float3 to, int index, int substeps)
        {
            int n = math.max(1, substeps);
            int i = math.clamp(index, 0, n - 1);
            return math.lerp(from, to, (i + 1) / (float)n);
        }
    }
}
