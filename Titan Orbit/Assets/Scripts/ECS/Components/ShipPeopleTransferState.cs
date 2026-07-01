using Unity.Entities;

namespace TitanOrbit.ECS
{
    /// <summary>Tracks coasting dwell time in a planet orbit ring before people transfer begins.</summary>
    public struct ShipPeopleTransferState : IComponentData
    {
        public float OrbitDwellSeconds;
        public int LastOrbitPlanetId;
    }
}
