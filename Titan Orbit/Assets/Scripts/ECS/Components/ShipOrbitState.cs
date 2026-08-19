using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [NETCODE] Replicated orbit context for HUD indicators, orbit-ring tint, and gameplay queries.
    /// Updated each predicted motor tick by <see cref="ShipPhysicsDriveLogic"/> when the ship is
    /// inside a planet's orbit ring (toroidal distance). Ghost-serialized so clients show orbit UI,
    /// tractor-beam range bonuses, planet ring occupancy, and so server
    /// <see cref="PeopleTransportDispatchSystem"/> can dwell before load/unload — without
    /// MonoBehaviour reading raw sim.
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
        /// [TITAN-ORBIT] True when passive orbit motor is steering the ship (no thrust input).
        /// Fire does not cancel orbit — weapons are locked while <see cref="InOrbitRing"/>.
        /// Used by HUD to show orbit-mode indicator.
        /// </summary>
        [GhostField] public bool UsingOrbitMotor;

        /// <summary>
        /// [TITAN-ORBIT] True when this ship is dwelling long enough that people load/unload is
        /// in progress (or inbound crew is still in flight). Ghosted so every client can tint
        /// that planet's orbit ring yellow for any locked-in hull — friendly or enemy.
        /// Written by <c>PeopleTransportDispatchSystem</c>; the motor preserves it while
        /// <see cref="UsingOrbitMotor"/> stays true and clears it on thrust / leave.
        /// </summary>
        [GhostField] public bool IsTransferringPeople;
    }
}
