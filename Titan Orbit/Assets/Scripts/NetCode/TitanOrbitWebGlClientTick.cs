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
    /// Asks the next ClientWorld initialization tick to rebuild the WebSocket driver.
    /// Doing that from a menu coroutine races <c>NetworkStreamReceiveSystem</c>, which is already
    /// updating the driver on the player loop. A failed reset used to leave the browser socket
    /// open, so Play / Join did nothing until a full page refresh.
    /// </summary>
    public static class TitanOrbitWebGlRelayConnect
    {
        /// <summary>What the next initialization tick should do with the client driver.</summary>
        internal enum Op : byte
        {
            /// <summary>Nothing queued.</summary>
            None = 0,

            /// <summary>Rebuild the Relay driver and call Connect.</summary>
            Connect = 1,

            /// <summary>Close the old socket and install an idle driver. Used after leave / drop.</summary>
            IdleReset = 2,
        }

        /// <summary>One pending operation. A later request replaces an earlier one.</summary>
        static Op s_Pending;

        /// <summary>Queue a Relay connect. Replaces a queued idle reset — Join wins over a stale leave.</summary>
        public static void Request()
        {
            s_Pending = Op.Connect;
        }

        /// <summary>
        /// Queue a socket close with no new Connect. Used when leaving a match or after a drop,
        /// once the Relay allocation has already been cleared.
        /// </summary>
        public static void RequestIdleReset()
        {
            s_Pending = Op.IdleReset;
        }

        /// <summary>Takes the pending operation and clears it. <see cref="Op.None"/> when nothing was queued.</summary>
        internal static Op Consume()
        {
            Op op = s_Pending;
            s_Pending = Op.None;
            return op;
        }
    }

    /// <summary>
    /// Rebuilds the client WebSocket driver before simulation reads the socket.
    /// Runs at the end of initialization, ahead of <c>NetworkStreamReceiveSystem</c>.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(InitializationSystemGroup), OrderLast = true)]
    [UpdateBefore(typeof(NetworkStreamReceiveSystem))]
    public partial struct TitanOrbitWebGlRelayConnectSystem : ISystem
    {
        /// <summary>Failed rebuilds this visit. Stops a broken driver from retrying every frame.</summary>
        byte _attempts;

        /// <summary>
        /// Applies a queued leave-reset or Relay join. Drops stuck connection entities first —
        /// they are cleanup data, so destroying them in place does not free the socket.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            TitanOrbitWebGlRelayConnect.Op op = TitanOrbitWebGlRelayConnect.Consume();
            if (op == TitanOrbitWebGlRelayConnect.Op.None)
                return;

            bool connect = op == TitanOrbitWebGlRelayConnect.Op.Connect;
            if (connect && !TitanOrbitRelayState.TryGetClientRelay(out _))
            {
                Debug.LogError("[WebGLClient] Relay connect queued but no client allocation is stored.");
                _attempts = 0;
                return;
            }

            // --- Drop leftover connections, then swap the driver ---
            // Receive runs after this system. A dead WebSocket never reports Disconnected, so
            // the cleanup entity stays forever and ResetDriverStore refuses to run. Releasing
            // it here, then disposing the driver, closes the JavaScript socket. The next Join
            // can open a new one on the same page.
            int released = TitanOrbitSessionManager.ForceReleaseClientConnectionEntities(state.EntityManager);
            if (released > 0)
            {
                Debug.Log("[WebGLClient] Released " + released +
                          " stale connection(s) before rebuilding the WebSocket driver.");
            }

            if (!TitanOrbitSessionManager.ResetClientDriverIfNeeded())
            {
                Requeue(op);
                return;
            }

            if (!connect)
            {
                _attempts = 0;
                Debug.Log("[WebGLClient] Idle WebSocket driver rebuilt after leave / disconnect.");
                return;
            }

            World world = state.World;
            Entity connection = TitanOrbitSessionManager.ConnectRelayClient(world);
            if (connection == Entity.Null)
            {
                Debug.LogError("[WebGLClient] Relay driver rebuilt but Connect was skipped.");
                Requeue(op);
                return;
            }

            _attempts = 0;
            TitanOrbitRelayState.TryGetClientRelay(out var relay);
            Debug.Log("[WebGLClient] Relay connect issued endpoint=" + relay.Endpoint +
                      " isWebSocket=" + relay.IsWebSocket +
                      " isSecure=" + relay.IsSecure +
                      " connection=" + connection.Index);
        }

        /// <summary>
        /// Puts the same operation back for the next frame, up to a small cap.
        /// A fresh Play / Join click calls <see cref="TitanOrbitWebGlRelayConnect.Request"/> again.
        /// </summary>
        void Requeue(TitanOrbitWebGlRelayConnect.Op op)
        {
            _attempts++;
            if (_attempts >= 8)
            {
                _attempts = 0;
                Debug.LogError("[WebGLClient] Gave up rebuilding the WebSocket driver after repeated failures.");
                return;
            }

            if (op == TitanOrbitWebGlRelayConnect.Op.Connect)
                TitanOrbitWebGlRelayConnect.Request();
            else
                TitanOrbitWebGlRelayConnect.RequestIdleReset();
        }
    }
}
#endif
