using Unity.Entities;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Registers ship reconcile smoothing once the NetCode singleton exists.
    /// Host worlds never roll a predicted hull back, so they skip registration.
    /// A remote client does: after each snapshot the resim pose is stepped toward
    /// from the pose already on screen, instead of replacing it.
    /// Bullets share <see cref="LocalTransform"/> but have no <see cref="ShipTag"/>,
    /// and the callbacks leave those ghosts on the resim result.
    /// World: ClientSimulation. Group: InitializationSystemGroup (last).
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(InitializationSystemGroup), OrderLast = true)]
    public partial struct TitanOrbitShipPredictionSmoothingBootstrap : ISystem
    {
        /// <summary>
        /// Registers pose and velocity smoothing for ship ghosts, then disables this system.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // The host simulates the authority pose. There is no snapshot to snap back to.
            if (state.WorldUnmanaged.IsHost())
            {
                state.Enabled = false;
                return;
            }

            if (!SystemAPI.TryGetSingletonRW<GhostPredictionSmoothing>(out var smoothing))
                return;

            var em = state.EntityManager;
            smoothing.ValueRW.RegisterSmoothingAction<LocalTransform, ShipTag>(
                em, ShipLocalTransformPredictionSmoothing.Action);
            smoothing.ValueRW.RegisterSmoothingAction<ShipKinematics, ShipTag>(
                em, ShipKinematicsPredictionSmoothing.Action);
            state.Enabled = false;
        }
    }
}
