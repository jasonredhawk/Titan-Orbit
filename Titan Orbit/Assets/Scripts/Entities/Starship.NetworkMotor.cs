using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using Unity.Netcode.Components;
using TitanOrbit.Input;
using TitanOrbit.Networking;
using TitanOrbit.Systems;

namespace TitanOrbit.Entities
{
    public partial class Starship
    {
        private const int MaxPendingInputSnapshots = 64;
        private const float InputSendMinInterval = 1f / 30f;

        // Motor motion is streamed to observers over an UNRELIABLE ClientRpc at a fixed cadence (independent
        // of the 50 Hz physics step) so packet loss / jitter over Relay cannot trigger reliable head-of-line
        // stalls — the cause of the "move / slowdown / move" choppiness on both remote and owner ships.
        private const float MotorStreamSendInterval = 1f / 30f;    // ~30 Hz dense motion stream
        private const float MotorKeyframeInterval = 1f / 3f;       // ~3 Hz reliable baseline (spawn / late-join)
        private const float MotorKeyframeForcePositionDelta = 5f;  // teleport / large move forces a keyframe now

        // Max planar step the server will accept from a client pose report per physics tick (anti-cheat).
        private const float ServerClientPoseMaxSpeedMultiplier = 2.5f;
        private const float ServerClientPoseMinMaxStep = 3f;

        private ShipInputSnapshot _serverLatestInput = ShipInputSnapshot.Default;
        private ShipInputSnapshot _motorInput = ShipInputSnapshot.Default;
        private uint _nextInputSequence = 1;
        private float _lastInputSendTime;
        private ShipInputSnapshot _lastSentInput = ShipInputSnapshot.Default;
        private uint _lastReconciledInputSequence;
        private uint _serverMotorPublishTick;
        private float _predictedMotorMass;
        private int _collisionIgnoreRefreshFrame = -1;

        // Server-side send cadence accumulators (advanced by fixed delta each physics tick).
        private float _motorStreamSendAccumulator;
        private float _motorKeyframeAccumulator;
        private ShipMotorStateSnapshot _lastStreamedMotorSnapshot;
        private bool _hasStreamedMotorSnapshot;

        private ShipMotorStateSnapshot _pendingMotorState;
        private bool _hasPendingMotorState;
        // Highest MotorPublishTick the owner has accepted for reconcile; lets us drop stale / out-of-order
        // unreliable snapshots so reconciliations are strictly latest-wins (never arrive in bursts).
        private uint _newestReceivedMotorTick;

        private readonly List<ShipInputSnapshot> _pendingInputForReconcile = new List<ShipInputSnapshot>(MaxPendingInputSnapshots);
        private readonly Queue<ShipInputSnapshot> _serverInputQueue = new Queue<ShipInputSnapshot>(MaxPendingInputSnapshots);

