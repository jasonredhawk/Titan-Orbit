using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [NETCODE] Replicated moon landing progress for a ship. Updated server-side by
    /// <see cref="ShipMoonDockSystem"/> each fixed step. [TITAN-ORBIT] LandingProgress reaches 1 when
    /// the ship is fully docked and gem deposit / orbit store actions are allowed. While fully
    /// landed, dock latches until thrust — the hull co-orbits via <see cref="ShipPhysicsDriveLogic"/>
    /// so the moving moon cannot clear the zone. Thrust starts a forced takeoff
    /// (<see cref="TakeoffPlanetId"/>) that exits the moon orbit zone away from the planet.
    /// Ghost-serialized so clients show landing UI and dock cinematics without reading raw sim
    /// in MonoBehaviour.
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

        /// <summary>
        /// [TITAN-ORBIT] Planet id while the motor is forcing the hull out of this moon's
        /// orbit zone (away from the planet). 0 when not taking off.
        /// </summary>
        [GhostField] public int TakeoffPlanetId;

        /// <summary>
        /// [TITAN-ORBIT] 0–1 takeoff progress along the outward ray. Reaches 1 when the hull
        /// is outside the moon orbit zone; bank presentation holds neutral until then.
        /// </summary>
        [GhostField] public float TakeoffProgress;

        /// <summary>
        /// True when this ship is fully docked on a gem moon. Same gate as bullet / ram immunity
        /// and planetary-defense targeting (do not acquire landed hulls).
        /// </summary>
        public bool IsFullyLanded =>
            MoonPlanetId != 0 &&
            LandingProgress >= GemEconomyConstants.MoonLandingCompleteThreshold;

        /// <summary>
        /// True while thrust-to-leave is forcing the hull out of the moon orbit zone.
        /// </summary>
        public bool IsTakingOff => TakeoffPlanetId != 0;

        /// <summary>
        /// Reads <see cref="ShipMoonDockState"/> when present and reports
        /// <see cref="IsFullyLanded"/>. False when the component is missing.
        /// </summary>
        public static bool IsFullyLandedOnMoon(EntityManager em, Entity ship)
        {
            if (!em.HasComponent<ShipMoonDockState>(ship))
                return false;
            return em.GetComponentData<ShipMoonDockState>(ship).IsFullyLanded;
        }
    }
}
