using Unity.Entities;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Per-ship sticky hold for friendly-territory thrust / max-speed multiplier.
    /// [TITAN-ORBIT] Filled same-team triangles multiply motor by <c>1 + 0.05 × homePlanetLevel</c>
    /// — this is <b>not</b> a ship MovementSpeed attribute upgrade. Point-in-triangle can flicker
    /// for a frame at edges or during brief runtime-cache gaps; the latch keeps the last boost
    /// for <see cref="TitanOrbit.Simulation.PlanetConnectionGraphLogic.TerritoryBoostStickySeconds"/>
    /// so cruise feels stable. Updated each predicted motor tick on client + server (not ghosted —
    /// both sides recompute from the same triangles + moon clock).
    /// Paired with presentation <see cref="PlanetConnectionGraphCache.LocalOwnerTerritoryMult"/>.
    /// </summary>
    public struct ShipTerritoryBoostLatch : IComponentData
    {
        /// <summary>
        /// Last friendly territory mult (≥ 1). Holds briefly after exit so edge flicker does not
        /// drop EngineThrust / MaxSpeed every tick.
        /// </summary>
        public float LatchedMult;

        /// <summary>
        /// Moon-orbit elapsed seconds until which <see cref="LatchedMult"/> stays active after
        /// leaving the fill. Negative when cleared.
        /// </summary>
        public double HoldUntilElapsed;
    }
}
