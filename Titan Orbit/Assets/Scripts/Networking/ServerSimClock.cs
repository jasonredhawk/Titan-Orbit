using System;
using Unity.Netcode;
using UnityEngine;

namespace TitanOrbit.Networking
{
    /// <summary>
    /// Global lockstep clock. Server stamps inputs at <see cref="ServerTick"/>; every peer simulates at
    /// <see cref="SimulationTick"/> (a short buffer behind) so inputs have time to arrive before sim.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public sealed class ServerSimClock : MonoBehaviour
    {
        public const float SimFixedDeltaTime = 0.02f;
        public const int SimTickRateHz = 50;
        public const int MinInputBufferTicks = 2;
        public const int MaxInputBufferTicks = 6;
        public const int DefaultInputBufferTicks = 3;

        public static ServerSimClock Instance { get; private set; }

        [SerializeField] private float heartbeatInterval = 0.1f;

        private uint _serverTick;
        private uint _authServerTick;
        private double _authLocalUnscaledTime;
        private float _heartbeatAccumulator;
        private bool _clockInitialized;

        private double _heartbeatArrivalMean;
        private double _heartbeatArrivalJitter;
        private bool _arrivalInitialized;
        private int _inputBufferTicks = DefaultInputBufferTicks;
        private const double ArrivalMeanBlend = 0.08;
        private const double JitterRelease = 0.04;

        public uint ServerTick
        {
            get
            {
                var nm = NetworkManager.Singleton;
                if (nm != null && nm.IsServer)
                    return _serverTick;
                if (!_clockInitialized)
                    return 0;
                double elapsed = Time.unscaledTimeAsDouble - _authLocalUnscaledTime;
                uint ticksSince = (uint)Math.Max(0, (long)Math.Floor(elapsed / SimFixedDeltaTime + 1e-6));
                return _authServerTick + ticksSince;
            }
        }

        public uint SimulationTick
        {
            get
            {
                if (!_clockInitialized) return 0;
                uint baseTick = ServerTick;
                return baseTick > (uint)_inputBufferTicks ? baseTick - (uint)_inputBufferTicks : 0;
            }
        }

        public int InputBufferTicks => _inputBufferTicks;
        public bool IsClockReady => _clockInitialized;
        public uint PhysicsStepId { get; private set; }

        public static double TickToSeconds(uint tick) => tick * SimFixedDeltaTime;
        public static uint SecondsToTick(float seconds) => (uint)Mathf.Max(0, Mathf.RoundToInt(seconds / SimFixedDeltaTime));

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        public void ResetForNetworkSession()
        {
            _serverTick = 0;
            _authServerTick = 0;
            _authLocalUnscaledTime = 0;
            _heartbeatAccumulator = 0f;
            _clockInitialized = false;
            _arrivalInitialized = false;
            _inputBufferTicks = DefaultInputBufferTicks;
            PhysicsStepId = 0;
        }

        private void FixedUpdate()
        {
            NetworkManager nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsListening)
                return;

            PhysicsStepId++;

            if (nm.IsServer)
            {
                _serverTick++;
                _clockInitialized = true;
                _heartbeatAccumulator += SimFixedDeltaTime;
                if (_heartbeatAccumulator >= heartbeatInterval)
                {
                    _heartbeatAccumulator = 0f;
                    double serverTime = nm.ServerTime.Time;
                    NetworkGameManager.Instance?.SendSimClockHeartbeat(_serverTick, serverTime);
                }
            }
        }

        public void RebaseFromAuthoritativeMotorTick(uint motorSimTick)
        {
            NetworkManager nm = NetworkManager.Singleton;
            if (nm == null || nm.IsServer || motorSimTick == 0)
                return;

            _authServerTick = motorSimTick + (uint)_inputBufferTicks;
            _authLocalUnscaledTime = Time.unscaledTimeAsDouble;
            _clockInitialized = true;
        }

        public void ApplyHeartbeat(uint serverTick, double serverTime)
        {
            NetworkManager nm = NetworkManager.Singleton;
            if (nm == null || nm.IsServer)
                return;

            double arrival = Time.unscaledTimeAsDouble;
            if (!_arrivalInitialized)
            {
                _heartbeatArrivalMean = arrival - serverTime;
                _heartbeatArrivalJitter = 0.0;
                _arrivalInitialized = true;
            }
            else
            {
                double transit = arrival - serverTime;
                double deviation = Math.Abs(transit - _heartbeatArrivalMean);
                _heartbeatArrivalMean += (transit - _heartbeatArrivalMean) * ArrivalMeanBlend;
                if (deviation > _heartbeatArrivalJitter)
                    _heartbeatArrivalJitter = deviation;
                else
                    _heartbeatArrivalJitter += (deviation - _heartbeatArrivalJitter) * JitterRelease;
            }

            int targetBuffer = MinInputBufferTicks + (int)Math.Round(_heartbeatArrivalJitter / SimFixedDeltaTime * 1.5);
            _inputBufferTicks = Mathf.Clamp(targetBuffer, MinInputBufferTicks, MaxInputBufferTicks);

            uint prevEst = ServerTick;
            if (_clockInitialized && serverTick < prevEst)
                return;

            _authServerTick = serverTick;
            _authLocalUnscaledTime = arrival;
            _clockInitialized = true;

            // #region agent log
            long error = (long)serverTick - (long)prevEst;
            if (error != 0)
            {
                TitanOrbit.Diagnostics.MotorDebugLog.Write("H4", "ServerSimClock:ApplyHeartbeat", "clock_auth",
                    $"{{\"error\":{error},\"prevEst\":{prevEst},\"authTick\":{serverTick},\"inputBuffer\":{_inputBufferTicks}}}", "post-fix12");
            }
            // #endregion
        }
    }
}
