#if UNITY_WEBGL && !UNITY_EDITOR
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// [TITAN-ORBIT] Logs the first WebGL ClientWorld tick so a Chrome memory trap is not
    /// confused with <c>OnCreate</c> (those logs are in <see cref="TitanOrbitBootstrap"/>).
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(InitializationSystemGroup), OrderFirst = true)]
    public partial struct TitanOrbitWebGlUpdateProbeSystem : ISystem
    {
        byte _logged;

        /// <summary>Logs once at the start of the first ClientWorld update.</summary>
        public void OnUpdate(ref SystemState state)
        {
            if (_logged != 0)
                return;
            _logged = 1;
            Debug.Log("[WebGLClient] First Update begin. If Chrome halts before " +
                      "'[WebGLClient] First Update end', the trap is inside this ClientWorld tick.");
        }
    }

    /// <summary>
    /// Logs once immediately before predicted simulation, which contains the ship motor.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderFirst = true)]
    [UpdateBefore(typeof(PredictedSimulationSystemGroup))]
    public partial struct TitanOrbitWebGlBeforePredictedProbeSystem : ISystem
    {
        byte _logged;

        /// <summary>Logs once before the first predicted-simulation pass.</summary>
        public void OnUpdate(ref SystemState state)
        {
            if (_logged != 0)
                return;
            _logged = 1;
            Debug.Log("[WebGLClient] PredictedSimulation about to run.");
        }
    }

    /// <summary>
    /// Runs at the end of <see cref="SimulationSystemGroup"/>, after transforms and prediction.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderLast = true)]
    public partial struct TitanOrbitWebGlUpdateProbeEndSystem : ISystem
    {
        byte _logged;

        /// <summary>Logs once when the first simulation pass finishes.</summary>
        public void OnUpdate(ref SystemState state)
        {
            if (_logged != 0)
                return;
            _logged = 1;
            Debug.Log("[WebGLClient] First Update end (SimulationSystemGroup finished; Transform and PredictedSimulation ran).");
        }
    }

    /// <summary>
    /// Asks the next ClientWorld initialization tick to rebuild the Relay WebSocket driver and connect.
    /// Doing that from a menu coroutine races <c>NetworkStreamReceiveSystem</c>, which is already
    /// updating the driver on the player loop.
    /// </summary>
    public static class TitanOrbitWebGlRelayConnect
    {
        static byte s_Pending;

        /// <summary>Queue one Relay connect for the next initialization tick.</summary>
        public static void Request()
        {
            s_Pending = 1;
        }

        /// <summary>True when a connect is waiting for the initialization tick.</summary>
        internal static bool Consume()
        {
            if (s_Pending == 0)
                return false;
            s_Pending = 0;
            return true;
        }
    }

    /// <summary>
    /// Rebuilds the client Relay driver and calls Connect before simulation reads the socket.
    /// Runs at the end of initialization, ahead of <c>NetworkStreamReceiveSystem</c>.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(InitializationSystemGroup), OrderLast = true)]
    public partial struct TitanOrbitWebGlRelayConnectSystem : ISystem
    {
        /// <summary>Applies a queued Relay join once the previous driver update has finished.</summary>
        public void OnUpdate(ref SystemState state)
        {
            if (!TitanOrbitWebGlRelayConnect.Consume())
                return;

            if (!TitanOrbitRelayState.TryGetClientRelay(out var relay))
            {
                Debug.LogError("[WebGLClient] Relay connect queued but no client allocation is stored.");
                return;
            }

            using var existingQuery = state.EntityManager.CreateEntityQuery(typeof(NetworkStreamConnection));
            int existing = existingQuery.CalculateEntityCount();
            if (existing > 0)
            {
                Debug.Log("[WebGLClient] Relay connect skipped — NetworkStreamConnection already exists.");
                return;
            }

            World world = state.World;
            TitanOrbitSessionManager.ResetClientDriverIfNeeded();
            Entity connection = TitanOrbitSessionManager.ConnectRelayClient(world);
            Debug.Log("[WebGLClient] Relay connect issued endpoint=" + relay.Endpoint +
                      " isWebSocket=" + relay.IsWebSocket +
                      " isSecure=" + relay.IsSecure +
                      " connection=" + connection.Index);
        }
    }
}
#endif
