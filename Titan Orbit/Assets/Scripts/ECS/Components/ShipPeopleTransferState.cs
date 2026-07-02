using Unity.Entities;

namespace TitanOrbit.ECS
{
    /// <summary>Tracks orbit dwell, transfer accumulators, and in-flight load crew.</summary>
    public struct ShipPeopleTransferState : IComponentData
    {
        public float OrbitDwellSeconds;
        public int LastOrbitPlanetId;
        public float PeopleInTransit;
        public float LoadAccumulator;
        public float UnloadAccumulator;
    }
}
