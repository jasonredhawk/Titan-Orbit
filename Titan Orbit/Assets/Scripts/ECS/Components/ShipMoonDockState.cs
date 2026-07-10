using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Replicated moon landing progress for a ship. Updated server-side by ShipMoonDockSystem.
    /// LandingProgress reaches 1 when the ship is fully docked and gem deposit is allowed.
    /// Ghost-serialized so clients can show landing UI and dock cinematics.
    /// </summary>
    public struct ShipMoonDockState : IComponentData
    {
        /// <summary>PlanetId of the moon being approached/landed on; 0 when not docking.</summary>
        [GhostField] public int MoonPlanetId;
        /// <summary>0–1 progress; reaches 1 after landing dwell completes on moon surface.</summary>
        [GhostField] public float LandingProgress;
        /// <summary>Accumulated stillness time toward approach delay; resets while moving/shooting.</summary>
        [GhostField] public float LandingApproachDelay;
    }
}
