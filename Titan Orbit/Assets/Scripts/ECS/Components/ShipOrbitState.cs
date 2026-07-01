using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>Replicated orbit context for HUD and gameplay queries.</summary>
    public struct ShipOrbitState : IComponentData
    {
        [GhostField] public int OrbitPlanetId;
        [GhostField] public bool InOrbitRing;
        [GhostField] public bool UsingOrbitMotor;
    }
}
