using TitanOrbit.Core;
using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Mathematics;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Shared enemy-territory comms jam test. A living ship inside a triangle that
    /// is not its own cannot send or receive hold-S callouts.
    /// <para>
    /// [TITAN-ORBIT] Not a ghost field — both sides recompute from hull pose plus the
    /// published planet-center triangles. Open space is clear. A friendly overlap
    /// (own triangle plus a stronger enemy face) stays clear, matching territory
    /// speed / mining. Server rejects the speaker and skips jammed recipients;
    /// the client locks the compose card and drops inbound chips.
    /// </para>
    /// Lives in the ECS assembly so <c>ShipCommsServerSystem</c> and
    /// <c>ShipCommsRpcClientSystem</c> can call it without a Game → UI reference.
    /// Map size comes from <see cref="ToroidalMapEcs"/> (never invent 1000×1000).
    /// </summary>
    public static class ShipCommsJam
    {
        /// <summary>
        /// True when <paramref name="worldPos"/> sits in enemy-owned fill for
        /// <paramref name="team"/> on the already-published graph for
        /// <paramref name="side"/>.
        /// </summary>
        /// <param name="worldPos">Hull world pose (Y ignored).</param>
        /// <param name="team">That ship's faction. None treats every triangle as hostile.</param>
        /// <param name="side">Server list for authority; Client list for HUD / inbox.</param>
        /// <returns>
        /// False when map size is unset, the graph has never published, the point is
        /// in open space, or the ship's team owns a containing triangle.
        /// </returns>
        public static bool IsPositionJammed(
            float3 worldPos,
            TeamId team,
            PlanetConnectionGraphSide side)
        {
            // --- Map size ---
            // [TITAN-ORBIT] PIT is toroidal. Missing size → skip jam (never invent 1000).
            if (!ToroidalMapEcs.TryGetMapSize(out float mapW, out float mapH))
                return false;

            // --- Published verts (no planet collect on this path) ---
            if (!PlanetConnectionGraphCache.TryGetPublishedRuntimeNative(side, out var triangles))
                return false;

            return PlanetConnectionGraphLogic.IsInNonFriendlyTriangle(
                worldPos, team, triangles, mapW, mapH);
        }
    }
}
