using TitanOrbit.Diagnostics;
using Unity.Entities;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// One-shot client bootstrap for NetCode <see cref="GhostPredictionSmoothing"/>.
    /// <para>
    /// basics42 (H47): do <b>not</b> register LocalTransform smoothing — it fought
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
        /// Registers LocalTransform smoothing once the NetCode singleton exists.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            if (_done)
            {
                state.Enabled = false;
                return;
            }

            // basics42 / H47: registering GhostPredictionSmoothing (blend 0.92) while
            // ShipVisualSyncSystem also smooths display caused blurry micro-jitter on the local
            // predicted ship (P2 especially). Remotes stay on NetCode interpolation only.
            // Display-only velocity chase owns presentation; leave LocalTransform unsmoothed.
            _done = true;
            state.Enabled = false;

            // #region agent log
            ShipFlightSmoothDebugLog.Write(
                "H47",
                "TitanOrbitShipPredictionSmoothingBootstrap.OnUpdate",
                "GhostPredictionSmoothing DISABLED (display owns smooth)",
                "{\"ok\":true,\"blend\":0,\"reason\":\"avoid double-smooth jitter\"}");
            // #endregion

            // Keep singleton lookup so we do not spin if NetCode creates it late.
            if (!SystemAPI.TryGetSingletonRW<GhostPredictionSmoothing>(out _))
                return;
        }
    }
}
