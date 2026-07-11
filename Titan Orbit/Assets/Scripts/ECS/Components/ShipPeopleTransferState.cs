using Unity.Entities;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server-only accumulator state for people transport between planets. Tracks orbit dwell time,
    /// fractional load/unload, and in-flight crew count. Read/written by
    /// <see cref="PeopleTransportSystem"/>. Not ghost-serialized — clients infer transport from
    /// replicated <see cref="ShipState.CurrentPeople"/> and transport VFX.
    /// </summary>
    public struct ShipPeopleTransferState : IComponentData
    {
        // --- Type members ---
        /// <summary>Seconds the ship has remained in orbit at the current planet.</summary>
        public float OrbitDwellSeconds;

        /// <summary>PlanetId where dwell timer is accumulating; 0 when not in a tracked orbit.</summary>
        public int LastOrbitPlanetId;

        /// <summary>Crew units currently animating between ship and planet (not yet in CurrentPeople).</summary>
        public float PeopleInTransit;

        /// <summary>Fractional accumulator toward loading one crew unit onto the ship.</summary>
        public float LoadAccumulator;

        /// <summary>Fractional accumulator toward unloading one crew unit at a planet.</summary>
        public float UnloadAccumulator;
    }
}
