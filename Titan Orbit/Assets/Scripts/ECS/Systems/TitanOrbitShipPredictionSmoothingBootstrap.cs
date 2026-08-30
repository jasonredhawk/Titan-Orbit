using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// One-shot client bootstrap for NetCode <see cref="GhostPredictionSmoothing"/>.
    /// <para>
    /// [TITAN-ORBIT] Do <b>not</b> register LocalTransform smoothing — it fought
    /// <c>ShipVisualSyncSystem</c> display coast and looked like blurry jitter (worse on P2).
    /// Display-only velocity chase owns local presentation; remotes keep NetCode interpolation.
    /// </para>
    /// World: ClientSimulation. Group: InitializationSystemGroup (last).
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(InitializationSystemGroup), OrderLast = true)]
    public partial struct TitanOrbitShipPredictionSmoothingBootstrap : ISystem
    {
        /// <summary>True after we successfully register (or decide we cannot).</summary>
        bool _done;

        /// <summary>
        /// Confirms we leave GhostPredictionSmoothing unregistered once the NetCode singleton exists.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            if (_done)
            {
                state.Enabled = false;
                return;
            }

            // [TITAN-ORBIT] Registering GhostPredictionSmoothing (blend 0.92) while
            // ShipVisualSyncSystem also smooths display caused blurry micro-jitter on the local
            // predicted ship (P2 especially). Remotes stay on NetCode interpolation only.
            // Display-only velocity chase owns presentation; leave LocalTransform unsmoothed.
            _done = true;
            state.Enabled = false;

            // Keep singleton lookup so we do not spin if NetCode creates it late.
            if (!SystemAPI.TryGetSingletonRW<GhostPredictionSmoothing>(out _))
                return;
        }
    }
}
