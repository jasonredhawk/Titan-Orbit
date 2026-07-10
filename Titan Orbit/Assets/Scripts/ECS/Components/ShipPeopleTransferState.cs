using Unity.Entities;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server-only state for people transport between planets. Tracks orbit dwell time,
    /// transfer accumulators, and in-flight crew count. Read/written by PeopleTransportSystem.
    /// Not ghost-serialized — clients infer transport from replicated ShipState.CurrentPeople.
    /// </summary>
    public struct ShipPeopleTransferState : IComponentData
    {
        /// <summary>Seconds the ship has remained in orbit at the current planet.</summary>
        public float OrbitDwellSeconds;
        public int LastOrbitPlanetId;
        /// <summary>Crew units currently animating between ship and planet.</summary>
        public float PeopleInTransit;
        /// <summary>Fractional accumulator for loading crew onto the ship.</summary>
        public float LoadAccumulator;
        /// <summary>Fractional accumulator for unloading crew at a planet.</summary>
        public float UnloadAccumulator;
    }
}
