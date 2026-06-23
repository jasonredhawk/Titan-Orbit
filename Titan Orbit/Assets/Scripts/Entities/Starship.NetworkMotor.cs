using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Input;
using TitanOrbit.Networking;
using TitanOrbit.Simulation;
using TitanOrbit.Audio;
using TitanOrbit.Diagnostics;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Dedicated-server authoritative motor with client-side prediction (local owner) and
    /// snapshot interpolation (remote ships) — standard arcade multiplayer (Starblast / Agar.io style).
    /// </summary>
    public partial class Starship
    {
        private const int MaxPendingInputCommands = 64;
        private const float MaxRemoteExtrapolationSeconds = 0.1f;

        private struct MotorRenderSample
        {
            public Vector3 Position;
            public Quaternion Rotation;
            public Vector3 Velocity;
        }

        private ShipMotorState _motorState;
        private uint _currentSimTick;
        private uint _nextInputSequence = 1;
        private ShipInputCommand _lastSentInput = ShipInputCommand.Default;
        private ShipInputCommand _ownerPredictInput = ShipInputCommand.Default;

        private ShipInputCommand _serverLatestInput = ShipInputCommand.Default;
        private readonly Queue<ShipInputCommand> _serverInputQueue = new Queue<ShipInputCommand>(MaxPendingInputCommands);
        private readonly Queue<ShipInputCommand> _ownerInputHistory = new Queue<ShipInputCommand>(MaxPendingInputCommands);
        private bool _motorSimInitialized;

        private MotorRenderSample _renderPrev;
        private MotorRenderSample _renderNext;
        private bool _renderSamplesValid;

        private Vector3 _remoteDisplayVelocity;
        private bool _remoteDisplayValid;
        private bool _clientPoseInitialized;
        private uint _lastBufferedSnapshotTick;

        private readonly ShipMotorSnapshotBuffer _remoteSnapshotBuffer = new ShipMotorSnapshotBuffer();

        // #region agent log
        private Vector3 _dbgLastDisplayPos;
        private bool _dbgHasLastDisplayPos;
        private int _dbgFixedLogCounter;
        private int _dbgLateLogCounter;
        // #endregion

        private uint _pendingOwnerReconcileTick;
        private uint _lastOwnerReconcileTick;
        private uint _lastReconciledAckSeq;

        private NetworkVariable<Vector3> netMotorPosition = new NetworkVariable<Vector3>(
            default,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        private NetworkVariable<Quaternion> netMotorRotation = new NetworkVariable<Quaternion>(
            Quaternion.identity,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        private NetworkVariable<Vector3> netMotorVelocity = new NetworkVariable<Vector3>(
            default,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        private NetworkVariable<uint> netMotorSimTick = new NetworkVariable<uint>(
            0,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        private NetworkVariable<Vector2> netAimWorldXZ = new NetworkVariable<Vector2>(
            default,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        private NetworkVariable<uint> lastProcessedInputSequence = new NetworkVariable<uint>(
            0,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        private static uint s_lastServerMotorPhysicsStep;
        private static readonly List<Starship> s_motorScratch = new List<Starship>(64);

        public uint LastProcessedInputSequence => lastProcessedInputSequence.Value;
        public uint CurrentSimTick => _currentSimTick;
        public bool IsThrustingForDisplay => IsOwner && !IsServer
            ? _ownerPredictInput.Thrust
            : moveForwardPressedNet.Value;

        public Vector3 GetSimPosition()
        {
            if (IsServer)
                return _motorState.Position;
            if (IsOwner && _renderSamplesValid)
                return SampleRenderPose(out _, out _);
            if (!IsOwner && _remoteDisplayValid)
                return transform.position;
            return netMotorPosition.Value;
        }

        public Vector3 GetSimVelocity()
        {
            if (IsServer)
                return _motorState.Velocity;
            if (IsOwner && _renderSamplesValid)
                return Vector3.Lerp(_renderPrev.Velocity, _renderNext.Velocity, GetRenderAlpha());
            if (!IsOwner && _remoteDisplayValid)
                return _remoteDisplayVelocity;
            return netMotorVelocity.Value;
        }

        public Quaternion GetSimRotation()
        {
            if (IsServer)
                return _motorState.Rotation;
            if (IsOwner && _renderSamplesValid)
                return Quaternion.Slerp(_renderPrev.Rotation, _renderNext.Rotation, GetRenderAlpha());
            if (!IsOwner && _remoteDisplayValid)
                return transform.rotation;
            return netMotorRotation.Value;
        }

        public float GetSimMass() => _motorState.Mass > 0f ? _motorState.Mass : EffectiveMass;

        private bool UsesInputSyncedMotor => true;

        private static float GetRenderAlpha()
        {
            float dt = Mathf.Max(Time.fixedDeltaTime, 0.001f);
            return Mathf.Clamp01((Time.time - Time.fixedTime) / dt);
        }

        /// <summary>Fractional sim tick for remote snapshot interpolation — keyed to buffered snapshots, not client clock.</summary>
        private float GetRemoteRenderTickF()
        {
            uint latest = _remoteSnapshotBuffer.LatestTick;
            if (latest == 0)
            {
                ServerSimClock clock = ServerSimClock.Instance;
                return clock != null && clock.IsClockReady
                    ? clock.SimulationTick - 1f + GetRenderAlpha()
                    : 0f;
            }

            if (_remoteSnapshotBuffer.Count < 2)
                return latest;

            return latest - 1f + GetRenderAlpha();
        }

        private Vector3 SampleRenderPose(out Quaternion rotation, out Vector3 velocity)
        {
            float t = GetRenderAlpha();
            rotation = Quaternion.Slerp(_renderPrev.Rotation, _renderNext.Rotation, t);
            velocity = Vector3.Lerp(_renderPrev.Velocity, _renderNext.Velocity, t);
            return Vector3.Lerp(_renderPrev.Position, _renderNext.Position, t);
        }

        private void CaptureRenderSampleFromMotorState()
        {
            _renderPrev = _renderNext;
            _renderNext = new MotorRenderSample
            {
                Position = _motorState.Position,
                Rotation = _motorState.Rotation,
                Velocity = _motorState.Velocity,
            };
            _renderSamplesValid = true;
        }

        private void SyncRenderSamplesFromMotorState()
        {
            var sample = new MotorRenderSample
            {
                Position = _motorState.Position,
                Rotation = _motorState.Rotation,
                Velocity = _motorState.Velocity,
            };
            _renderPrev = sample;
            _renderNext = sample;
            _renderSamplesValid = true;
        }

        private void InitializeMotorSimOnSpawn()
        {
            if (_motorSimInitialized) return;

            Vector3 pos = rb != null ? rb.position : transform.position;
            Quaternion rot = rb != null ? rb.rotation : transform.rotation;
            float mass = rb != null ? rb.mass : EffectiveMass;
            _motorState.ResetAt(pos, rot, mass);
            _currentSimTick = 0;
            _motorSimInitialized = true;
            ConfigureServerAuthoritativeNetworking();

            _renderPrev = _renderNext = new MotorRenderSample
            {
                Position = pos,
                Rotation = rot,
                Velocity = Vector3.zero,
            };
            _renderSamplesValid = true;

            if (IsClient && !IsServer)
            {
                netMotorSimTick.OnValueChanged += OnReplicatedMotorSimTickChanged;
                lastProcessedInputSequence.OnValueChanged += OnReplicatedInputAckChanged;
                ResetClientPoseFromNetwork();
            }
        }

        private void UnsubscribeMotorNetworkCallbacks()
        {
            if (!IsClient || IsServer) return;
            netMotorSimTick.OnValueChanged -= OnReplicatedMotorSimTickChanged;
            lastProcessedInputSequence.OnValueChanged -= OnReplicatedInputAckChanged;
        }

        private void OnReplicatedInputAckChanged(uint previous, uint current)
        {
            if (IsServer || !IsSpawned || !IsOwner || isDead.Value || gemMoonDocked.Value || current == 0)
                return;
            if (current <= _lastReconciledAckSeq)
                return;
            uint tick = netMotorSimTick.Value;
            if (tick > _pendingOwnerReconcileTick)
                _pendingOwnerReconcileTick = tick;
        }

        private void ConfigureServerAuthoritativeNetworking()
        {
            var networkRigidbody = GetComponent<Unity.Netcode.Components.NetworkRigidbody>();
            if (networkRigidbody != null)
                networkRigidbody.enabled = false;

            var networkTransform = GetComponent<Unity.Netcode.Components.NetworkTransform>();
            if (networkTransform != null)
                networkTransform.enabled = false;

            if (rb != null)
            {
                rb.isKinematic = true;
                rb.interpolation = RigidbodyInterpolation.None;
            }
        }

        private void OnReplicatedMotorSimTickChanged(uint previous, uint current)
        {
            if (IsServer || !IsSpawned || isDead.Value || gemMoonDocked.Value || current == 0)
                return;
            if (current == _lastBufferedSnapshotTick)
                return;
            _lastBufferedSnapshotTick = current;

            if (!IsOwner)
                BufferRemoteSnapshot(current);
        }

        private void TryProcessPendingOwnerReconcile()
        {
            if (_pendingOwnerReconcileTick == 0)
                return;
            if (_pendingOwnerReconcileTick <= _lastOwnerReconcileTick)
            {
                _pendingOwnerReconcileTick = 0;
                return;
            }

            uint tick = _pendingOwnerReconcileTick;
            _pendingOwnerReconcileTick = 0;
            if (ReconcileOwnerFromServer(tick))
                _lastOwnerReconcileTick = tick;
        }

        private void BufferRemoteSnapshot(uint tick)
        {
            _remoteSnapshotBuffer.Push(new ShipMotorSnapshot
            {
                Tick = tick,
                Position = netMotorPosition.Value,
                Rotation = netMotorRotation.Value,
                Velocity = netMotorVelocity.Value,
                Thrust = moveForwardPressedNet.Value,
                AimWorldXZ = netAimWorldXZ.Value,
            });
        }

        private bool ReconcileOwnerFromServer(uint serverTick)
        {
            Vector3 serverPos = netMotorPosition.Value;
            serverPos.y = FIXED_Y_POSITION;
            uint ackSeq = lastProcessedInputSequence.Value;

            if (ackSeq <= _lastReconciledAckSeq)
                return false;

            while (_ownerInputHistory.Count > 0 && _ownerInputHistory.Peek().Sequence <= ackSeq)
                _ownerInputHistory.Dequeue();

            // #region agent log
            Vector3 prePos = _motorState.Position;
            uint preTick = _currentSimTick;
            float preSnapErr = Vector3.Distance(
                new Vector3(serverPos.x, 0f, serverPos.z),
                new Vector3(prePos.x, 0f, prePos.z));
            int replayCount = 0;
            // #endregion

            ShipMotorSimulator.SnapState(
                ref _motorState,
                serverPos,
                netMotorRotation.Value,
                netMotorVelocity.Value,
                FIXED_Y_POSITION);
            _currentSimTick = serverTick;

            uint replayTick = serverTick;
            foreach (ShipInputCommand input in _ownerInputHistory)
            {
                if (input.Sequence <= ackSeq)
                    continue;
                replayTick++;
                replayCount++;
                StepMotorWithInput(input, replayTick, fireWeapons: false);
            }

            _lastReconciledAckSeq = ackSeq;
            SyncRenderSamplesFromMotorState();

            // #region agent log
            ServerSimClock dbgClock = ServerSimClock.Instance;
            MotorDebugLog.Write("H1", "Starship.NetworkMotor:ReconcileOwnerFromServer", "owner_reconcile",
                $"{{\"preSnapErr\":{preSnapErr:F4},\"replayCount\":{replayCount},\"preTick\":{preTick},\"postTick\":{_currentSimTick},\"serverTick\":{serverTick},\"ackSeq\":{ackSeq},\"simTick\":{(dbgClock != null ? dbgClock.SimulationTick : 0)}}}", "post-fix7");
            // #endregion
            return true;
        }

        /// <summary>Server-only: assign inputs, step motor, replicate state.</summary>
        public static void ServerTickAllShipMotors(uint serverTick)
        {
            NetworkManager nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return;

            ServerSimClock clock = ServerSimClock.Instance;
            if (clock == null) return;
            uint step = clock.PhysicsStepId;
            if (step == s_lastServerMotorPhysicsStep) return;
            s_lastServerMotorPhysicsStep = step;

            s_motorScratch.Clear();
            for (int i = 0; i < AllStarships.Count; i++)
            {
                Starship ship = AllStarships[i];
                if (ship == null || !ship.IsSpawned || ship.isDead.Value || ship.gemMoonDocked.Value)
                    continue;
                ship.InitializeMotorSimOnSpawn();
                s_motorScratch.Add(ship);
            }

            if (s_motorScratch.Count == 0) return;

            s_motorScratch.Sort((a, b) => a.GetInstanceID().CompareTo(b.GetInstanceID()));

            for (int i = 0; i < s_motorScratch.Count; i++)
                s_motorScratch[i].ServerAssignInputForTick(serverTick);

            for (int i = 0; i < s_motorScratch.Count; i++)
                s_motorScratch[i].StepMotorWithInput(s_motorScratch[i]._serverLatestInput, serverTick, fireWeapons: true);

            for (int i = 0; i < s_motorScratch.Count; i++)
            {
                Starship ship = s_motorScratch[i];
                if (ship._currentSimTick == 0) continue;
                ship.ResolveSimCollisionsForTick(ship._currentSimTick);
            }

            for (int i = 0; i < s_motorScratch.Count; i++)
            {
                Starship ship = s_motorScratch[i];
                ship.PublishMotorStateToNetwork();
                ship.ApplyMotorSimStateToRigidbody();
                if (ship.IsClient)
                    ship.CaptureRenderSampleFromMotorState();
            }
        }

        /// <summary>Owner client: predict motor immediately from local input (no RTT wait).</summary>
        internal void ClientPredictMotorFixedStep()
        {
            if (!IsClient || IsServer || !IsOwner || isDead.Value || gemMoonDocked.Value)
                return;

            InitializeMotorSimOnSpawn();
            TryProcessPendingOwnerReconcile();
            SyncSimMassFromShip();

            ShipInputCommand input = _lastSentInput;
            if (input.Sequence == 0)
            {
                input = BuildInputCommandFromLocalControls();
                input.Sequence = 1;
                input.SpaceBrakes = (inputHandler as PlayerInputHandler)?.SpaceBrakesEnabled ?? true;
            }
            _ownerPredictInput = input;

            uint nextTick = _currentSimTick + 1;
            if (_currentSimTick == 0)
            {
                ServerSimClock clock = ServerSimClock.Instance;
                if (clock != null && clock.IsClockReady && clock.SimulationTick > 0)
                    nextTick = clock.SimulationTick;
            }

            StepMotorWithInput(input, nextTick, fireWeapons: false);
            CaptureRenderSampleFromMotorState();

            // #region agent log
            _dbgFixedLogCounter++;
            if (_dbgFixedLogCounter % 25 == 0)
            {
                ServerSimClock dbgClock = ServerSimClock.Instance;
                MotorDebugLog.Write("H2", "Starship.NetworkMotor:ClientPredictMotorFixedStep", "owner_predict",
                    $"{{\"tick\":{_currentSimTick},\"simTick\":{(dbgClock != null ? dbgClock.SimulationTick : 0)},\"serverClock\":{(dbgClock != null ? dbgClock.ServerTick : 0)},\"tickLead\":{(dbgClock != null ? (int)_currentSimTick - (int)dbgClock.SimulationTick : 0)},\"speed\":{_motorState.Velocity.magnitude:F3},\"thrust\":{(input.Thrust ? 1 : 0)}}}", "post-fix7");
            }
            // #endregion
        }

        private void StepMotorWithInput(ShipInputCommand input, uint simTick, bool fireWeapons)
        {
            if (input.Sequence == 0)
            {
                input = ShipInputCommand.Default;
                input.Sequence = 1;
                input.SpaceBrakes = true;
            }

            Vector3 preVel = _motorState.Velocity;
            ShipMotorTickParams p = BuildMotorTickParams(input, simTick);
            ShipMotorSimulator.Step(
                ref _motorState,
                in p,
                input.AimWorldXZ,
                input.Thrust,
                input.SpaceBrakes);

            _lastFixedPlayPlaneVelocity = preVel;
            _currentSimTick = simTick;
            _motorState.LastSimTick = simTick;

            if (fireWeapons && input.Fire)
                ProcessSimTickWeaponFire(simTick);
        }

        private void PublishMotorStateToNetwork()
        {
            Vector3 pos = _motorState.Position;
            pos.y = FIXED_Y_POSITION;
            netMotorSimTick.Value = _currentSimTick;
            netMotorPosition.Value = pos;
            netMotorRotation.Value = _motorState.Rotation;
            netMotorVelocity.Value = _motorState.Velocity;
            netAimWorldXZ.Value = _serverLatestInput.AimWorldXZ;
        }

        private void ResolveSimCollisionsForTick(uint tick)
        {
            if (IsAwaitingTeamSelection) return;

            Vector3 preVel = _lastFixedPlayPlaneVelocity;
            float shipR = GetShipCollisionRadiusXZ();
            float restitution = GetEffectiveAsteroidRestitution();

            if (ShipCollisionSimulator.TryResolveAsteroidCollision(
                    ref _motorState,
                    shipR,
                    restitution,
                    GetRammingImpactMass(),
                    ServerSimClock.SimFixedDeltaTime,
                    preVel,
                    out ShipCollisionResult asteroidHit))
            {
                HandleAsteroidSimCollision(asteroidHit, tick);
            }

            if (ShipCollisionSimulator.TryResolveShipShipCollision(
                    ref _motorState,
                    this,
                    shipR,
                    GetShipShipRestitution(),
                    ServerSimClock.SimFixedDeltaTime,
                    out ShipCollisionResult shipHit))
            {
                HandleShipSimCollision(shipHit, tick);
            }
        }

        /// <summary>Writes authoritative sim state to rb (dedicated server only).</summary>
        private void ApplyMotorSimStateToRigidbody()
        {
            if (rb == null || IsClient) return;
            Vector3 pos = _motorState.Position;
            pos.y = FIXED_Y_POSITION;
            rb.MovePosition(pos);
            rb.MoveRotation(_motorState.Rotation);
            rb.linearVelocity = _motorState.Velocity;
            currentVelocity = _motorState.Velocity;
            transform.SetPositionAndRotation(pos, _motorState.Rotation);
        }

        private void ApplyClientMotorDisplayPose(Vector3 pos, Quaternion rot, Vector3 vel)
        {
            pos.y = FIXED_Y_POSITION;
            transform.SetPositionAndRotation(pos, rot);
            if (rb == null) return;
            rb.MovePosition(pos);
            rb.MoveRotation(rot);
            rb.linearVelocity = vel;
            currentVelocity = vel;
        }

        /// <summary>Aligns weapon/collision transforms with current sim or display pose before gameplay queries.</summary>
        internal void SyncTransformForGameplayQuery()
        {
            if (IsClient)
            {
                if (IsOwner && _renderSamplesValid)
                {
                    Vector3 pos = SampleRenderPose(out Quaternion rot, out Vector3 vel);
                    ApplyClientMotorDisplayPose(pos, rot, vel);
                }
                else
                {
                    Vector3 pos = _motorState.Position;
                    pos.y = FIXED_Y_POSITION;
                    ApplyClientMotorDisplayPose(pos, _motorState.Rotation, _motorState.Velocity);
                }
                return;
            }

            ApplyMotorSimStateToRigidbody();
        }

        internal void ClientApplyMotorPoseSmoothing()
        {
            if (!IsClient || rb == null || isDead.Value || gemMoonDocked.Value)
                return;

            if (IsOwner)
            {
                ApplyOwnerPredictedMotorVisuals();
                return;
            }

            ApplyRemoteMotorSnapshotVisuals();
        }

        private void ApplyOwnerPredictedMotorVisuals()
        {
            if (!_renderSamplesValid)
            {
                Vector3 pos = _motorState.Position;
                pos.y = FIXED_Y_POSITION;
                ApplyClientMotorDisplayPose(pos, _motorState.Rotation, _motorState.Velocity);
                return;
            }

            ApplyClientMotorDisplayPose(
                SampleRenderPose(out Quaternion rot, out Vector3 vel),
                rot,
                vel);

            // #region agent log
            _dbgLateLogCounter++;
            if (_dbgLateLogCounter % 12 == 0)
            {
                Vector3 displayPos = transform.position;
                float alpha = GetRenderAlpha();
                float renderSpan = Vector3.Distance(_renderPrev.Position, _renderNext.Position);
                float frameDelta = _dbgHasLastDisplayPos ? Vector3.Distance(displayPos, _dbgLastDisplayPos) : 0f;
                _dbgLastDisplayPos = displayPos;
                _dbgHasLastDisplayPos = true;
                MotorDebugLog.Write("H5", "Starship.NetworkMotor:ApplyOwnerPredictedMotorVisuals", "owner_display",
                    $"{{\"alpha\":{alpha:F3},\"renderSpan\":{renderSpan:F4},\"frameDelta\":{frameDelta:F4},\"speed\":{vel.magnitude:F3}}}", "post-fix7");
            }
            // #endregion
        }

        private void ApplyRemoteMotorSnapshotVisuals()
        {
            if (_remoteSnapshotBuffer.Count == 0)
            {
                ServerSimClock clock = ServerSimClock.Instance;
                if (clock == null || !clock.IsClockReady)
                {
                    if (!_clientPoseInitialized)
                        ResetClientPoseFromNetwork();
                    return;
                }
            }

            float renderTickF = GetRemoteRenderTickF();
            uint latestTick = _remoteSnapshotBuffer.LatestTick;
            bool extrapolating = latestTick > 0 && renderTickF > latestTick;
            ServerSimClock dbgClock = ServerSimClock.Instance;

            if (_remoteSnapshotBuffer.TrySample(
                    renderTickF,
                    ServerSimClock.SimFixedDeltaTime,
                    MaxRemoteExtrapolationSeconds,
                    out Vector3 pos,
                    out Quaternion rot,
                    out Vector3 vel))
            {
                ApplyClientMotorDisplayPose(pos, rot, vel);
                _remoteDisplayVelocity = vel;
                _remoteDisplayValid = true;
                _clientPoseInitialized = true;

                // #region agent log
                _dbgLateLogCounter++;
                if (_dbgLateLogCounter % 12 == 0)
                {
                    float frameDelta = _dbgHasLastDisplayPos ? Vector3.Distance(pos, _dbgLastDisplayPos) : 0f;
                    _dbgLastDisplayPos = pos;
                    _dbgHasLastDisplayPos = true;
                    MotorDebugLog.Write("H3", "Starship.NetworkMotor:ApplyRemoteMotorSnapshotVisuals", "remote_display",
                        $"{{\"renderTickF\":{renderTickF:F3},\"latestTick\":{latestTick},\"simTick\":{(dbgClock != null ? dbgClock.SimulationTick : 0)},\"bufCount\":{_remoteSnapshotBuffer.Count},\"extrapolating\":{(extrapolating ? 1 : 0)},\"frameDelta\":{frameDelta:F4},\"speed\":{vel.magnitude:F3}}}", "post-fix7");
                }
                // #endregion
                return;
            }

            if (!_clientPoseInitialized)
                ResetClientPoseFromNetwork();
        }

        private void ResetClientPoseFromNetwork()
        {
            Vector3 pos = netMotorPosition.Value;
            if (pos.sqrMagnitude < 0.01f)
                pos = transform.position;
            pos.y = FIXED_Y_POSITION;
            Quaternion rot = netMotorRotation.Value;
            Vector3 vel = netMotorVelocity.Value;
            transform.SetPositionAndRotation(pos, rot);
            _clientPoseInitialized = true;
            _remoteDisplayValid = true;
            _remoteDisplayVelocity = vel;
            if (rb != null)
            {
                rb.MovePosition(pos);
                rb.MoveRotation(rot);
                rb.linearVelocity = vel;
            }
            currentVelocity = vel;

            if (!IsOwner && netMotorSimTick.Value > 0)
                BufferRemoteSnapshot(netMotorSimTick.Value);
        }

        public Vector3 GetGameplayShipCenterWorld() => GetSimPosition();

        public Vector3 GetDisplayWorldPosition() => transform.position;

        /// <summary>
        /// Interpolated display position for camera/toroidal reference — matches rendered ship, not stale FixedUpdate snap.
        /// </summary>
        public Vector3 GetDisplayMotorWorldPosition()
        {
            if (gemMoonDocked.Value)
                return transform.position;

            if (IsServer && !IsClient)
                return _motorState.Position;

            if (IsClient && IsOwner && _renderSamplesValid)
            {
                Vector3 pos = SampleRenderPose(out _, out _);
                pos.y = FIXED_Y_POSITION;
                return pos;
            }

            if (IsClient && !IsOwner)
            {
                if (_remoteSnapshotBuffer.Count > 0
                    && _remoteSnapshotBuffer.TrySample(
                        GetRemoteRenderTickF(),
                        ServerSimClock.SimFixedDeltaTime,
                        MaxRemoteExtrapolationSeconds,
                        out Vector3 pos,
                        out _,
                        out _))
                {
                    pos.y = FIXED_Y_POSITION;
                    return pos;
                }
            }

            return transform.position;
        }

        private void SyncMotorRigidbodyToTransform()
        {
            if (IsServer && !IsClient)
                ApplyMotorSimStateToRigidbody();
            else if (IsClient)
                ClientApplyMotorPoseSmoothing();
            else
                ResetClientPoseFromNetwork();
        }

        private void SyncSimMassFromShip()
        {
            float mass = Mathf.Max(0.5f, EffectiveMass);
            _motorState.Mass = mass;
            if (rb != null)
                rb.mass = mass;
        }

        private void TickNetworkInputSender()
        {
            if (!IsOwner || IsAwaitingTeamSelection || isDead.Value) return;
            if (inputHandler == null) return;

            ShipInputCommand cmd = BuildInputCommandFromLocalControls();
            cmd.Sequence = _nextInputSequence++;
            if (_nextInputSequence == 0) _nextInputSequence = 1;
            ServerSimClock clock = ServerSimClock.Instance;
            cmd.ClientTick = clock != null ? clock.ServerTick : 0;
            _lastSentInput = cmd;

            _ownerInputHistory.Enqueue(cmd);
            while (_ownerInputHistory.Count > MaxPendingInputCommands)
                _ownerInputHistory.Dequeue();

            SubmitShipInputServerRpc(cmd);
            SyncFireIntentFromInput(cmd);
        }

        private ShipInputCommand BuildInputCommandFromLocalControls()
        {
            Vector2 aimXZ = Vector2.zero;
            UnityEngine.Camera cam = UnityEngine.Camera.main;
            if (cam != null && inputHandler != null)
            {
                Vector3 aimWorld = inputHandler.GetMouseWorldPosition(cam);
                aimXZ = new Vector2(aimWorld.x, aimWorld.z);
            }
            else
            {
                Vector3 fwd = GetSimRotation() * Vector3.forward;
                fwd.y = 0f;
                if (fwd.sqrMagnitude > 0.01f)
                {
                    Vector3 pt = GetSimPosition() + fwd.normalized * 10f;
                    aimXZ = new Vector2(pt.x, pt.z);
                }
            }

            bool uiBlocksShot = IsPointerOverUI();
            MobileInputHandler mobileHud = MobileInputHandler.Resolve();
            if (mobileHud != null && (mobileHud.ShootButtonPressed
                || (Application.isMobilePlatform && inputHandler.ShootPressed)))
                uiBlocksShot = false;

            bool canFire = !isDead.Value
                && !IsBulletElectricShockDisabled
                && !gemMoonDocked.Value
                && bulletConfig != null
                && bulletConfig.cannons != null
                && bulletConfig.cannons.Count > 0;

            return new ShipInputCommand
            {
                AimWorldXZ = aimXZ,
                Thrust = inputHandler.MoveForwardPressed,
                Fire = inputHandler.ShootPressed && !uiBlocksShot && canFire,
                SpaceBrakes = (inputHandler as PlayerInputHandler)?.SpaceBrakesEnabled ?? true,
            };
        }

        [ServerRpc]
        private void SubmitShipInputServerRpc(ShipInputCommand input)
        {
            _serverInputQueue.Enqueue(input);
            while (_serverInputQueue.Count > MaxPendingInputCommands)
                _serverInputQueue.Dequeue();

            if (input.Sequence >= _serverLatestInput.Sequence)
                _serverLatestInput = input;

            moveForwardPressedNet.Value = input.Thrust;
            shootPressedNet.Value = input.Fire;
            wantToFire.Value = input.Fire;
            netAimWorldXZ.Value = input.AimWorldXZ;
        }

        private void ServerAssignInputForTick(uint serverTick)
        {
            if (!IsServer) return;

            // One input per sim tick — matches client replay semantics (one command per tick).
            if (_serverInputQueue.Count > 0)
                _serverLatestInput = _serverInputQueue.Dequeue();

            ShipInputCommand latest = _serverLatestInput;
            if (latest.Sequence == 0)
            {
                latest = ShipInputCommand.Default;
                latest.Sequence = 1;
                latest.SpaceBrakes = true;
                _serverLatestInput = latest;
            }

            latest.ServerTick = serverTick;
            lastProcessedInputSequence.Value = latest.Sequence;
            moveForwardPressedNet.Value = latest.Thrust;
            netAimWorldXZ.Value = latest.AimWorldXZ;
        }

        private void SyncFireIntentFromInput(ShipInputCommand cmd)
        {
            if (cmd.Fire != localWantToFireSent)
            {
                localWantToFireSent = cmd.Fire;
                if (cmd.Fire)
                {
                    BeginOwnerWeaponFiringSession();
                    SetWantToFireServerRpc(true);
                }
                else
                {
                    EndOwnerWeaponFiringSession();
                    SetWantToFireServerRpc(false);
                }
            }
        }

        private void SnapMotorSimAfterSpawn(Vector3 position, Quaternion rotation, Vector3 velocity, float mass)
        {
            ShipMotorSimulator.SnapState(ref _motorState, position, rotation, velocity, FIXED_Y_POSITION);
            _motorState.Mass = Mathf.Max(0.5f, mass);
            ServerSimClock clock = ServerSimClock.Instance;
            _currentSimTick = clock != null ? clock.SimulationTick : 0;
            _serverLatestInput = ShipInputCommand.Default;
            _serverInputQueue.Clear();
            _ownerInputHistory.Clear();
            _pendingOwnerReconcileTick = 0;
            _lastOwnerReconcileTick = 0;
            _lastReconciledAckSeq = 0;
            _lastSentInput = ShipInputCommand.Default;
            _nextInputSequence = 1;
            _remoteSnapshotBuffer.Clear();
            _lastBufferedSnapshotTick = 0;

            _renderPrev = _renderNext = new MotorRenderSample
            {
                Position = position,
                Rotation = rotation,
                Velocity = velocity,
            };
            _renderSamplesValid = true;

            if (IsServer)
            {
                PublishMotorStateToNetwork();
                ApplyMotorSimStateToRigidbody();
            }
        }

        [ClientRpc]
        private void BroadcastSnapMotorSimClientRpc(Vector3 position, Vector3 velocity, Quaternion rotation, float mass)
        {
            if (IsServer) return;
            SnapClientMotorPose(position, rotation, velocity);
        }

        private void SnapClientMotorPose(Vector3 position, Quaternion rotation, Vector3 velocity)
        {
            Vector3 pos = position;
            pos.y = FIXED_Y_POSITION;
            ShipMotorSimulator.SnapState(ref _motorState, pos, rotation, velocity, FIXED_Y_POSITION);
            _ownerInputHistory.Clear();
            _pendingOwnerReconcileTick = 0;
            _lastOwnerReconcileTick = 0;
            _lastReconciledAckSeq = 0;
            _renderPrev = _renderNext = new MotorRenderSample
            {
                Position = pos,
                Rotation = rotation,
                Velocity = velocity,
            };
            _renderSamplesValid = true;
            transform.SetPositionAndRotation(pos, rotation);
            _clientPoseInitialized = true;
            _remoteDisplayValid = true;
            _remoteDisplayVelocity = velocity;
            if (rb != null)
            {
                rb.MovePosition(pos);
                rb.MoveRotation(rotation);
                rb.linearVelocity = velocity;
            }
            currentVelocity = velocity;
        }

        private void ServerAssignGemMoonUndockGraceSimTick()
        {
            ServerSimClock clock = ServerSimClock.Instance;
            if (clock == null || !clock.IsClockReady) return;
            float graceSeconds = Mathf.Max(0.05f, gemMoonTransitionDurationSeconds);
            gemMoonUndockGraceEndSimTick = clock.ServerTick + ServerSimClock.SecondsToTick(graceSeconds);
            SyncGemMoonUndockGraceEndSimTickClientRpc(gemMoonUndockGraceEndSimTick);
        }

        [ClientRpc]
        private void SyncGemMoonUndockGraceEndSimTickClientRpc(uint endSimTick)
        {
            if (IsServer) return;
            gemMoonUndockGraceEndSimTick = endSimTick;
        }

        private ShipMotorTickParams BuildMotorTickParams(ShipInputCommand input, uint simTick)
        {
            ApplyMotorInputToMoveDirectionFromCommand(input);

            bool inOrbitRing = currentOrbitPlanet != null && IsShipInPlanetOrbitRing(currentOrbitPlanet);
            bool useOrbit = inOrbitRing && !input.Thrust && !IsInsideFriendlyGemMoonOrbitZone();

            var p = new ShipMotorTickParams
            {
                FixedDeltaTime = ServerSimClock.SimFixedDeltaTime,
                EngineThrust = EffectiveEngineThrust,
                MaxSpeed = EffectiveMaxSpeed,
                RotationSpeedDegPerSec = EffectiveRotationSpeed,
                BrakeDeceleration = brakeDeceleration,
                RecoilDecayPerSecond = recoilDecayPerSecond,
                ElectricShockDisabled = IsBulletElectricShockDisabled,
                TheatricalRotationLocked = false,
                UseOrbit = useOrbit,
                FixedY = FIXED_Y_POSITION,
            };

            if (useOrbit)
                BuildOrbitMotorParams(ref p, simTick);

            return p;
        }

        private void BuildOrbitMotorParams(ref ShipMotorTickParams p, uint simTick)
        {
            if (currentOrbitPlanet == null) return;

            Vector3 planetPos = currentOrbitPlanet.GetOrbitGameplayCenterWorld();
            Vector3 shipPos = _motorState.Position;
            shipPos.y = 0f;
            float dist = TitanOrbit.Generation.ToroidalMap.ToroidalDistance(shipPos, planetPos);
            if (dist < 0.01f) return;

            Vector3 toShip = TitanOrbit.Generation.ToroidalMap.ShortestWorldOffsetXZ(planetPos, shipPos);
            float innerWorld = currentOrbitPlanet.PlanetSize * currentOrbitPlanet.GetOrbitRingInnerRadiusLocal();
            float outerWorld = currentOrbitPlanet.PlanetSize * currentOrbitPlanet.GetOrbitRingOuterRadiusLocal();
            bool inOrbitRing = dist >= innerWorld && dist <= outerWorld;

            bool inUndockGrace = !gemMoonDocked.Value && gemMoonUndockGraceEndSimTick > 0 && simTick < gemMoonUndockGraceEndSimTick;
            float graceRemaining = inUndockGrace
                ? (gemMoonUndockGraceEndSimTick - simTick) * ServerSimClock.SimFixedDeltaTime
                : 0f;
            if (!inOrbitRing && !inUndockGrace) return;

            float centerWorld = currentOrbitPlanet.PlanetSize * currentOrbitPlanet.GetOrbitRingCenterRadiusLocal();
            Vector3 radial = toShip / dist;
            float targetSpeed = GetOrbitTargetSpeed(currentOrbitPlanet, centerWorld, innerWorld, outerWorld);
            Vector3 tangent = new Vector3(radial.z, 0f, -radial.x);

            Vector3 radialCorrection = Vector3.zero;
            if (!inUndockGrace && inOrbitRing)
            {
                float radiusError = dist - centerWorld;
                if (Mathf.Abs(radiusError) > 0.02f)
                    radialCorrection -= radial * radiusError * orbitRadiusPullStrength;
            }

            Vector3 orbitTangentVelocity = tangent * targetSpeed + radialCorrection;
            Vector3 desiredOrbitVelocity;
            float transitionDur = Mathf.Max(0.05f, gemMoonTransitionDurationSeconds);
            if (inUndockGrace && transitionDur > 0.001f)
            {
                float w = Mathf.Clamp01(graceRemaining / transitionDur);
                Vector3 flat = TitanOrbit.Generation.ToroidalMap.ShortestWorldOffsetXZ(gemMoonUndockCachedMoonPos, _motorState.Position);
                Vector3 outwardDir = flat.sqrMagnitude > 0.0001f ? flat.normalized : tangent;
                Vector3 outwardVel = outwardDir * (gemMoonUndockOutwardSpeed * w);
                float handoff = 1f - w;
                desiredOrbitVelocity = Vector3.Lerp(outwardVel, orbitTangentVelocity, Mathf.SmoothStep(0f, 1f, handoff));
            }
            else
                desiredOrbitVelocity = orbitTangentVelocity;

            float mass = Mathf.Max(0.5f, _motorState.Mass);
            float gravityFactor = GetOrbitGravityFactor(currentOrbitPlanet, dist, innerWorld, outerWorld);
            float massFactor = Mathf.Sqrt(mass);
            float alignRate = (orbitCaptureResponsiveness * gravityFactor) / massFactor;
            if (inUndockGrace && transitionDur > 0.001f)
            {
                float fade = Mathf.Clamp01(graceRemaining / transitionDur);
                float ease = Mathf.Lerp(gemMoonUndockOrbitCaptureEase, 1f, 1f - fade);
                alignRate *= ease;
            }

            p.OrbitDesiredVelocity = desiredOrbitVelocity;
            p.OrbitAlignRate = alignRate;
        }

        private void ApplyMotorInputToMoveDirectionFromCommand(ShipInputCommand input)
        {
            if (IsBulletElectricShockDisabled || !input.Thrust)
            {
                moveDirection = Vector3.zero;
                return;
            }

            Vector3 fwd = _motorState.Rotation * Vector3.forward;
            fwd.y = 0f;
            moveDirection = fwd.sqrMagnitude > 0.01f ? fwd.normalized : Vector3.zero;
        }

        private void HandleAsteroidSimCollision(ShipCollisionResult hit, uint tick)
        {
            float asteroidCollisionPitch = Mathf.Lerp(0.7f, 1.25f, Mathf.InverseLerp(25f, 1200f, hit.ImpactForceNewtons));
            if (AudioManager.Instance != null)
                AudioManager.Instance.PlayAsteroidCollisionSound(asteroidCollisionPitch);

            if (hit.ImpactForceNewtons >= GetCollisionVfxAsteroidMinImpactForce())
            {
                float sev = ComputeCollisionVfxSeverityFromImpactForce(hit.ImpactForceNewtons);
                TrySpawnWeaponCollisionImpactVfx(hit.ImpactPoint, hit.SurfaceNormal, sev, asteroidCollisionPitch, RamGrindImpactVfxScaleFactor);
            }

            if (hit.Asteroid != null)
                MarkAsteroidRamContact(hit.Asteroid);

            if (!IsServer) return;

            float inboundSpeed = hit.RelativeSpeed;
            ComputeRamImpactDamage(inboundSpeed, out float asteroidCollisionDamage, out float shipCollisionDamage);

            if (shipCollisionDamage > 0.0001f)
            {
                float expulsionIntensity = ComputeRamImpactGemExpulsionIntensity(hit.ImpactForceNewtons, shipCollisionDamage);
                ApplyShipRamDamage(shipCollisionDamage, expulsionIntensity, gemExpulsionPerHullDamage: 1f);
            }

            if (hit.Asteroid != null && asteroidCollisionDamage > 0.0001f)
            {
                ulong attackerShipId = NetworkObject != null ? NetworkObjectId : 0ul;
                ApplyAsteroidRamDamage(hit.Asteroid, asteroidCollisionDamage, attackerShipId);
                SpawnAsteroidCollisionFeedback(hit.ImpactPoint, hit.Asteroid, asteroidCollisionDamage,
                    hit.ImpactForceNewtons >= asteroidImpactForcePopupMin ? hit.ImpactForceNewtons : (float?)null);
            }
        }

        private void HandleShipSimCollision(ShipCollisionResult hit, uint tick)
        {
            if (hit.RelativeSpeed >= 2f && AudioManager.Instance != null)
                AudioManager.Instance.PlayShipCollisionSound(Mathf.Lerp(0.8f, 1.35f, Mathf.InverseLerp(2f, 35f, hit.RelativeSpeed)));

            if (hit.RelativeSpeed >= GetCollisionVfxShipMinRelativeSpeed())
            {
                float sev = ComputeCollisionVfxSeverityFromRelativeSpeed(hit.RelativeSpeed);
                TrySpawnWeaponCollisionImpactVfx(hit.ImpactPoint, hit.SurfaceNormal, sev, 1f);
            }
        }

        private void ProcessSimTickWeaponFire(uint tick)
        {
            if (IsServer)
                TickPlayerHoldWeaponFiring(authoritative: true);
        }

        internal static void ResetSessionStaticState()
        {
            s_lastServerMotorPhysicsStep = 0;
        }
    }
}
