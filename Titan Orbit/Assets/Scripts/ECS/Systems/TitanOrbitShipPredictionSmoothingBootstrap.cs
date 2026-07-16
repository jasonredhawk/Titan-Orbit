using Unity.Entities;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Intentionally does <b>not</b> register a custom <see cref="GhostPredictionSmoothing"/> blend.
    /// [NETCODE] Full server correction + resim is the basic reconciliation path. A 0.45 lerp was
    /// leaving the hull in a hybrid pose (part predicted, part corrected), which felt like
    /// jump-forward-then-pull-back while command age was high. When the shared motor is fully
    /// deterministic we can re-enable soft correction here. World: ClientSimulation.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(InitializationSystemGroup), OrderLast = true)]
    public partial struct TitanOrbitShipPredictionSmoothingBootstrap : ISystem
    {
        // #region agent log
        bool _loggedOnce;
        // #endregion

        /// <summary>
        /// One-shot log that custom LocalTransform prediction smoothing is disabled for basics.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // #region agent log
            if (!_loggedOnce)
            {
                _loggedOnce = true;
                try
                {
                    string path = System.IO.Path.GetFullPath(
                        System.IO.Path.Combine(UnityEngine.Application.dataPath, "..", "..", "debug-6b87b4.log"));
                    long ts = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    string line =
                        "{\"sessionId\":\"6b87b4\",\"runId\":\"basics2\",\"hypothesisId\":\"H9\"," +
                        "\"location\":\"TitanOrbitShipPredictionSmoothingBootstrap.OnUpdate\"," +
                        "\"message\":\"GhostPredictionSmoothing NOT registered (full snap reconcile)\"," +
                        "\"data\":{\"smoothing\":\"disabled\"},\"timestamp\":" + ts + "}\n";
                    System.IO.File.AppendAllText(path, line);
                }
                catch { /* debug I/O only */ }
            }
            // #endregion

            state.Enabled = false;
        }
    }
}
