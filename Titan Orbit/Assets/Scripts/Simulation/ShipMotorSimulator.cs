using Unity.Mathematics;

namespace TitanOrbit.Simulation
{
    /// <summary>
    /// Fixed-step deterministic ship motor math. Shared by server authority and client prediction
    /// so both worlds produce identical velocity and rotation each tick.
    /// When <c>integratePosition</c> is false (ships with Unity Physics), only velocity and
    /// rotation are updated — the physics solver integrates hull position and resolves collisions.
    /// Inlined by <see cref="TitanOrbit.ECS.ShipMovementJob"/> — no per-method [BurstCompile]
    /// on helpers (BC1064 AOT failure if compiled separately).
    /// </summary>
    public static class ShipMotorSimulator
    {
        /// <summary>
        /// Advances one motor tick: rotate toward aim, apply thrust/brakes or orbit blend,
        /// optionally integrate position (non-physics entities only).
        /// </summary>
        /// <param name="state">Read/write motor state (position, rotation, velocity, mass).</param>
        /// <param name="p">Per-tick tuning from ship stats and world context.</param>
        /// <param name="aimWorldXZ">World-space aim point on the XZ plane (motor rotates toward this).</param>
        /// <param name="thrust">True when the player is holding forward thrust.</param>
        /// <param name="spaceBrakes">True when space-brake deceleration is enabled.</param>
        /// <param name="integratePosition">When false, caller writes PhysicsVelocity; physics owns Position.</param>
        public static void Step(
            ref ShipMotorState state,
            in ShipMotorTickParams p,
            in float2 aimWorldXZ,
            bool thrust,
            bool spaceBrakes,
            bool integratePosition = true)
        {
            float dt = p.FixedDeltaTime;
            if (dt <= 0f) return;

            // [TITAN-ORBIT] Electric shock status effect — hard brake, no rotation or thrust.
            if (p.ElectricShockDisabled)
            {
                ApplyElectricShockBraking(ref state, p.BrakeDeceleration, dt);
                state.Position.y = p.FixedY;
                return;
            }

            // --- Facing ---
            // [TITAN-ORBIT] Rotation comes from motor, not collision — ship InverseInertia is zero.
            if (!p.TheatricalRotationLocked)
                TryRotateTowardAim(ref state, in aimWorldXZ, p.RotationSpeedDegPerSec, dt);

            // --- Velocity: orbit blend or thrust/brakes ---
            if (p.UseOrbit)
            {
                // [TITAN-ORBIT] Passive orbit — lerp current velocity toward tangential orbit velocity.
                float3 currentVel = state.Velocity;
                currentVel.y = 0f;
                float3 desired = p.OrbitDesiredVelocity;
                desired.y = 0f;
                float t = math.saturate(p.OrbitAlignRate * dt);
                float3 blended = math.lerp(currentVel, desired, t);
                blended.y = 0f;
                state.Velocity = blended;
            }
            else
            {
                ApplyThrustAndBrakes(ref state, in p, thrust, spaceBrakes, dt);
            }

            // --- Position integration (legacy / non-physics path only) ---
            if (integratePosition)
            {
                IntegratePosition(ref state, dt);
                state.Position.y = p.FixedY;
            }
            // [TITAN-ORBIT] integratePosition: false — caller writes PhysicsVelocity; physics solver owns Position.
        }

        /// <summary>
        /// Slerps ship rotation toward the aim point, clamped by rotation speed × dt.
        /// </summary>
        static void TryRotateTowardAim(
            ref ShipMotorState state,
            in float2 aimWorldXZ,
            float rotationSpeedDeg,
            float dt)
        {
            float3 shipPos = state.Position;
            float3 aimPoint = new float3(aimWorldXZ.x, shipPos.y, aimWorldXZ.y);
            float3 directionToAim = aimPoint - shipPos;
            directionToAim.y = 0f;
            if (math.lengthsq(directionToAim) <= 0.001f)
                return;

            directionToAim = math.normalize(directionToAim);
            quaternion targetRotation = quaternion.LookRotationSafe(directionToAim, math.up());
            float maxRadians = math.radians(rotationSpeedDeg * dt);
            quaternion from = state.Rotation;
            quaternion to = targetRotation;
            float angle = math.angle(from, to);
            // [STANDARD] Clamp rotation step so turning speed is frame-rate independent.
            state.Rotation = angle <= maxRadians ? to : math.slerp(from, to, maxRadians / math.max(angle, 1e-6f));
        }

