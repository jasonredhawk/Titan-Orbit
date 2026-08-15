using Unity.Mathematics;

namespace TitanOrbit.Simulation
{
    /// <summary>
    /// Shared gem crystal size from cargo value. Server pickup and client mesh scale must use
    /// the same curve — otherwise the player overlaps a large crystal whose sim radius is tiny
    /// and the gem never consumes.
    /// <para>
    /// [TITAN-ORBIT] Value 1 → scale 0.5, value 100 → scale 4 (legacy <c>GemVisualApplier</c>).
    /// <c>GemState.Size</c> is the sim LocalTransform scale (0.2–0.5) and is <b>not</b> the
    /// visible diameter. Collect tests floor at this visual radius.
    /// </para>
    /// </summary>
    public static class GemPresentationScale
    {
        /// <summary>Designer reference: smallest cargo value on the visual curve.</summary>
        public const float MinGemValue = 1f;

        /// <summary>Designer reference: cargo value that reaches max visual scale.</summary>
        public const float MaxGemValue = 100f;

        /// <summary>Uniform local scale at <see cref="MinGemValue"/>.</summary>
        public const float ScaleAtMinValue = 0.5f;

        /// <summary>Uniform local scale at <see cref="MaxGemValue"/>.</summary>
        public const float ScaleAtMaxValue = 4f;

        /// <summary>
        /// Uniform visual scale for a gem of this cargo value (same number the hybrid mesh uses).
        /// </summary>
        /// <param name="gemValue">Authoritative <c>GemState.Value</c>.</param>
        /// <returns>Uniform local scale (world diameter when the mesh is ~1 unit at scale 1).</returns>
        public static float ComputeVisualScale(float gemValue)
        {
            float t = math.unlerp(MinGemValue, MaxGemValue, gemValue);
            t = math.saturate(t);
            return math.lerp(ScaleAtMinValue, ScaleAtMaxValue, t);
        }

        /// <summary>
        /// World-space radius of the visible crystal (half of <see cref="ComputeVisualScale"/>).
        /// Use this as a floor on wing-tip / hull scoop so overlapping the mesh counts.
        /// </summary>
        /// <param name="gemValue">Authoritative <c>GemState.Value</c>.</param>
        public static float ComputeVisualRadius(float gemValue) =>
            ComputeVisualScale(math.max(0.25f, gemValue)) * 0.5f;
    }
}
