using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Replicated orbit context for HUD indicators and gameplay queries. Updated each motor tick
    /// by <see cref="ShipMovementBurstLogic"/> when the ship is inside a planet's orbit ring.
    /// Ghost-serialized so clients show orbit UI without reading raw sim state in MonoBehaviour.
    /// </summary>
    public struct ShipOrbitState : IComponentData
    {
        /// <summary>PlanetId of the nearest planet whose orbit ring contains the ship; 0 if none.</summary>
        [GhostField] public int OrbitPlanetId;
        /// <summary>True when ship position is inside any planet's orbit ring.</summary>
        [GhostField] public bool InOrbitRing;
        /// <summary>True when passive orbit motor is steering (no thrust, no fire).</summary>
        [GhostField] public bool UsingOrbitMotor;
    }
}
