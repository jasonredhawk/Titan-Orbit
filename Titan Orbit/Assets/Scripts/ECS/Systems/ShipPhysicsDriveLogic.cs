using TitanOrbit.Simulation;
using Unity.Mathematics;
using Unity.Physics;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Shared Starblast-style planar motor for server authority and client owner prediction.
    /// [NETCODE] Identical math on both worlds inside PredictedFixedStepSimulationSystemGroup —
    /// same inputs must produce the same velocity/yaw so reconciliation stays quiet.
    /// [PHYSICS] Drive writes <see cref="PhysicsVelocity"/> and yaw only; Unity Physics integrates
    /// position and resolves hull collisions afterward. The next tick <b>reads</b> post-collision
    /// velocity (bounce is not overwritten blindly). Paired with <see cref="ShipPhysicsDriveSystem"/>
    /// and <see cref="ShipClientPredictedPhysicsDriveSystem"/>.
    /// </summary>
    public static class ShipPhysicsDriveLogic
    {
        /// <summary>
        /// Soft coast drag when not thrusting or braking (high friction / low agility).
        /// Units: deceleration in world-units/s² applied against velocity.
        /// </summary>
        const float CoastFriction = 4.5f;

        /// <summary>
        /// Applies player input before <see cref="Unity.Physics.Systems.PhysicsSystemGroup"/>.
        /// Starts from the previous physics step's linear velocity so asteroid bounces carry forward.
        /// </summary>
        public static void Step(
            in ShipInput input,
            in ShipMotorConfig motor,
            in ShipMoonDockState moonDock,
            in ShipState shipState,
            ref PhysicsVelocity physicsVelocity,
            ref PhysicsDamping physicsDamping,
            ref LocalTransform transform,
            float dt)
        {
            // --- Guard: fixed-step dt only ---
            if (dt <= 0f)
                return;

            // --- Dead / team select: freeze the hull ---
            if (shipState.IsDead || shipState.AwaitingTeamSelection)
            {
                physicsVelocity = PhysicsVelocity.Zero;
                physicsDamping = default;
                return;
            }

            // --- Landed moon dock — hold still until thrust undocks ---
            if (moonDock.MoonPlanetId != 0 &&
                moonDock.LandingProgress >= GemEconomyConstants.MoonLandingCompleteThreshold &&
                !input.Thrust)
            {
                physicsVelocity = PhysicsVelocity.Zero;
                physicsDamping = default;
                return;
            }

            // --- Movement mass (HP bulk + gems) — must match on client and server ---
            float baseMass = motor.Mass > 0f ? motor.Mass : ShipMassLogic.DefaultBaseMass;
            float movementMass = ShipMassLogic.ComputeMovementMass(
                motor.HullMassReference,
                shipState.MaxHealth,
                motor.ChassisReferenceHealth,
                shipState.CurrentGems,
                baseMass);

            // --- Yaw: dt-capped slerp toward aim (never snap to mouse in one frame) ---
            AimWorldPoint(in transform.Position, in transform.Rotation, in input.AimPlanarDir, out float2 aimWorldXz);
            TryRotateTowardAim(ref transform, in aimWorldXz, motor.RotationSpeed, dt);

            // --- Start from post-collision velocity (Unity Physics may have bounced us last tick) ---
            float3 vel = physicsVelocity.Linear;
            vel.y = 0f;

            ApplyThrustCoastAndBrakes(
                ref vel,
                in transform.Rotation,
                motor.EngineThrust,
                motor.MaxSpeed,
                motor.BrakeDeceleration,
                movementMass,
                input.Thrust,
                input.SpaceBrakes,
                dt);

            ApplyRecoilDecay(ref vel, motor.MaxSpeed, movementMass, motor.RecoilDecayPerSecond, dt);

            vel.y = 0f;
            physicsVelocity = new PhysicsVelocity
            {
                Linear = vel,
                Angular = float3.zero,
            };

            // [PHYSICS] Motor owns cruise feel — clear package damping so it cannot fight our curves.
            physicsDamping = default;
        }

        /// <summary>
        /// Slerps ship rotation toward the aim point, clamped by rotation speed × dt.
        /// [TITAN-ORBIT] Starblast pillar 3 — continuous turn rate; never set Rotation = LookAt in one tick.
        /// </summary>
        static void TryRotateTowardAim(
            ref LocalTransform transform,
            in float2 aimWorldXz,
            float rotationSpeedDeg,
            float dt)
        {
            float3 shipPos = transform.Position;
            float3 aimPoint = new float3(aimWorldXz.x, shipPos.y, aimWorldXz.y);
            float3 directionToAim = aimPoint - shipPos;
            directionToAim.y = 0f;
            if (math.lengthsq(directionToAim) <= 0.001f)
                return;

            directionToAim = math.normalize(directionToAim);
            quaternion targetRotation = quaternion.LookRotationSafe(directionToAim, math.up());
            float maxRadians = math.radians(math.max(0f, rotationSpeedDeg) * dt);
            float angle = math.angle(transform.Rotation, targetRotation);
            transform.Rotation = angle <= maxRadians
                ? targetRotation
                : math.slerp(transform.Rotation, targetRotation, maxRadians / math.max(angle, 1e-6f));
        }

        /// <summary>
        /// Continuous thrust, coast friction, and space-brake deceleration on the XZ plane.
        /// </summary>
        static void ApplyThrustCoastAndBrakes(
            ref float3 vel,
            in quaternion rotation,
            float engineThrust,
            float maxSpeed,
            float brakeDeceleration,
            float mass,
            bool thrust,
            bool spaceBrakes,
            float dt)
        {
            mass = math.max(ShipMassLogic.MinMass, mass);
            maxSpeed = math.max(0.1f, maxSpeed);

            if (thrust)
            {
                float3 fwd = math.mul(rotation, new float3(0f, 0f, 1f));
                fwd.y = 0f;
                if (math.lengthsq(fwd) > 0.01f)
                {
                    float3 moveDirection = math.normalize(fwd);
                    float speed = math.length(vel);
                    float3 accel;
                    if (speed < maxSpeed)
                    {
                        // [TITAN-ORBIT] F = ma — EngineThrust is force; mass slows acceleration.
                        accel = moveDirection * (engineThrust / mass);
                    }
                    else
                    {
                        // At cruise cap: thrust steers sideways without adding forward speed.
                        float3 velNorm = math.normalize(vel);
                        float3 thrustVec = moveDirection * engineThrust;
                        float alongVel = math.dot(thrustVec, velNorm);
                        float3 steerForce = thrustVec - velNorm * math.max(0f, alongVel);
                        accel = steerForce / mass;
                    }

                    vel += accel * dt;
                }
            }
            else if (spaceBrakes && math.lengthsq(vel) > 0.001f)
            {
                // Space brakes: authored continuous deceleration toward stop.
                float brakeAccel = math.max(0.5f, brakeDeceleration);
                float3 brake = -math.normalize(vel) * brakeAccel * dt;
                if (math.lengthsq(brake) >= math.lengthsq(vel))
                    vel = float3.zero;
                else
                    vel += brake;
            }
            else if (math.lengthsq(vel) > 0.001f)
            {
                // Coast friction: ships bleed speed without thrust (predictable, high-friction feel).
                float3 drag = -math.normalize(vel) * CoastFriction * dt;
                if (math.lengthsq(drag) >= math.lengthsq(vel))
                    vel = float3.zero;
                else
                    vel += drag;
            }

            vel.y = 0f;

            // Soft cruise clamp — collision overspeed is allowed until ApplyRecoilDecay bleeds it.
            float mag = math.length(vel);
            if (mag > maxSpeed * 1.35f)
                vel = vel * ((maxSpeed * 1.35f) / mag);
        }

        /// <summary>
        /// Bleeds temporary overspeed from recoil / impact bounces back toward MaxSpeed.
        /// </summary>
        static void ApplyRecoilDecay(
            ref float3 vel,
            float maxSpeed,
            float mass,
            float recoilDecayPerSecond,
            float dt)
        {
            float mag = math.length(vel);
            if (mag <= maxSpeed || maxSpeed <= 0.001f)
                return;

            float decay = recoilDecayPerSecond > 0f ? recoilDecayPerSecond : 6f;
            float effectiveRecoilDecay = decay / math.max(ShipMassLogic.MinMass, mass);
            float targetMag = math.clamp(mag - effectiveRecoilDecay * dt, maxSpeed, mag);
            vel = math.normalize(vel) * targetMag;
        }

        /// <summary>
        /// Builds a world aim point from stick/mouse planar direction, or falls back to current facing.
        /// </summary>
        static void AimWorldPoint(
            in float3 shipPos,
            in quaternion rot,
            in float2 aimPlanarDir,
            out float2 aimWorldXz)
        {
            if (math.lengthsq(aimPlanarDir) > 0.01f)
            {
                float2 dir = math.normalize(aimPlanarDir);
                aimWorldXz = shipPos.xz + dir * 100f;
                return;
            }

            float3 forward = math.mul(rot, new float3(0f, 0f, 1f));
            forward.y = 0f;
            if (math.lengthsq(forward) < 0.0001f)
                forward = new float3(0f, 0f, 1f);
            else
                forward = math.normalize(forward);

            aimWorldXz = shipPos.xz + forward.xz * 100f;
        }
    }
}
