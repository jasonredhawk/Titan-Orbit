using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [NETCODE] Replicated orbit context for HUD indicators and gameplay queries. Updated each
    /// predicted motor tick by <see cref="ShipPhysicsDriveLogic"/> when the ship is inside a
    /// planet's orbit ring (toroidal distance). Ghost-serialized so clients show orbit UI,
    /// tractor-beam range bonuses, and so server <see cref="PeopleTransportDispatchSystem"/> can
    /// dwell before load/unload — without MonoBehaviour reading raw sim.
    /// Paired with <see cref="ShipMoonDockState"/> for moon-specific actions inside the ring.
    /// </summary>
    public struct ShipOrbitState : IComponentData
    {
        // --- Type members ---
        /// <summary>
        /// [TITAN-ORBIT] <see cref="PlanetState.PlanetId"/> of the nearest planet whose orbit ring
        /// contains the ship; 0 if the ship is in open space.
        /// </summary>
        [GhostField] public int OrbitPlanetId;

        /// <summary>
        /// [TITAN-ORBIT] True when ship position is inside any planet's orbit ring (friendly or enemy).
        /// </summary>
        [GhostField] public bool InOrbitRing;

        /// <summary>
        /// [TITAN-ORBIT] True when passive orbit motor is steering the ship (no thrust input, no fire).
        /// Used by HUD to show orbit-mode indicator.
        /// </summary>
        [GhostField] public bool UsingOrbitMotor;
    }
}
