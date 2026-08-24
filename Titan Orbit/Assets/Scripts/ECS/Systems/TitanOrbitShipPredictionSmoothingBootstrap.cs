using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// One-shot client bootstrap for NetCode <see cref="GhostPredictionSmoothing"/>.
    /// <para>
    /// e2d7d2: registering LocalTransform smoothing (switch-v3) did not stop stutter — both
    /// players still reported hitchy motion, matching the earlier P2 blurry-jitter result.
    /// Leave unregistered. Display owns presentation (<c>ShipVisualSyncSystem</c>).
    /// </para>
    /// World: ClientSimulation. Group: InitializationSystemGroup (last).
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(InitializationSystemGroup), OrderLast = true)]
    public partial struct TitanOrbitShipPredictionSmoothingBootstrap : ISystem
    {
        /// <summary>True after we decide once.</summary>
        bool _done;

        /// <summary>Confirms GhostPredictionSmoothing stays unregistered.</summary>
        public void OnUpdate(ref SystemState state)
        {
            if (_done)
            {
                state.Enabled = false;
                return;
            }

            _done = true;
            state.Enabled = false;

            if (!SystemAPI.TryGetSingletonRW<GhostPredictionSmoothing>(out _))
                return;
        }
    }
}
