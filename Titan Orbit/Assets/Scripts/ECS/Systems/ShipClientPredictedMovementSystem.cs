using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Intentionally idle. Baseline mode uses Interpolated ghosts:
    /// client sends ShipInput, server owns movement, client displays the interpolated LocalTransform.
    /// Kept so group attributes remain available when OwnerPredicted is re-enabled later.
    /// </summary>
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    public partial class ShipClientPredictedMovementSystem : SystemBase
    {
        protected override void OnCreate()
        {
            Enabled = false;
        }

        protected override void OnUpdate() { }
    }
}
