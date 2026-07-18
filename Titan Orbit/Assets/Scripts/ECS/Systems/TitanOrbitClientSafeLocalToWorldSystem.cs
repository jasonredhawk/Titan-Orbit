using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Retired: Burst LocalToWorld runs again after join settle so Entities Graphics ships render.
    /// Kept as a disabled stub so older references / asmdef churn do not break compiles.
    /// Map-body Crash!!! prevention is MarkFromQuery disabled + Pending-only visualizer — not
    /// permanent TransformSystemGroup quarantine.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct TitanOrbitClientSafeLocalToWorldSystem : ISystem
    {
        /// <summary>Disables this system permanently.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<NetworkStreamInGame>();
            state.Enabled = false;
        }

        /// <summary>No-op.</summary>
        public void OnUpdate(ref SystemState state) { }
    }
}
