using Unity.Entities;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server-only accumulator state for people transport between planets. Tracks orbit dwell time,
    /// fractional load/unload progress, and inbound (planet→ship) crew still in flight.
    /// Unload debits <see cref="ShipState.CurrentPeople"/> at dispatch — this component does not
    /// reserve outbound crew. Not ghost-serialized; clients show floats via transport VFX.
    /// </summary>
    public struct ShipPeopleTransferState : IComponentData
    {
        // --- Type members ---
        /// <summary>Seconds the ship has remained in orbit at the current planet.</summary>
        public float OrbitDwellSeconds;

        /// <summary>PlanetId where dwell timer is accumulating; 0 when not in a tracked orbit.</summary>
        public int LastOrbitPlanetId;

        /// <summary>
        /// Inbound crew still flying planet→ship (load). Counts against ship capacity until
        /// <see cref="PeopleTransportSimulationSystem"/> delivers or returns/destroys the transport.
        /// Unload does not use this — ship <see cref="ShipState.CurrentPeople"/> drops at dispatch.
        /// </summary>
        public float PeopleInTransit;

        /// <summary>
        /// Fractional accumulator toward the next load batch.
        /// Batch size is <c>min(shipLevel, planetLevel)</c> people (one transport sphere each).
        /// </summary>
        public float LoadAccumulator;

        /// <summary>
        /// Fractional accumulator toward the next unload batch.
        /// Batch size is ship level only (one transport sphere per person).
        /// </summary>
        public float UnloadAccumulator;
    }
}
