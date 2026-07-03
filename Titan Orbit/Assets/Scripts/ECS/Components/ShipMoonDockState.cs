using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>Server-side gem-moon surface landing progress (1 = fully landed, deposit allowed).</summary>
    public struct ShipMoonDockState : IComponentData
    {
        [GhostField] public int MoonPlanetId;
        /// <summary>0–1 progress; reaches 1 after landing dwell completes on moon surface.</summary>
        [GhostField] public float LandingProgress;
    }
}
