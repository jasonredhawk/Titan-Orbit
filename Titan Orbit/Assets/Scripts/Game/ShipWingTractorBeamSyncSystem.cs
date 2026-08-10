using TitanOrbit.ECS;
using Unity.Entities;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Formerly copied live hull GO wing transforms into <see cref="ShipWingTractorBeamElement"/> each
    /// frame. That path is intentionally disabled.
    /// <para>
    /// [TITAN-ORBIT] Hybrid proxies + <c>ShipWingTractorBeamCollector</c> can invent more Wing-named
    /// children than the sim catalog. Client VFX then showed beams while the server (catalog / prefab
    /// bake) still used a smaller reach set — gems did not pull until the ship was nearly on top of
    /// them. Wing buffers now come only from <see cref="ShipChassisCatalogApplySystem"/> (live prefab
    /// bake) on server and client so beam visuals and pull physics share one source of truth.
    /// </para>
    /// System kept so update-order / asmdef wiring stay stable.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    public partial class ShipWingTractorBeamSyncSystem : SystemBase
    {
        /// <summary>No-op: do not overwrite wing buffers from hybrid GO hierarchies.</summary>
        protected override void OnUpdate()
        {
            // Intentionally empty — see type summary (catalog/prefab bake owns wing locals).
        }
    }
}
