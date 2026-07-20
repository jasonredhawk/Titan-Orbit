using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [NETCODE] Replicated moon landing progress for a ship. Updated server-side by
    /// <see cref="ShipMoonDockSystem"/> each fixed step. [TITAN-ORBIT] LandingProgress reaches 1 when
    /// the ship is fully docked and gem deposit / orbit store actions are allowed. While fully
    /// landed, dock latches until thrust — the hull co-orbits via <see cref="ShipPhysicsDriveLogic"/>
    /// so the moving moon cannot clear the zone. Ghost-serialized so clients show landing UI and
    /// dock cinematics without reading raw sim in MonoBehaviour.
    /// </summary>
    public struct ShipMoonDockState : IComponentData
    {
        // --- Type members ---
        /// <summary>
        /// [TITAN-ORBIT] <see cref="PlanetState.PlanetId"/> of the moon being approached or landed on;
        /// 0 when the ship is not in a docking sequence.
        /// </summary>
        [GhostField] public int MoonPlanetId;

        /// <summary>
        /// [TITAN-ORBIT] 0–1 landing progress; reaches 1 after the landing dwell timer completes on
        /// the moon surface. Orbit store and deposit require progress near 1.
        /// </summary>
        [GhostField] public float LandingProgress;

        /// <summary>
        /// [TITAN-ORBIT] Accumulated stillness time toward approach delay; resets while the ship
        /// moves or fires weapons (prevents instant dock while fighting).
        /// </summary>
        [GhostField] public float LandingApproachDelay;
    }
}