        private NetworkVariable<uint> lastProcessedInputSequence = new NetworkVariable<uint>(
            0,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        private bool _ownerAwaitingInitialSpawnSnap = true;

        private NetworkVariable<ShipMotorStateSnapshot> authoritativeMotorState = new NetworkVariable<ShipMotorStateSnapshot>(
            default,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        private Vector3 _clientReportedTractorPosition;
        private Quaternion _clientReportedTractorRotation;
        private bool _hasClientReportedTractorPose;
        private float _clientReportedTractorPoseTime;

        public uint LastProcessedInputSequence => lastProcessedInputSequence.Value;

        private bool UsesServerAuthoritativeMotor => !_isAIControlled;

        private bool ShouldRunMotorSimulation =>
            UsesServerAuthoritativeMotor
            && (IsServer || (IsOwner && !IsServer));

        private bool ShouldRunMotorOnServer => UsesServerAuthoritativeMotor && IsServer;
        private bool ShouldRunMotorPrediction =>
            UsesServerAuthoritativeMotor && IsOwner && !IsServer && !gemMoonDocked.Value;

        private bool IsDedicatedOwnerClient =>
            UsesServerAuthoritativeMotor && IsOwner && !IsServer;

        private void ConfigureOwnerClientPredictionNetworking()
        {
            if (!IsDedicatedOwnerClient || rb == null) return;

            var networkRigidbody = GetComponent<NetworkRigidbody>();
            if (networkRigidbody != null)
                networkRigidbody.enabled = false;

            var networkTransform = GetComponent<NetworkTransform>();
            if (networkTransform != null)
                networkTransform.enabled = false;

            rb.isKinematic = false;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            RestoreOwnerGameplayCollisions();
            RefreshOwnerPredictionCollisionIgnores();
            _ownerPredictionNetworkingReady = true;
        }

        private bool _ownerPredictionNetworkingReady;

        /// <summary>
        /// Motor sim moves rb; gameplay colliders follow rb. Sync transform after each motor step so
        /// child fire points match physics pose. Do not call from LateUpdate — rb interpolation
        /// smooths transform between FixedUpdate steps for camera and hull visuals.
        /// </summary>
        private void SyncMotorRigidbodyToTransform()
        {
            if (rb == null) return;
            Vector3 pos = rb.position;
            pos.y = FIXED_Y_POSITION;
            transform.SetPositionAndRotation(pos, rb.rotation);
        }

        /// <summary>World center for gameplay (gem expulsion, damage VFX). Syncs rb → transform when motor owns pose.</summary>
        public Vector3 GetGameplayShipCenterWorld()
        {
            if (rb == null) return transform.position;
            if (IsServer || IsDedicatedOwnerClient)
                SyncMotorRigidbodyToTransform();
            return rb.position;
        }

        /// <summary>Re-map a server bullet spawn payload to the owner's current muzzle pose (dedicated client).</summary>
        public BulletSpawnPayload AdjustBulletSpawnPayloadForLocalPose(BulletSpawnPayload payload)
        {
            if (bulletConfig == null || bulletConfig.cannons == null)
                return payload;

            SyncMotorRigidbodyToTransform();
            Vector3 shipFwd = rb != null ? rb.rotation * Vector3.forward : transform.forward;
            shipFwd.y = 0f;
            if (shipFwd.sqrMagnitude < 0.01f) shipFwd = Vector3.forward;
            else shipFwd.Normalize();

            Vector3 shipVel = rb != null ? rb.linearVelocity : Vector3.zero;
            shipVel.y = 0f;

            Vector3 serverTotalVel = payload.Velocity;
            serverTotalVel.y = 0f;
            Vector3 inferredBulletDir = serverTotalVel - shipVel;
            if (inferredBulletDir.sqrMagnitude < 0.01f)
                inferredBulletDir = serverTotalVel;
            inferredBulletDir.y = 0f;
            float inferredBulletSpeed = inferredBulletDir.magnitude;
            if (inferredBulletDir.sqrMagnitude > 0.0001f)
                inferredBulletDir.Normalize();

            int bestCannon = -1;
            float bestDot = -1f;
            Vector3 bestOrigin = payload.SpawnPosition;
            Vector3 bestDir = inferredBulletDir.sqrMagnitude > 0.0001f ? inferredBulletDir : shipFwd;

            for (int i = 0; i < bulletConfig.cannons.Count; i++)
            {
                if (!TryResolveCannonFirePose(i, shipFwd, out Vector3 origin, out Vector3 cannonFwd))
                    continue;

                var c = bulletConfig.cannons[i];
                cannonFwd.y = 0f;
                if (cannonFwd.sqrMagnitude < 0.01f) cannonFwd = shipFwd;
                else cannonFwd.Normalize();
                Vector3 cannonRight = Vector3.Cross(Vector3.up, cannonFwd);
                float baseDirAngle = c.directionAngle * Mathf.Deg2Rad;
                Vector3 baseDir = (cannonFwd * Mathf.Cos(baseDirAngle) + cannonRight * Mathf.Sin(baseDirAngle)).normalized;

                if (inferredBulletDir.sqrMagnitude < 0.0001f)
                {
                    bestCannon = i;
                    bestOrigin = origin;
                    bestDir = baseDir;
                    break;
                }

                float dot = Vector3.Dot(baseDir, inferredBulletDir);
                if (dot > bestDot)
                {
                    bestDot = dot;
                    bestCannon = i;
                    bestOrigin = origin;
                    bestDir = baseDir;
                }
            }

            if (bestCannon >= 0)
            {
                payload.SpawnPosition = bestOrigin;
                payload.SpawnPosition.y = 0f;
                if (inferredBulletSpeed > 0.01f)
                    payload.Velocity = bestDir * inferredBulletSpeed + shipVel;
                else
                    payload.Velocity = bestDir * serverTotalVel.magnitude + shipVel;
                payload.Velocity.y = 0f;
            }

            return payload;
        }

        /// <summary>Server: briefly adopt owner-reported pose so wing tractor beams target the visible hull.</summary>
        public bool TryApplyClientTractorPoseOverride()
        {
            if (!IsServer || _isAIControlled || rb == null || !_hasClientReportedTractorPose)
                return false;
            if (Time.time - _clientReportedTractorPoseTime > 0.35f)
                return false;

            Vector3 posePos = _clientReportedTractorPosition;
            posePos.y = FIXED_Y_POSITION;
            rb.position = posePos;
            rb.rotation = _clientReportedTractorRotation;
            SyncMotorRigidbodyToTransform();
            return true;
        }

        public void RestoreMotorPoseAfterTractorOverride(Vector3 savedPosition, Quaternion savedRotation, Vector3 savedVelocity)
        {
            if (rb == null) return;
            rb.position = savedPosition;
            rb.rotation = savedRotation;
            rb.linearVelocity = savedVelocity;
            SyncMotorRigidbodyToTransform();
        }

        private void EnsureOwnerClientPredictionReady()
        {
            if (!IsDedicatedOwnerClient || rb == null) return;
            if (!rb.isKinematic && _ownerPredictionNetworkingReady)
                return;
            ConfigureOwnerClientPredictionNetworking();
        }

        /// <summary>Undo asteroid/moon ignore from older builds; ship–ship ignores remain off.</summary>
        private void RestoreOwnerGameplayCollisions()
        {
            if (!IsDedicatedOwnerClient) return;

            Collider[] shipCols = GetComponentsInChildren<Collider>();
            if (shipCols == null || shipCols.Length == 0) return;

            for (int i = 0; i < Asteroid.AllAsteroids.Count; i++)
            {
                Asteroid asteroid = Asteroid.AllAsteroids[i];
                if (asteroid == null) continue;
                Collider asteroidCol = asteroid.GetComponent<Collider>();
                if (asteroidCol == null) continue;
                for (int c = 0; c < shipCols.Length; c++)
                {
                    Collider shipCol = shipCols[c];
                    if (shipCol == null) continue;
                    Physics.IgnoreCollision(shipCol, asteroidCol, false);
                }
            }

            for (int m = 0; m < PlanetGemMoon.ActiveMoonCount; m++)
            {
                PlanetGemMoon moon = PlanetGemMoon.GetActiveMoonAt(m);
                if (moon == null) continue;
                Collider[] moonCols = moon.GetComponentsInChildren<Collider>();
                if (moonCols == null) continue;
                for (int a = 0; a < moonCols.Length; a++)
                {
                    Collider moonCol = moonCols[a];
                    if (moonCol == null) continue;
                    for (int c = 0; c < shipCols.Length; c++)
                    {
                        Collider shipCol = shipCols[c];
                        if (shipCol == null) continue;
                        Physics.IgnoreCollision(shipCol, moonCol, false);
                    }
                }
            }
        }

        /// <summary>
        /// Owner client: ignore ship–ship physics only (server resolves toroidal hull overlap).
        /// Asteroids and moons must stay solid for local feedback; server still owns damage.
        /// </summary>
        private void RefreshOwnerPredictionCollisionIgnores()
        {
            if (!IsDedicatedOwnerClient) return;

            Collider[] shipCols = GetComponentsInChildren<Collider>();
            if (shipCols == null || shipCols.Length == 0) return;

            for (int i = 0; i < AllStarships.Count; i++)
            {
                Starship other = AllStarships[i];
                if (other == null || other == this) continue;
                Collider[] otherCols = other.GetComponentsInChildren<Collider>();
                if (otherCols == null) continue;
                for (int a = 0; a < otherCols.Length; a++)
                {
                    Collider otherCol = otherCols[a];
                    if (otherCol == null || !otherCol.enabled) continue;
                    for (int c = 0; c < shipCols.Length; c++)
                    {
                        Collider shipCol = shipCols[c];
                        if (shipCol == null || !shipCol.enabled) continue;
                        Physics.IgnoreCollision(shipCol, otherCol, true);
                    }
                }
            }
        }

        private void TickOwnerPredictionCollisionIgnores()
        {
            if (!ShouldRunMotorPrediction) return;
            int frame = Time.frameCount;
            if (_collisionIgnoreRefreshFrame != -1 && frame - _collisionIgnoreRefreshFrame < 32)
                return;
            _collisionIgnoreRefreshFrame = frame;
            RestoreOwnerGameplayCollisions();
            RefreshOwnerPredictionCollisionIgnores();
        }

        private float GetMotorPhysicsMass()
        {
            if (ShouldRunMotorPrediction && _predictedMotorMass > 0f)
                return _predictedMotorMass;
            return EffectiveMass;
        }

        private void ApplyMotorPhysicsMass()
        {
            if (rb == null) return;
            rb.mass = Mathf.Max(0.5f, GetMotorPhysicsMass());
        }

        private void SubscribeOwnerPredictionReconciliation()
        {
            if (!IsDedicatedOwnerClient) return;
            authoritativeMotorState.OnValueChanged += OnAuthoritativeMotorStateChanged;
            if (authoritativeMotorState.Value.MotorPublishTick != 0)
                ConsiderOwnerReconcileSnapshot(authoritativeMotorState.Value);
        }

        private void UnsubscribeOwnerPredictionReconciliation()
        {
            authoritativeMotorState.OnValueChanged -= OnAuthoritativeMotorStateChanged;
        }

        public void SubscribeRemotePredictionInterpolation()
        {
            if (IsServer || IsOwner || _isAIControlled) return;
            authoritativeMotorState.OnValueChanged += OnRemoteAuthoritativeMotorStateChanged;
            if (authoritativeMotorState.Value.MotorPublishTick != 0)
            {
                PushRemoteStateSnapshot(authoritativeMotorState.Value);
            }
        }

        public void UnsubscribeRemotePredictionInterpolation()
        {
            authoritativeMotorState.OnValueChanged -= OnRemoteAuthoritativeMotorStateChanged;
        }

        private void OnRemoteAuthoritativeMotorStateChanged(ShipMotorStateSnapshot previous, ShipMotorStateSnapshot current)
        {
            PushRemoteStateSnapshot(current);
        }

        private void PushRemoteStateSnapshot(ShipMotorStateSnapshot state)
        {
            var interpolator = GetComponent<ShipVisualInterpolator>();
            if (interpolator != null)
            {
                interpolator.OnNetworkMotorStateReceived(state);
            }
        }

        /// <summary>Reliable keyframe path (spawn / late-join baseline). Funnels into the latest-wins reconcile buffer.</summary>
        private void OnAuthoritativeMotorStateChanged(ShipMotorStateSnapshot previous, ShipMotorStateSnapshot current)
        {
            ConsiderOwnerReconcileSnapshot(current);
        }

        /// <summary>
        /// Unreliable per-tick motor stream broadcast to every observer (~30 Hz). Remote ships feed it into the
        /// interpolation buffer; the owner uses it as the latest-wins reconcile source. Delivered unreliably so a
        /// dropped/jittered packet never stalls the stream behind a reliable queue.
        /// </summary>
        [ClientRpc(Delivery = RpcDelivery.Unreliable)]
        private void BroadcastMotorStateUnreliableClientRpc(ShipMotorStateSnapshot state)
        {
            if (IsServer || _isAIControlled) return;

            if (IsOwner)
                ConsiderOwnerReconcileSnapshot(state);
            else
                PushRemoteStateSnapshot(state);
        }

        /// <summary>
        /// Latest-wins acceptance for the owner reconcile buffer. Drops snapshots that are not newer than the
        /// most recent one already accepted, so unreliable reordering cannot produce a burst of reconciliations.
        /// The pose is applied once per FixedUpdate by <see cref="ApplyPendingOwnerMotorReconciliation"/>.
        /// </summary>
        private void ConsiderOwnerReconcileSnapshot(ShipMotorStateSnapshot state)
        {
            if (!IsDedicatedOwnerClient) return;
            if (state.MotorPublishTick != 0 && state.MotorPublishTick <= _newestReceivedMotorTick)
                return;
            _newestReceivedMotorTick = state.MotorPublishTick;
            _pendingMotorState = state;
            _hasPendingMotorState = true;
        }

        /// <summary>Runs at the start of owner FixedUpdate before motor / moon dock.</summary>
        private void ApplyPendingOwnerMotorReconciliation()
        {
            if (!IsDedicatedOwnerClient || rb == null || !_hasPendingMotorState)
                return;

            _hasPendingMotorState = false;
            TryApplyOwnerMotorReconciliation(_pendingMotorState);
        }

        private void TickNetworkInputSender()
        {
            if (!IsOwner || _isAIControlled || IsAwaitingTeamSelection) return;
            if (inputHandler == null) return;

            if (IsDedicatedOwnerClient)
                SyncMotorRigidbodyToTransform();

            ShipInputSnapshot snap = BuildInputSnapshotFromLocalControls();
            bool changed = snap.Thrust != _lastSentInput.Thrust
                || snap.Fire != _lastSentInput.Fire
                || snap.SpaceBrakes != _lastSentInput.SpaceBrakes
                || (snap.AimWorldXZ - _lastSentInput.AimWorldXZ).sqrMagnitude > 0.04f;
            float now = Time.unscaledTime;
            if (!changed && now - _lastInputSendTime < InputSendMinInterval)
                return;

            snap.Sequence = _nextInputSequence++;
            if (_nextInputSequence == 0) _nextInputSequence = 1;
            snap.ClientSendTime = now;
            _lastSentInput = snap;
            _lastInputSendTime = now;

            _pendingInputForReconcile.Add(snap);
            while (_pendingInputForReconcile.Count > MaxPendingInputSnapshots)
                _pendingInputForReconcile.RemoveAt(0);

            SubmitShipInputServerRpc(snap);
            SyncFireIntentFromInput(snap);
        }

        private ShipInputSnapshot BuildInputSnapshotFromLocalControls()
        {
            Vector2 aimXZ = Vector2.zero;
            UnityEngine.Camera cam = UnityEngine.Camera.main;
            if (cam != null && inputHandler != null)
            {
                Vector3 aimWorld = inputHandler.GetMouseWorldPosition(cam);
                aimXZ = new Vector2(aimWorld.x, aimWorld.z);
            }
            else if (rb != null)
            {
                Vector3 fwd = rb.rotation * Vector3.forward;
                fwd.y = 0f;
                if (fwd.sqrMagnitude > 0.01f)
                {
                    Vector3 pt = rb.position + fwd.normalized * 10f;
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

            Vector3 vel = rb != null ? rb.linearVelocity : Vector3.zero;
            vel.y = 0f;

            return new ShipInputSnapshot
            {
                AimWorldXZ = aimXZ,
                Thrust = inputHandler.MoveForwardPressed,
                Fire = inputHandler.ShootPressed && !uiBlocksShot && canFire,
                SpaceBrakes = (inputHandler as PlayerInputHandler)?.SpaceBrakesEnabled ?? true,
                PredictedPosition = GetPredictedPositionForTractorReport(),
                PredictedRotation = rb != null ? rb.rotation : transform.rotation,
                PredictedVelocity = vel,
            };
        }

        private Vector3 GetPredictedPositionForTractorReport()
        {
            if (rb == null)
                return transform.position;
            Vector3 pos = rb.position;
            pos.y = FIXED_Y_POSITION;
            return pos;
        }

        /// <summary>
        /// Dedicated server: human ships are client-authoritative for pose. The owner simulates locally and
        /// streams predicted position/rotation/velocity; the server adopts that pose for asteroid collisions and
        /// for broadcasting to other clients. This prevents phantom hits when server-only simulation diverged
        /// from what the owner was actually flying through.
        /// </summary>
        private void ServerSyncHumanShipPoseFromClientReport()
        {
            if (!IsServer || _isAIControlled || rb == null || gemMoonDocked.Value) return;
            if (_serverLatestInput.Sequence == 0) return;

            Vector3 pos = _serverLatestInput.PredictedPosition;
            pos.y = FIXED_Y_POSITION;
            Quaternion rot = _serverLatestInput.PredictedRotation;
            Vector3 vel = _serverLatestInput.PredictedVelocity;
            vel.y = 0f;

            Vector3 offset = TitanOrbit.Generation.ToroidalMap.ShortestWorldOffsetXZ(rb.position, pos);
            float maxStep = Mathf.Max(
                EffectiveMaxSpeed * Time.fixedDeltaTime * ServerClientPoseMaxSpeedMultiplier,
                ServerClientPoseMinMaxStep);
            if (offset.sqrMagnitude > maxStep * maxStep)
                pos = rb.position + offset.normalized * maxStep;

            float maxSpeed = EffectiveMaxSpeed;
            if (maxSpeed > 0.001f && vel.sqrMagnitude > maxSpeed * maxSpeed * 2.25f)
                vel = vel.normalized * maxSpeed;

            rb.position = pos;
            rb.rotation = rot;
            rb.linearVelocity = vel;
            currentVelocity = vel;
            SyncMotorRigidbodyToTransform();
        }

        private void SyncFireIntentFromInput(ShipInputSnapshot snap)
        {
            if (snap.Fire != localWantToFireSent)
            {
                localWantToFireSent = snap.Fire;
                if (snap.Fire)
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

        [ServerRpc]
        private void SubmitShipInputServerRpc(ShipInputSnapshot input)
        {
            if (_isAIControlled) return;
            _serverInputQueue.Enqueue(input);
            while (_serverInputQueue.Count > MaxPendingInputSnapshots)
                _serverInputQueue.Dequeue();

            if (input.Sequence >= _serverLatestInput.Sequence)
                _serverLatestInput = input;

            _clientReportedTractorPosition = input.PredictedPosition;
            _clientReportedTractorRotation = input.PredictedRotation;
            _hasClientReportedTractorPose = true;
            _clientReportedTractorPoseTime = Time.time;

            moveForwardPressedNet.Value = input.Thrust;
            shootPressedNetNetSync(input.Fire);
            wantToFire.Value = input.Fire;
        }

        private void shootPressedNetNetSync(bool fire)
        {
            shootPressedNet.Value = fire;
        }

        private void ServerConsumeInputForMotorTick()
        {
            if (!ShouldRunMotorOnServer) return;
            ShipInputSnapshot latest = _serverLatestInput;
            while (_serverInputQueue.Count > 0)
                latest = _serverInputQueue.Dequeue();
            _motorInput = latest;
        }

        private void ServerPublishAuthoritativeMotorState()
        {
            if (!IsServer || _isAIControlled || rb == null) return;

            Vector3 vel = rb.linearVelocity;
            vel.y = 0f;
            if (gemMoonDocked.Value)
            {
                Planet dockPlanet = ResolveGemMoonDockPlanet();
                PlanetGemMoon dockMoon = dockPlanet != null ? dockPlanet.GemMoon : null;
                if (dockMoon != null)
                {
                    vel = dockMoon.WorldOrbitVelocity;
                    vel.y = 0f;
                }
            }
            uint appliedSeq = _motorInput.Sequence;

            _serverMotorPublishTick++;
            var snapshot = new ShipMotorStateSnapshot
            {
                Position = rb.position,
                Rotation = rb.rotation,
                Velocity = vel,
                LastAppliedInputSequence = appliedSeq,
                MotorPublishTick = _serverMotorPublishTick,
                SimMass = rb.mass,
                // Stamp with the PHYSICS clock, not NGO's ServerTime. rb.position is sampled here in FixedUpdate,
                // so the timestamp must come from the same clock that defines when that position existed. NGO's
                // ServerTime advances on a separate (frame/tick) cadence, so sampling it in FixedUpdate labels
                // 50 Hz-sampled positions with mismatched times: the position deltas and time deltas between
                // streamed snapshots disagree, and the client interpolator renders that as the "move / slowdown /
                // move" speed ripple. Time.fixedTimeAsDouble advances exactly fixedDeltaTime per physics step, so
                // every position carries a time label consistent with its true motion and playback is constant-rate.
                ServerTime = Time.fixedTimeAsDouble,
                Thrust = _motorInput.Thrust && !IsBulletElectricShockDisabled,
            };
            lastProcessedInputSequence.Value = appliedSeq;

            float dt = Time.fixedDeltaTime;
            bool forceKeyframe = ShouldForceMotorKeyframe(snapshot);

            // Dense unreliable motion stream at a steady ~30 Hz cadence.
            _motorStreamSendAccumulator += dt;
            if (_motorStreamSendAccumulator >= MotorStreamSendInterval || forceKeyframe)
            {
                // Subtract (don't zero) so the long-run cadence stays ~30 Hz instead of drifting to 25 Hz.
                _motorStreamSendAccumulator -= MotorStreamSendInterval;
                if (_motorStreamSendAccumulator < 0f || forceKeyframe)
                    _motorStreamSendAccumulator = 0f;
                _lastStreamedMotorSnapshot = snapshot;
                _hasStreamedMotorSnapshot = true;
                BroadcastMotorStateUnreliableClientRpc(snapshot);
            }

            // Sparse reliable keyframe so late-joining observers and respawns get an authoritative baseline,
            // plus an immediate keyframe on teleport / large pose change.
            _motorKeyframeAccumulator += dt;
            bool firstKeyframe = authoritativeMotorState.Value.MotorPublishTick == 0;
            if (_motorKeyframeAccumulator >= MotorKeyframeInterval || forceKeyframe || firstKeyframe)
            {
                _motorKeyframeAccumulator = 0f;
                authoritativeMotorState.Value = snapshot;
            }
        }

        /// <summary>Teleport / respawn / gross pose change should not wait for the next periodic keyframe.</summary>
        private bool ShouldForceMotorKeyframe(ShipMotorStateSnapshot snapshot)
        {
            if (!_hasStreamedMotorSnapshot)
                return true;
            Vector3 offset = TitanOrbit.Generation.ToroidalMap.ShortestWorldOffsetXZ(
                _lastStreamedMotorSnapshot.Position, snapshot.Position);
            offset.y = 0f;
            return offset.sqrMagnitude > MotorKeyframeForcePositionDelta * MotorKeyframeForcePositionDelta;
        }

        private void ClientApplyPredictionInput()
        {
            if (!ShouldRunMotorPrediction) return;
            // Predict from live controls immediately — do not wait for throttled ServerRpc snapshots.
            ShipInputSnapshot live = BuildInputSnapshotFromLocalControls();
            live.Sequence = _lastSentInput.Sequence;
            _motorInput = live;
        }

        private void ApplyMotorInputToMoveDirection()
        {
            if (IsBulletElectricShockDisabled || !_motorInput.Thrust)
            {
                moveDirection = Vector3.zero;
                return;
            }

            Vector3 fwd = rb != null ? rb.rotation * Vector3.forward : transform.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude > 0.01f)
                moveDirection = fwd.normalized;
            else
                moveDirection = Vector3.zero;
        }

        private bool TryGetMotorAimRotation(out Quaternion targetRotation)
        {
            targetRotation = Quaternion.identity;
            if (rb == null) return false;

            Vector3 shipPos = rb.position;
            Vector3 aimPoint = new Vector3(_motorInput.AimWorldXZ.x, shipPos.y, _motorInput.AimWorldXZ.y);
            Vector3 directionToAim = aimPoint - shipPos;
            directionToAim.y = 0f;
            if (directionToAim.sqrMagnitude <= 0.001f)
                return false;

            directionToAim.Normalize();
            targetRotation = Quaternion.LookRotation(directionToAim);
            return true;
        }

        private bool GetMotorSpaceBrakesEnabled() =>
            _motorInput.SpaceBrakes;

        private void RunPlayerMotorSimulationTick()
        {
            if (!ShouldRunMotorSimulation || IsAwaitingTeamSelection) return;
            if (gemMoonDocked.Value) return;

            ApplyMotorInputToMoveDirection();

            bool inOrbitRing = currentOrbitPlanet != null && IsShipInPlanetOrbitRing(currentOrbitPlanet);
            bool useOrbit = inOrbitRing && !_motorInput.Thrust && !IsInsideFriendlyGemMoonOrbitZone();
            if (useOrbit)
            {
                HandleOrbitMovement();
                HandleMotorRotation();
            }
            else
            {
                HandleMovementWithMotorBrakes();
                HandleMotorRotation();
            }
        }

        private bool withinGemMoonBoundaryForMotor;

        private void HandleMotorRotation()
        {
            if (IsBulletElectricShockDisabled)
                return;

            EnsureCachedCameraControllerForShake();
            if (s_cachedCameraController != null && s_cachedCameraController.IsTheatricalShipRotationLocked)
            {
                if (rb != null)
                    rb.angularVelocity = Vector3.zero;
                return;
            }

            if (TryGetMotorAimRotation(out Quaternion targetRotation))
            {
                Quaternion newRotation = Quaternion.RotateTowards(
                    rb.rotation,
                    targetRotation,
                    EffectiveRotationSpeed * Time.fixedDeltaTime);
                rb.MoveRotation(newRotation);
            }
        }

        private void HandleMovementWithMotorBrakes()
        {
            currentVelocity = rb.linearVelocity;
            currentVelocity.y = 0f;

            if (IsBulletElectricShockDisabled)
            {
                TickElectricShockBraking();
                return;
            }

            float mass = Mathf.Max(0.5f, rb.mass);
            float maxSpeed = EffectiveMaxSpeed;

            if (moveDirection.magnitude > 0.1f)
            {
                float speed = currentVelocity.magnitude;
                if (speed < maxSpeed)
                    rb.AddForce(moveDirection * EffectiveEngineThrust, ForceMode.Force);
                else
                {
                    Vector3 velNorm = currentVelocity.normalized;
                    Vector3 thrustVec = moveDirection * EffectiveEngineThrust;
                    float alongVel = Vector3.Dot(thrustVec, velNorm);
                    Vector3 steerForce = thrustVec - velNorm * Mathf.Max(0f, alongVel);
                    rb.AddForce(steerForce, ForceMode.Force);
                }
            }
            else
            {
                bool brakesOn = GetMotorSpaceBrakesEnabled();
                if (brakesOn && currentVelocity.sqrMagnitude > 0.001f)
                {
                    float brakeForce = brakeDeceleration * mass;
                    rb.AddForce(-currentVelocity.normalized * brakeForce, ForceMode.Force);
                }
            }

            Vector3 vel = rb.linearVelocity;
            if (Mathf.Abs(vel.y) > 0.01f)
            {
                vel.y = 0f;
                rb.linearVelocity = vel;
            }

            float mag = rb.linearVelocity.magnitude;
            if (mag > maxSpeed && maxSpeed > 0.001f)
            {
                float effectiveRecoilDecay = recoilDecayPerSecond / mass;
                float targetMag = Mathf.MoveTowards(mag, maxSpeed, effectiveRecoilDecay * Time.fixedDeltaTime);
                vel = rb.linearVelocity;
                vel.y = 0f;
                rb.linearVelocity = vel.normalized * targetMag;
            }

            currentVelocity = rb.linearVelocity;
        }

        /// <summary>
        /// Owner: sync gem mass + input acks only. Local prediction owns pose/velocity; the server adopts the
        /// owner's reported pose for collisions and for what other players see — never the reverse.
        /// </summary>
        private void TryApplyOwnerMotorReconciliation(ShipMotorStateSnapshot serverState)
        {
            if (rb == null) return;

            uint serverSeq = serverState.LastAppliedInputSequence;

            if (serverState.SimMass > 0f)
            {
                float mass = Mathf.Max(0.5f, serverState.SimMass);
                if (!Mathf.Approximately(mass, _predictedMotorMass))
                {
                    _predictedMotorMass = mass;
                    rb.mass = mass;
                }
            }

            if (_ownerAwaitingInitialSpawnSnap && serverState.MotorPublishTick > 0)
            {
                SnapOwnerRbToServerMotorState(serverState);
                _ownerAwaitingInitialSpawnSnap = false;
            }

            if (serverSeq > _lastReconciledInputSequence)
            {
                PruneAckedInputs(serverSeq);
                _lastReconciledInputSequence = serverSeq;
            }
        }

        private void SnapOwnerRbToServerMotorState(ShipMotorStateSnapshot serverState)
        {
            if (rb == null) return;

            Vector3 pos = serverState.Position;
            pos.y = FIXED_Y_POSITION;
            Vector3 vel = serverState.Velocity;
            vel.y = 0f;

            rb.position = pos;
            rb.rotation = serverState.Rotation;
            rb.linearVelocity = vel;
            currentVelocity = vel;
            SyncMotorRigidbodyToTransform();
        }

        private void PruneAckedInputs(uint serverSeq)
        {
            for (int i = _pendingInputForReconcile.Count - 1; i >= 0; i--)
            {
                if (_pendingInputForReconcile[i].Sequence <= serverSeq)
                    _pendingInputForReconcile.RemoveAt(i);
            }
        }
    }
}
