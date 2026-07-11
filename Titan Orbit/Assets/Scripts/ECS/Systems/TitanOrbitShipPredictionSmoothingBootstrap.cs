using Unity.Entities;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Registers <see cref="ShipLocalTransformPredictionSmoothing"/> for ship <see cref="LocalTransform"/>
    /// on client boot. Without this, NetCode uses no owner rollback easing — mispredictions snap visibly.
    /// World: ClientSimulation. Group: InitializationSystemGroup (once at startup).
    /// Paired with <see cref="ShipClientPredictedMovementSystem"/> and NetCode ghost prediction.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(InitializationSystemGroup), OrderLast = true)]
    public partial struct TitanOrbitShipPredictionSmoothingBootstrap : ISystem
    {
        static bool s_Registered;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GhostPredictionSmoothing>();
        }

        /// <summary>
        /// Registers smoothing once the GhostPredictionSmoothing singleton is available, then disables self.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            if (s_Registered)
            {
                state.Enabled = false;
                return;
            }

            var smoothing = SystemAPI.GetSingletonRW<GhostPredictionSmoothing>();
            bool ok = smoothing.ValueRW.RegisterSmoothingAction<LocalTransform>(
                state.EntityManager,
                ShipLocalTransformPredictionSmoothing.Action);

            if (!ok)
                return;

            s_Registered = true;
            state.Enabled = false;
        }
    }
}
