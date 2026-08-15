using TitanOrbit.Data;
using Unity.Mathematics;

namespace TitanOrbit.Simulation
{
    /// <summary>
    /// Shared cargo-scoop radii for server <c>GemPickupSystem</c> and client beam / predicted-hide.
    /// Floors designer wing/hull ranges at the <b>visible</b> crystal radius so the mesh you
    /// overlap is the gem the server consumes.
    /// </summary>
    public static class GemCollectMath
    {
        /// <summary>
        /// Wing-tip absorb radius: designer collect range, but never smaller than the crystal.
        /// </summary>
        /// <param name="settings">TractorBeamSettings (wing collect + size pad).</param>
        /// <param name="gemValue"><c>GemState.Value</c> — drives visual scale.</param>
        /// <param name="gemSize"><c>GemState.Size</c> — sim scale pad on the designer radius.</param>
        public static float ResolveWingCollectRadius(
            TractorBeamSettings settings,
            float gemValue,
            float gemSize)
        {
            float designed = settings.ResolveWingCollectRadius(gemSize);
            float visual = GemPresentationScale.ComputeVisualRadius(gemValue);
            return math.max(designed, visual);
        }

        /// <summary>
        /// Hull-center absorb radius: designer hull range, hull collider floor, and visual crystal.
        /// </summary>
        /// <param name="settings">TractorBeamSettings (hull range + size pad).</param>
        /// <param name="gemValue"><c>GemState.Value</c>.</param>
        /// <param name="gemSize"><c>GemState.Size</c>.</param>
        /// <param name="shipScale">Ship <c>LocalTransform.Scale</c> for hull-collider floor.</param>
        public static float ResolveHullCollectRadius(
            TractorBeamSettings settings,
            float gemValue,
            float gemSize,
            float shipScale)
        {
            float designed = settings.ResolveHullPickupRange(gemSize);
            float hullFloor = BodyCollisionMath.GetShipHullRadiusWorld(shipScale) +
                              math.max(0f, gemSize) * 0.5f;
            float visual = GemPresentationScale.ComputeVisualRadius(gemValue);
            return math.max(designed, math.max(hullFloor, visual));
        }
    }
}
