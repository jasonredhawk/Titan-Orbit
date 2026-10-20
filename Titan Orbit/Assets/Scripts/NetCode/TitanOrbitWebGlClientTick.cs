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

}
#endif

namespace TitanOrbit.NetCode
{
    using Unity.Entities;
    using Unity.NetCode;
    using UnityEngine;

    /// <summary>
    /// Asks the next ClientWorld initialization tick to rebuild the transport driver.
    /// Doing that from the Join async method races <c>NetworkStreamReceiveSystem</c>.
    /// After the first match, the old socket is still registered, so an inline
    /// <c>ResetDriverStore</c> refuses until that connection has actually disconnected.
    /// A later request replaces an earlier one — Join wins over a stale leave — but the
    /// init system still closes whatever socket is live before it attaches a new one.
    /// </summary>
    public static class TitanOrbitClientRelayConnect
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

        /// <summary>Set while the init system is closing a socket or rebuilding the driver.</summary>
        static byte s_Busy;

        /// <summary>True when a leave or join has not finished closing the old socket and rebuilding.</summary>
        public static bool HasPendingWork => s_Pending != Op.None || s_Busy != 0;

        /// <summary>Queue a Relay connect. Replaces a queued idle reset.</summary>
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
            // A Join that already queued Connect must not be overwritten by a stale leave
            // that resumes after the new allocation is stored.
            if (s_Pending == Op.Connect)
                return;
            s_Pending = Op.IdleReset;
        }

        /// <summary>Takes the pending operation and clears it. <see cref="Op.None"/> when nothing was queued.</summary>
        internal static Op Consume()
        {
            Op op = s_Pending;
            s_Pending = Op.None;
            return op;
        }

        /// <summary>Marks the init system as still closing or rebuilding. Leave waits on this.</summary>
        internal static void SetBusy(bool busy)
        {
            s_Busy = busy ? (byte)1 : (byte)0;
        }
    }

    /// <summary>
    /// Closes the live client socket, then rebuilds the driver, before simulation reads it.
    /// Runs at the end of initialization, ahead of <c>NetworkStreamReceiveSystem</c>,
    /// on every player (Editor, desktop, WebGL). Dedicated servers have no client world.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(InitializationSystemGroup), OrderLast = true)]
    [UpdateBefore(typeof(NetworkStreamReceiveSystem))]
    public partial struct TitanOrbitClientRelayConnectSystem : ISystem
    {
        /// <summary>Leave or join that is waiting for the old socket to finish closing.</summary>
        TitanOrbitClientRelayConnect.Op _held;

        /// <summary>Frames spent on this visit. Caps a driver that cannot be rebuilt.</summary>
        byte _attempts;

        /// <summary>Logged once per close so a slow Relay disconnect does not spam the console.</summary>
        byte _loggedWait;

        /// <summary>
        /// Disconnects a live socket and waits until receive drops the entity, then swaps the
        /// driver. Connect runs only after that. Destroying the entity first leaves the old
        /// Relay connection up on the server, and the new Connect is a second socket on the ship.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            TitanOrbitClientRelayConnect.Op incoming = TitanOrbitClientRelayConnect.Consume();
            if (incoming != TitanOrbitClientRelayConnect.Op.None)
            {
                _held = incoming;
                _attempts = 0;
                _loggedWait = 0;
            }

            if (_held == TitanOrbitClientRelayConnect.Op.None)
            {
                TitanOrbitClientRelayConnect.SetBusy(false);
                return;
            }

            TitanOrbitClientRelayConnect.SetBusy(true);

            bool connect = _held == TitanOrbitClientRelayConnect.Op.Connect;
            if (connect && !TitanOrbitRelayState.TryGetClientRelay(out _))
            {
                Debug.LogError("[TitanOrbitClient] Relay connect queued but no client allocation is stored.");
                _held = TitanOrbitClientRelayConnect.Op.IdleReset;
                connect = false;
            }

            var em = state.EntityManager;

            // --- Close the old socket before any driver swap ---
            // Receive runs after this system and is what deletes the connection entity.
            // Resetting the driver in this same update disposes the socket before that
            // close is processed, so the server keeps the first connection.
            int live = TitanOrbitSessionManager.DisconnectLiveClientConnections(em);
            if (live > 0)
            {
                if (_loggedWait == 0)
                {
                    _loggedWait = 1;
                    Debug.Log("[TitanOrbitClient] Disconnecting " + live +
                              " live socket(s) before rebuilding the driver.");
                }

                return;
            }

            int stale = TitanOrbitSessionManager.ReleaseStaleClientConnectionEntities(em);
            if (stale > 0)
            {
                Debug.Log("[TitanOrbitClient] Dropped " + stale +
                          " connection record(s) that no longer had a socket.");
                return;
            }

            if (!TitanOrbitSessionManager.ResetClientDriverIfNeeded())
            {
                _attempts++;
                if (_attempts >= 8)
                    GiveUp("Gave up rebuilding the driver after repeated failures.");
                return;
            }

            if (!connect)
            {
                Finish();
                Debug.Log("[TitanOrbitClient] Idle driver rebuilt after leave / disconnect.");
                return;
            }

            Entity connection = TitanOrbitSessionManager.ConnectRelayClient(state.World);
            if (connection == Entity.Null)
            {
                _attempts++;
                if (_attempts >= 8)
                    GiveUp("Relay driver rebuilt but Connect was skipped.");
                return;
            }

            Finish();
            TitanOrbitRelayState.TryGetClientRelay(out var relay);
            Debug.Log("[TitanOrbitClient] Relay connect issued endpoint=" + relay.Endpoint +
                      " isWebSocket=" + relay.IsWebSocket +
                      " isSecure=" + relay.IsSecure +
                      " connection=" + connection.Index);
        }

        /// <summary>Stops this visit so a later Join click can queue a fresh connect.</summary>
        void GiveUp(string message)
        {
            Debug.LogError("[TitanOrbitClient] " + message);
            Finish();
        }

        /// <summary>Clears the in-progress op so leave's wait can return.</summary>
        void Finish()
        {
            _held = TitanOrbitClientRelayConnect.Op.None;
            _attempts = 0;
            _loggedWait = 0;
            TitanOrbitClientRelayConnect.SetBusy(false);
        }
    }
}
