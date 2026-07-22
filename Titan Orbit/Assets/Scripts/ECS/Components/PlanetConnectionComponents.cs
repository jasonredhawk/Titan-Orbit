using TitanOrbit.Core;
using TitanOrbit.Simulation;
using Unity.Entities;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Marker on the server (and client-local) singleton that owns planet-connection edge/triangle
    /// buffers. Not a NetCode ghost — clients rebuild the same topology from Instantiated planet
    /// snapshots via <see cref="PlanetConnectionGraphCache"/> so Windows late-join never needs a
    /// planet <c>ToEntityArray</c>. Authoritative bonuses (asteroid tint, pop) run on the server only.
    /// </summary>
    public struct PlanetConnectionGraphTag : IComponentData { }

    /// <summary>
    /// One undirected same-team planet edge in the connection graph.
    /// Written by <see cref="PlanetConnectionGraphSystem"/> after ownership changes.
    /// </summary>
    public struct PlanetConnectionEdgeElement : IBufferElementData
    {
        /// <summary>Smaller planet id endpoint (canonical order).</summary>
        public int PlanetIdA;

        /// <summary>Larger planet id endpoint (canonical order).</summary>
        public int PlanetIdB;

        /// <summary>Owning team for this edge.</summary>
        public TeamId Team;
    }

    /// <summary>
    /// One territory triangle — three same-team planets plus average level / gem multiplier.
    /// Vertex world positions are resolved live (gem moons) when tinting asteroids or drawing.
    /// </summary>
    public struct PlanetConnectionTriangleElement : IBufferElementData
    {
        public int PlanetIdA;
        public int PlanetIdB;
        public int PlanetIdC;
        public TeamId Team;

        /// <summary>Mean of the three planet levels at last rebuild.</summary>
        public float AverageLevel;

        /// <summary><c>1 + AverageLevel × 0.05</c> — strongest-triangle pick key.</summary>
        public float GemBonusMultiplier;
    }

    /// <summary>
    /// Server-only bookkeeping on the graph singleton: last ownership fingerprint and recompute timer.
    /// </summary>
    public struct PlanetConnectionGraphState : IComponentData
    {
        /// <summary>Server ElapsedTime of last full rebuild.</summary>
        public float LastRebuildElapsed;

        /// <summary>
        /// Hash of (PlanetId, Ownership, PlanetLevel) across all planets — dirty when capture/level changes.
        /// </summary>
        public uint OwnershipFingerprint;

        /// <summary>True while an animated one-planet-per-tick rebuild is in progress.</summary>
        public bool RebuildInProgress;
    }
}
