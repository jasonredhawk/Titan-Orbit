using UnityEngine;

namespace TitanOrbit.Simulation
{
    /// <summary>
    /// Fixed-step deterministic ship motor. No Unity physics forces — identical results on every peer.
    /// </summary>
    public static class ShipMotorSimulator
    {
        public static void Step(
            ref ShipMotorState state,
            in ShipMotorTickParams p,
            Vector2 aimWorldXZ,
            bool thrust,
            bool spaceBrakes,
            bool integratePosition = true)
        {
            float dt = p.FixedDeltaTime;
            if (dt <= 0f) return;

            if (p.ElectricShockDisabled)
            {
                ApplyElectricShockBraking(ref state, p.BrakeDeceleration, dt);
                state.Position.y = p.FixedY;
                return;
            }

            if (p.TheatricalRotationLocked)
            {
                // Hold rotation; still allow velocity decay from prior state if needed.
            }
            else
            {
                TryRotateTowardAim(ref state, aimWorldXZ, p.RotationSpeedDegPerSec, dt);
            }

            if (p.UseOrbit)
            {
                Vector3 currentVel = state.Velocity;
                currentVel.y = 0f;
                Vector3 desired = p.OrbitDesiredVelocity;
                desired.y = 0f;
                float t = Mathf.Clamp01(p.OrbitAlignRate * dt);
                Vector3 blended = Vector3.Lerp(currentVel, desired, t);
                blended.y = 0f;
                state.Velocity = blended;
            }
            else
            {
                ApplyThrustAndBrakes(ref state, p, thrust, spaceBrakes, dt);
            }

            if (integratePosition)
            {
                IntegratePosition(ref state, dt);
                state.Position.y = p.FixedY;
            }
        }

        private static void TryRotateTowardAim(
            ref ShipMotorState state,
            Vector2 aimWorldXZ,
            float rotationSpeedDeg,
            float dt)
        {
            Vector3 shipPos = state.Position;
            Vector3 aimPoint = new Vector3(aimWorldXZ.x, shipPos.y, aimWorldXZ.y);
            Vector3 directionToAim = aimPoint - shipPos;
            directionToAim.y = 0f;
            if (directionToAim.sqrMagnitude <= 0.001f)
                return;

            directionToAim.Normalize();
            Quaternion targetRotation = Quaternion.LookRotation(directionToAim);
            state.Rotation = Quaternion.RotateTowards(state.Rotation, targetRotation, rotationSpeedDeg * dt);
        }

        private static void ApplyThrustAndBrakes(
            ref ShipMotorState state,
            in ShipMotorTickParams p,
            bool thrust,
            bool spaceBrakes,
            float dt)
        {
            Vector3 vel = state.Velocity;
            vel.y = 0f;
            float mass = Mathf.Max(0.5f, state.Mass);
            float maxSpeed = p.MaxSpeed;

            Vector3 moveDirection = Vector3.zero;
            if (thrust)
            {
                Vector3 fwd = state.Rotation * Vector3.forward;
                fwd.y = 0f;
                if (fwd.sqrMagnitude > 0.01f)
                    moveDirection = fwd.normalized;
            }

            if (moveDirection.magnitude > 0.1f)
            {
                float speed = vel.magnitude;
                Vector3 accel;
                if (speed < maxSpeed)
                {
                    accel = moveDirection * (p.EngineThrust / mass);
                }
                else
                {
                    Vector3 velNorm = vel.normalized;
                    Vector3 thrustVec = moveDirection * p.EngineThrust;
                    float alongVel = Vector3.Dot(thrustVec, velNorm);
                    Vector3 steerForce = thrustVec - velNorm * Mathf.Max(0f, alongVel);
                    accel = steerForce / mass;
                }
                vel += accel * dt;
            }
            else if (spaceBrakes && vel.sqrMagnitude > 0.001f)
            {
                float brakeAccel = p.BrakeDeceleration;
                Vector3 brake = -vel.normalized * brakeAccel * dt;
                if (brake.magnitude > vel.magnitude)
                    vel = Vector3.zero;
                else
                    vel += brake;
            }

            vel.y = 0f;
            float mag = vel.magnitude;
            if (mag > maxSpeed && maxSpeed > 0.001f)
            {
                float effectiveRecoilDecay = p.RecoilDecayPerSecond / mass;
                float targetMag = Mathf.MoveTowards(mag, maxSpeed, effectiveRecoilDecay * dt);
                vel = vel.normalized * targetMag;
            }

            state.Velocity = vel;
        }

        private static void ApplyElectricShockBraking(ref ShipMotorState state, float brakeDeceleration, float dt)
        {
            Vector3 vel = state.Velocity;
            vel.y = 0f;
            if (vel.sqrMagnitude <= 0.001f)
            {
                state.Velocity = Vector3.zero;
                return;
            }
            float mass = Mathf.Max(0.5f, state.Mass);
            float brakeForce = brakeDeceleration * mass * 2.5f;
            Vector3 decel = -vel.normalized * (brakeForce / mass) * dt;
            if (decel.magnitude > vel.magnitude)
                vel = Vector3.zero;
            else
                vel += decel;
            vel.y = 0f;
            state.Velocity = vel;
        }

        private static void IntegratePosition(ref ShipMotorState state, float dt)
        {
            state.Position += state.Velocity * dt;
        }

        public static void ApplyVelocityImpulse(ref ShipMotorState state, Vector3 deltaVelocity)
        {
            deltaVelocity.y = 0f;
            state.Velocity += deltaVelocity;
            state.Velocity.y = 0f;
        }

        public static void SetVelocity(ref ShipMotorState state, Vector3 velocity)
        {
            velocity.y = 0f;
            state.Velocity = velocity;
        }

        public static void SnapState(ref ShipMotorState state, Vector3 position, Quaternion rotation, Vector3 velocity, float fixedY)
        {
            position.y = fixedY;
            velocity.y = 0f;
            state.Position = position;
            state.Rotation = rotation;
            state.Velocity = velocity;
        }
    }
}
