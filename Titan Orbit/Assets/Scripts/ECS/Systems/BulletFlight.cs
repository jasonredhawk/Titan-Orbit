using Unity.Mathematics;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Shared tick / frame advance for authoritative bullets and cosmetic tracers.
    /// Substep count comes from <see cref="BulletCollision"/> so a long
    /// <c>|vel|*dt</c> cannot skip a small body between samples.
    /// </summary>
    public static class BulletFlight
    {
        /// <summary>
        /// End of this step and how many equal segments to sweep.
        /// </summary>
        public static void GetStep(
            float3 from,
            float3 velocity,
            float dt,
            out float3 to,
            out int substeps)
        {
            to = from + velocity * dt;
            float stepDistance = math.distance(from, to);
            substeps = BulletCollision.ComputeAdvanceSubstepCount(stepDistance);
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