        /// <summary>
        /// Applies forward thrust along ship facing, space-brake deceleration, and recoil decay above max speed.
        /// </summary>
        static void ApplyThrustAndBrakes(
            ref ShipMotorState state,
            in ShipMotorTickParams p,
            bool thrust,
            bool spaceBrakes,
            float dt)
        {
            float3 vel = state.Velocity;
            vel.y = 0f;
            float mass = math.max(0.5f, state.Mass);
            float maxSpeed = p.MaxSpeed;

            // --- Thrust direction = ship forward on XZ plane ---
            float3 moveDirection = float3.zero;
            if (thrust)
            {
                float3 fwd = math.mul(state.Rotation, new float3(0f, 0f, 1f));
                fwd.y = 0f;
                if (math.lengthsq(fwd) > 0.01f)
                    moveDirection = math.normalize(fwd);
            }

            if (math.length(moveDirection) > 0.1f)
            {
                float speed = math.length(vel);
                float3 accel;
                if (speed < maxSpeed)
                {
                    // [STANDARD] F = ma → a = thrust / mass
                    accel = moveDirection * (p.EngineThrust / mass);
                }
                else
                {
                    // [TITAN-ORBIT] At max speed, thrust steers without adding forward speed (strafe-like).
                    float3 velNorm = math.normalize(vel);
                    float3 thrustVec = moveDirection * p.EngineThrust;
                    float alongVel = math.dot(thrustVec, velNorm);
                    float3 steerForce = thrustVec - velNorm * math.max(0f, alongVel);
                    accel = steerForce / mass;
                }
                vel += accel * dt;
            }
            else if (spaceBrakes && math.lengthsq(vel) > 0.001f)
            {
                // [TITAN-ORBIT] Space brakes — constant deceleration opposite velocity.
                float brakeAccel = p.BrakeDeceleration;
                float3 brake = -math.normalize(vel) * brakeAccel * dt;
                if (math.length(brake) > math.length(vel))
                    vel = float3.zero;
                else
                    vel += brake;
            }

            vel.y = 0f;

            // --- Recoil decay: speed above max bleeds off over time (gun kick, collisions) ---
            float mag = math.length(vel);
            if (mag > maxSpeed && maxSpeed > 0.001f)
            {
                float effectiveRecoilDecay = p.RecoilDecayPerSecond / mass;
                float targetMag = math.clamp(mag - effectiveRecoilDecay * dt, maxSpeed, mag);
                vel = math.normalize(vel) * targetMag;
            }

            state.Velocity = vel;
        }

        /// <summary>
        /// Stronger braking used when electric shock disables normal motor control.
        /// </summary>
        static void ApplyElectricShockBraking(ref ShipMotorState state, float brakeDeceleration, float dt)
        {
            float3 vel = state.Velocity;
            vel.y = 0f;
            if (math.lengthsq(vel) <= 0.001f)
            {
                state.Velocity = float3.zero;
                return;
            }
            float mass = math.max(0.5f, state.Mass);
            float brakeForce = brakeDeceleration * mass * 2.5f;
            float3 decel = -math.normalize(vel) * (brakeForce / mass) * dt;
            if (math.length(decel) > math.length(vel))
                vel = float3.zero;
            else
                vel += decel;
            vel.y = 0f;
            state.Velocity = vel;
        }

        /// <summary>Legacy position integration — not used when Unity Physics owns the hull.</summary>
        static void IntegratePosition(ref ShipMotorState state, float dt)
        {
            state.Position += state.Velocity * dt;
        }

        /// <summary>Adds a velocity delta (e.g. knockback) while keeping Y locked.</summary>
        public static void ApplyVelocityImpulse(ref ShipMotorState state, in float3 deltaVelocity)
        {
            float3 dv = deltaVelocity;
            dv.y = 0f;
            state.Velocity += dv;
            state.Velocity.y = 0f;
        }

        /// <summary>Replaces velocity entirely (e.g. teleport sync).</summary>
        public static void SetVelocity(ref ShipMotorState state, in float3 velocity)
        {
            float3 vel = velocity;
            vel.y = 0f;
            state.Velocity = vel;
        }

        /// <summary>Hard-resets motor state at a spawn point (respawn, dock snap).</summary>
        public static void SnapState(
            ref ShipMotorState state,
            in float3 position,
            in quaternion rotation,
            in float3 velocity,
            float fixedY)
        {
            float3 pos = position;
            float3 vel = velocity;
            pos.y = fixedY;
            vel.y = 0f;
            state.Position = pos;
            state.Rotation = rotation;
            state.Velocity = vel;
        }
    }
}
