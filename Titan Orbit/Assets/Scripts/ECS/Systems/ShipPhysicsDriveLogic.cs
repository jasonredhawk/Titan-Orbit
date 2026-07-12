using Unity.Mathematics;
using Unity.Physics;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Burst-safe ship physics input step shared by server authority and client prediction.
    /// Uses Unity Physics velocity integration only — linear thrust via impulse, braking via
    /// <see cref="PhysicsDamping"/>, yaw via angular velocity. No custom motor integrator.
    /// Paired with <see cref="ShipPhysicsDriveSystem"/> and
    /// <see cref="ShipClientPredictedPhysicsDriveSystem"/>.
    /// </summary>
    public static class ShipPhysicsDriveLogic
    {
        const float CruiseLinearDamping = 0.15f;
        const float CruiseAngularDamping = 2f;
        const float BrakeAngularDamping = 4f;

        /// <summary>
        /// Applies player input to physics components before <see cref="Unity.Physics.Systems.PhysicsSystemGroup"/>.
        /// </summary>
        public static void Step(
            in ShipInput input,
            in ShipMotorConfig motor,
            in ShipState shipState,
            ref PhysicsVelocity physicsVelocity,
            ref PhysicsDamping physicsDamping,
            ref LocalTransform transform,
            in PhysicsMass physicsMass,
            float dt)
        {
            if (dt <= 0f)
                return;

            // --- Dead / team select: stop the body ---
            if (shipState.IsDead || shipState.AwaitingTeamSelection)
            {
                physicsVelocity = PhysicsVelocity.Zero;
                physicsDamping = new PhysicsDamping { Linear = 0f, Angular = 0f };
                return;
            }

            // --- Braking: standard PhysicsDamping (no custom deceleration curve) ---
            float brakeDamping = math.max(0.5f, motor.BrakeDeceleration);
            physicsDamping = new PhysicsDamping
            {
                Linear = input.SpaceBrakes ? brakeDamping : CruiseLinearDamping,
                Angular = input.SpaceBrakes ? BrakeAngularDamping : CruiseAngularDamping,
            };

            // --- Yaw toward aim using physics angular velocity ---
            AimWorldDirection(in transform.Position, in transform.Rotation, in input.AimPlanarDir, out float3 aimDir);
            ApplyYawTowardAim(
                ref physicsVelocity,
                in transform.Rotation,
                in aimDir,
                motor.RotationSpeed,
                dt);

            // --- Forward thrust: linear impulse (force × dt) along ship forward ---
            if (input.Thrust)
            {
                float3 forward = math.mul(transform.Rotation, new float3(0f, 0f, 1f));
                forward.y = 0f;
                if (math.lengthsq(forward) > 0.0001f)
                {
                    forward = math.normalize(forward);
                    float3 linearImpulse = forward * (motor.EngineThrust * dt);
                    ApplyLinearImpulse(ref physicsVelocity, in physicsMass, transform.Scale, linearImpulse);
                }
            }

            // --- Top-down: lock vertical motion ---
            float3 linear = physicsVelocity.Linear;
            linear.y = 0f;
            physicsVelocity.Linear = linear;

            float3 angular = physicsVelocity.Angular;
            angular.x = 0f;
            angular.z = 0f;
            physicsVelocity.Angular = angular;
        }

        /// <summary>Standard impulse: Δv = impulse × inverseMass (Unity Physics convention).</summary>
        public static void ApplyLinearImpulse(
            ref PhysicsVelocity velocity,
            in PhysicsMass mass,
            float uniformScale,
            in float3 impulse)
        {
            float invMass = mass.InverseMass / math.max(uniformScale, 1e-6f);
            velocity.Linear += impulse * invMass;
        }

        /// <summary>Sets world-space yaw rate toward aim, clamped by degrees-per-second cap.</summary>
        static void ApplyYawTowardAim(
            ref PhysicsVelocity velocity,
            in quaternion rotation,
            in float3 aimDirWorld,
            float rotationSpeedDegPerSec,
            float dt)
        {
            float3 forward = math.mul(rotation, new float3(0f, 0f, 1f));
            forward.y = 0f;
            if (math.lengthsq(forward) < 0.0001f)
                forward = new float3(0f, 0f, 1f);
            else
                forward = math.normalize(forward);

            float3 target = aimDirWorld;
            target.y = 0f;
            if (math.lengthsq(target) < 0.0001f)
                return;
            target = math.normalize(target);

            // [UNITY] Left-handed XZ: match Vector3.SignedAngle(forward, target, up) — cross order is z*x - x*z.
            float signedYaw = math.atan2(
                forward.z * target.x - forward.x * target.z,
                forward.x * target.x + forward.z * target.z);
            float maxYaw = math.radians(rotationSpeedDegPerSec) * dt;
            float yawStep = math.clamp(signedYaw, -maxYaw, maxYaw);
            float yawRate = yawStep / math.max(dt, 1e-6f);
            velocity.Angular = new float3(0f, yawRate, 0f);
        }

        static void AimWorldDirection(
            in float3 shipPos,
            in quaternion rot,
            in float2 aimPlanarDir,
            out float3 aimDirWorld)
        {
            if (math.lengthsq(aimPlanarDir) > 0.01f)
            {
                float2 dir = math.normalize(aimPlanarDir);
                float3 toAim = new float3(dir.x, 0f, dir.y);
                aimDirWorld = math.normalize(toAim);
                return;
            }

            float3 forward = math.mul(rot, new float3(0f, 0f, 1f));
            forward.y = 0f;
            if (math.lengthsq(forward) < 0.0001f)
                forward = new float3(0f, 0f, 1f);
            else
                forward = math.normalize(forward);

            aimDirWorld = forward;
        }
    }
}
