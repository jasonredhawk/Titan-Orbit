using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.NetCode
{
    // --- Type members ---
    /// <summary>
    /// Client-world hook reserved for future client-side tick tuning. Titan Orbit uses NetCode's
    /// default Interpolated presentation mode today, so OnUpdate is intentionally empty. Kept as
    /// a named extension point if we need client-only timing overrides without touching server Hz.
    /// World: ClientSimulation. Group: SimulationSystemGroup (first).
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderFirst = true)]
    public partial struct TitanOrbitClientTickRateSystem : ISystem
    {
        /// <summary>No-op — server <see cref="TitanOrbitServerTickRateSystem"/> owns tick rate today.</summary>
        public void OnUpdate(ref SystemState state) { }
    }
}
