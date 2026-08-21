using TitanOrbit.Generation;
using Unity.Burst;
using Unity.Mathematics;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Shared tick / frame advance for authoritative bullets and cosmetic tracers.
    /// Steps stay on the sphere shell, then substep for swept hits.
    /// </summary>
    [BurstCompile]
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
            GetStep(from, velocity, dt, out to, out _, out substeps);
        }

        /// <summary>
        /// Same as <see cref="GetStep(float3,float3,float,out float3,out int)"/> but also
        /// returns the parallel-transported tangent velocity at <paramref name="to"/>.
        /// </summary>
        public static void GetStep(
            float3 from,
            float3 velocity,
            float dt,
            out float3 to,
            out float3 velocityOnShell,
            out int substeps)
        {
            float radius = SphericalMapEcs.BurstSafeRadius(from);
            SphericalMapEcs.StepOnSphere(from, velocity, dt, radius, out to, out velocityOnShell);
            float stepDistance = SphericalMapEcs.GeodesicDistance(from, to, radius);
            substeps = BulletCollision.ComputeAdvanceSubstepCount(stepDistance);
        }

        /// <summary>
        /// End of substep <paramref name="index"/> (0-based) along the great-circle [from, to].
        /// </summary>
        public static float3 SubstepEnd(float3 from, float3 to, int index, int substeps)
        {
            int n = math.max(1, substeps);
            int i = math.clamp(index, 0, n - 1);
            float t = (i + 1) / (float)n;
            float radius = SphericalMapEcs.BurstSafeRadius(from);
            return SphericalMapEcs.SphericalLerp(from, to, t, radius);
        }
    }
}
