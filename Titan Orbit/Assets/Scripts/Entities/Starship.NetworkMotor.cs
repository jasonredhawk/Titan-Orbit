using UnityEngine;
using Unity.Netcode;
using Unity.Netcode.Components;
using TitanOrbit.Input;
using TitanOrbit.Networking;
using TitanOrbit.Diagnostics;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Minimal NGO movement: owner sends input via ServerRpc, server simulates with Rigidbody forces,
    /// NetworkTransform + NetworkRigidbody replicate to all clients.
    /// </summary>
    public partial class Starship
    {
        private ShipInputCommand _serverInput = ShipInputCommand.Default;
        private bool _movementNetworkInitialized;
        private int _motionDbgFixedCounter;
        private float _motionDbgPrePhysicsSpeed;
        private Vector3 _motionDbgPrePhysicsRbPos;
        private Vector3 _motionDbgPrePhysicsTrPos;

        public bool IsThrustingForDisplay => moveForwardPressedNet.Value;

        public Vector3 GetSimPosition() => rb != null ? rb.position : transform.position;

        public Vector3 GetSimVelocity()
        {
            if (rb == null) return Vector3.zero;
            if (!IsServer && rb.isKinematic)
                return _collisionPlanarVelocityEstimate;
            return rb.linearVelocity;
        }

        public Quaternion GetSimRotation() => rb != null ? rb.rotation : transform.rotation;

        public float GetSimMass() => rb != null ? rb.mass : EffectiveMass;

        private void EnsureBasicNetworkMovement()
        {
            if (_movementNetworkInitialized) return;
            _movementNetworkInitialized = true;

            var networkTransform = GetComponent<NetworkTransform>();
            if (networkTransform != null)
            {
                networkTransform.enabled = true;
                networkTransform.Interpolate = true;
            }

            var networkRigidbody = GetComponent<NetworkRigidbody>();
            if (networkRigidbody != null)
                networkRigidbody.enabled = true;

            // #region agent log
            if (IsOwner)
            {
                var nt = GetComponent<NetworkTransform>();
                MotorDebugLog.Write("C", "Starship.NetworkMotor.EnsureBasicNetworkMovement",
                    "owner network movement init",
                    $"{{\"isServer\":{IsServer.ToString().ToLower()},\"isClient\":{IsClient.ToString().ToLower()},\"isHost\":{(IsServer && IsClient).ToString().ToLower()},\"ntInterpolate\":{(nt != null && nt.Interpolate).ToString().ToLower()},\"nrEnabled\":{(networkRigidbody != null && networkRigidbody.enabled).ToString().ToLower()},\"rbKinematic\":{(rb != null && rb.isKinematic).ToString().ToLower()},\"rbInterpolation\":{(rb != null ? (int)rb.interpolation : -1)}}}",
                    "post-fix");
            }
            // #endregion

            if (rb == null) return;

            rb.useGravity = false;
            rb.linearDamping = 0f;
            rb.angularDamping = 0f;
            rb.constraints = RigidbodyConstraints.FreezePositionY
                | RigidbodyConstraints.FreezeRotationX
                | RigidbodyConstraints.FreezeRotationZ;

            if (IsServer)
                rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        }

        internal void TickNetworkInputSender()
        {
            if (!IsOwner || IsAwaitingTeamSelection || isDead.Value || gemMoonDocked.Value) return;
            if (inputHandler == null) return;

            ShipInputCommand cmd = BuildInputCommandFromLocalControls();
            SubmitShipInputServerRpc(cmd);
            SyncFireIntentFromInput(cmd);
        }

        internal void ServerApplyBasicMovement()
        {
            if (!IsServer || rb == null || isDead.Value || gemMoonDocked.Value) return;

            EnsureBasicNetworkMovement();
            EnforcePlanarMaxSpeedCap();
            ApplyServerMovement(_serverInput);
        }

        internal void ServerClampPlanarSpeedAfterPhysics()
        {
            if (!IsServer || rb == null || isDead.Value || gemMoonDocked.Value) return;
            EnforcePlanarMaxSpeedCap();
        }

        private void ApplyServerMovement(ShipInputCommand input)
        {
            float dt = Time.fixedDeltaTime;

            ApplyRotationTowardAim(input, dt);
            ApplyThrustAndBrakes(input);

            if (input.Fire)
                TickPlayerHoldWeaponFiring(authoritative: true);

            EnforcePlanarMaxSpeedCap();

            Vector3 vel = rb.linearVelocity;
            currentVelocity = new Vector3(vel.x, 0f, vel.z);

            // #region agent log
            if (IsServer && input.Thrust && IsOwner && (_motionDbgFixedCounter++ % 4 == 0))
            {
                Vector3 trPos = transform.position;
                Vector3 rbPos = rb.position;
                float maxSpd = EffectiveMaxSpeed;
                float spd = currentVelocity.magnitude;
                float trRbDelta = Vector3.Distance(
                    new Vector3(trPos.x, 0f, trPos.z),
                    new Vector3(rbPos.x, 0f, trPos.z));
                float alongVel = 0f;
                if (spd >= maxSpd - 0.01f && moveDirection.sqrMagnitude > 0.01f)
                {
                    alongVel = Vector3.Dot(moveDirection * EffectiveEngineThrust, currentVelocity.normalized);
                }
                _motionDbgPrePhysicsSpeed = spd;
                _motionDbgPrePhysicsRbPos = rbPos;
                _motionDbgPrePhysicsTrPos = trPos;
                MotorDebugLog.Write("B", "Starship.NetworkMotor.ApplyServerMovement",
                    "post-cap movement sample",
                    $"{{\"speed\":{spd:F4},\"maxSpeed\":{maxSpd:F4},\"overMax\":{(spd > maxSpd + 0.05f).ToString().ToLower()},\"trRbDelta\":{trRbDelta:F5},\"alongThrustOnVel\":{alongVel:F3}}}",
                    "speed-cap");
            }
            // #endregion
        }

        /// <summary>
        /// Server hard cap on planar speed. Lateral thrust while turning and weapon recoil can otherwise push past max indefinitely.
        /// </summary>
        private void EnforcePlanarMaxSpeedCap()
        {
            float max = EffectiveMaxSpeed;
            if (max <= 0.001f) return;

            Vector3 vel = rb.linearVelocity;
            Vector3 planar = new Vector3(vel.x, 0f, vel.z);
            float s = planar.magnitude;
            if (s <= max) return;

            Vector3 capped = planar * (max / s);
            rb.linearVelocity = new Vector3(capped.x, 0f, capped.z);
        }

        private void ApplyRotationTowardAim(ShipInputCommand input, float dt)
        {
            if (IsBulletElectricShockDisabled)
                return;

            Vector3 pos = rb.position;
            pos.y = FIXED_Y_POSITION;
            Vector3 aimPoint = new Vector3(input.AimWorldXZ.x, pos.y, input.AimWorldXZ.y);
            Vector3 toAim = aimPoint - pos;
            toAim.y = 0f;
            if (toAim.sqrMagnitude <= 0.001f)
                return;

            Quaternion targetRot = Quaternion.LookRotation(toAim.normalized, Vector3.up);
            Quaternion newRot = Quaternion.RotateTowards(rb.rotation, targetRot, EffectiveRotationSpeed * dt);
            rb.MoveRotation(newRot);
            rb.angularVelocity = Vector3.zero;
        }

        private void ApplyThrustAndBrakes(ShipInputCommand input)
        {
            Vector3 vel = rb.linearVelocity;
            vel.y = 0f;

            Vector3 fwd = rb.rotation * Vector3.forward;
            fwd.y = 0f;
            moveDirection = input.Thrust && fwd.sqrMagnitude > 0.01f ? fwd.normalized : Vector3.zero;

            if (input.Thrust && moveDirection.sqrMagnitude > 0.01f)
            {
                float maxSpeed = EffectiveMaxSpeed;
                float speed = vel.magnitude;
                if (speed < maxSpeed)
                {
                    rb.AddForce(moveDirection * EffectiveEngineThrust, ForceMode.Force);
                }
                else
                {
                    Vector3 velNorm = vel.normalized;
                    Vector3 thrustVec = moveDirection * EffectiveEngineThrust;
                    float alongVel = Vector3.Dot(thrustVec, velNorm);
                    if (alongVel < 0f)
                        rb.AddForce(thrustVec, ForceMode.Force);
                    else
                        rb.AddForce(thrustVec - velNorm * alongVel, ForceMode.Force);
                }
            }
            else if (input.SpaceBrakes && vel.sqrMagnitude > 0.001f)
            {
                float mass = Mathf.Max(0.5f, rb.mass);
                rb.AddForce(-vel.normalized * brakeDeceleration * mass, ForceMode.Force);
            }
        }

        internal void SyncTransformForGameplayQuery() { }

        public Vector3 GetGameplayShipCenterWorld() => GetSimPosition();

        public Vector3 GetDisplayWorldPosition() => transform.position;

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
                Vector3 fwd = transform.forward;
                fwd.y = 0f;
                if (fwd.sqrMagnitude > 0.01f)
                {
                    Vector3 pt = transform.position + fwd.normalized * 10f;
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

        [ServerRpc(RequireOwnership = true)]
        private void SubmitShipInputServerRpc(ShipInputCommand input, ServerRpcParams rpcParams = default)
        {
            // #region agent log
            if (IsOwner && input.Thrust != _serverInput.Thrust)
            {
                MotorDebugLog.Write("D", "Starship.NetworkMotor.SubmitShipInputServerRpc",
                    "thrust input changed",
                    $"{{\"thrust\":{input.Thrust.ToString().ToLower()},\"frame\":{Time.frameCount}}}");
            }
            // #endregion
            _serverInput = input;
            moveForwardPressedNet.Value = input.Thrust;
            shootPressedNet.Value = input.Fire;
            wantToFire.Value = input.Fire;
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

        private void SnapBasicMovementPose(Vector3 position, Quaternion rotation, Vector3 velocity)
        {
            Vector3 pos = position;
            pos.y = FIXED_Y_POSITION;
            velocity.y = 0f;
            transform.SetPositionAndRotation(pos, rotation);
            if (rb != null)
            {
                if (IsServer)
                    rb.isKinematic = false;
                rb.position = pos;
                rb.rotation = rotation;
                rb.linearVelocity = velocity;
                rb.angularVelocity = Vector3.zero;
                if (IsServer)
                    rb.WakeUp();
            }
            currentVelocity = velocity;
            if (IsServer)
            {
                var nt = GetComponent<NetworkTransform>();
                if (nt != null)
                    nt.SetState(pos, rotation, transform.localScale, teleportDisabled: false);
            }
        }

        private void SnapMotorSimAfterSpawn(Vector3 position, Quaternion rotation, Vector3 velocity, float mass)
        {
            if (rb != null)
                rb.mass = Mathf.Max(0.5f, mass);
            _serverInput = ShipInputCommand.Default;
            SnapBasicMovementPose(position, rotation, velocity);
        }

        [ClientRpc]
        private void BroadcastSnapMotorSimClientRpc(Vector3 position, Vector3 velocity, Quaternion rotation, float mass)
        {
            if (IsServer) return;
            if (rb != null)
                rb.mass = Mathf.Max(0.5f, mass);
            SnapBasicMovementPose(position, rotation, velocity);
        }

        private void SnapClientMotorPose(Vector3 position, Quaternion rotation, Vector3 velocity)
        {
            SnapBasicMovementPose(position, rotation, velocity);
        }

        private void SyncMotorRigidbodyToTransform()
        {
            if (rb == null || !gemMoonDocked.Value) return;
            rb.MovePosition(transform.position);
            rb.MoveRotation(transform.rotation);
        }

        internal static void ResetSessionStaticState() { }
    }
}
