using Unity.Entities;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server-only metronome for hold-V cargo dumps.
    /// Not ghosted — clients see the spawned gem ghosts. Added at runtime by
    /// <see cref="ShipEnsureComponentsSystem"/>.
    /// </summary>
    public struct ShipGemExpelTimer : IComponentData
    {
        /// <summary>
        /// Seconds accumulated toward the next dump pulse.
        /// Primed to one full interval on first eligible tick so the first gem fires immediately.
        /// Reset to 0 when V is released or the hold is empty.
        /// </summary>
        public float Accum;
    }
}
