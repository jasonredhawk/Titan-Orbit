using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Collections;
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
    /// velocity (bounce is not overwritten blindly).
    /// [TITAN-ORBIT] Also detects planet orbit rings (toroidal distance), blends passive orbit
    /// velocity when coasting, writes <see cref="ShipOrbitState"/> for people-transport dwell /
    /// HUD, and applies enemy moon shield repel. Paired with <see cref="ShipPhysicsDriveSystem"/>
    /// and <see cref="ShipClientPredictedPhysicsDriveSystem"/>.
    /// </summary>
    public static class ShipPhysicsDriveLogic
    {
        /// <summary>
        /// Soft coast drag when not thrusting, braking, or in passive orbit (high friction / low agility).
        /// Units: deceleration in world-units/s² applied against velocity.
        /// </summary>
        const float CoastFriction = 4.5f;

        /// <summary>
        /// Applies player input before <see cref="Unity.Physics.Systems.PhysicsSystemGroup"/>.
        /// Starts from the previous physics step's linear velocity so asteroid bounces carry forward.
        /// </summary>
        /// <param name="input">Predicted ship input for this tick.</param>
        /// <param name="motor">Designer motor caps (thrust, max speed, turn rate).</param>
        /// <param name="moonDock">Moon landing progress — pins hull when fully landed without thrust.</param>
        /// <param name="shipState">Death / team-select / mass contributors (HP, gems).</param>
        /// <param name="physicsVelocity">Read/write linear velocity handed to Unity Physics.</param>
        /// <param name="physicsDamping">Cleared so package damping cannot fight motor curves.</param>
        /// <param name="transform">Position (read) and yaw (write); position integration is physics-owned.</param>
        /// <param name="orbitState">Replicated orbit ring context for HUD and people transports.</param>
        /// <param name="planets">Main-thread planet snapshots shared by all ships this tick.</param>
        /// <param name="dt">Fixed prediction step delta time.</param>
        /// <param name="mapW">Toroidal map width.</param>
        /// <param name="mapH">Toroidal map height.</param>
        /// <param name="elapsedSeconds">
        /// Shared moon orbit clock (<c>PlanetGemMoonOrbitClock</c> / ServerTick seconds) for shield repel phase.
        /// </param>
        public static void Step(
            in ShipInput input,
            in ShipMotorConfig motor,
            in ShipMoonDockState moonDock,
            in ShipState shipState,
            ref PhysicsVelocity physicsVelocity,
            ref PhysicsDamping physicsDamping,
            ref LocalTransform transform,
            ref ShipOrbitState orbitState,
            in NativeArray<PlanetMotorSnapshot> planets,
            float dt,
            float mapW,
            float mapH,
            double elapsedSeconds)
        {
            // --- Guard: fixed-step dt only ---
            if (dt <= 0f)
                return;

            // --- Dead / team select: freeze the hull ---
            if (shipState.IsDead || shipState.AwaitingTeamSelection)
            {
                physicsVelocity = PhysicsVelocity.Zero;
                physicsDamping = default;
                orbitState = default;
                return;
            }

            // --- Landed moon dock — hold still until thrust undocks ---
            // [TITAN-ORBIT] ShipMoonDockSystem owns landing progress; motor yields and clears orbit.
            if (moonDock.MoonPlanetId != 0 &&
                moonDock.LandingProgress >= GemEconomyConstants.MoonLandingCompleteThreshold &&
                !input.Thrust)
            {
                physicsVelocity = PhysicsVelocity.Zero;
                physicsDamping = default;
                orbitState = default;
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

            // --- Orbit ring detection (toroidal) ---
            // [TITAN-ORBIT] PeopleTransportDispatchSystem dwells on InOrbitRing; without this write,
            // load/unload never starts. Thrust or fire cancels passive orbit motor only — ring flag
            // stays true while still inside the annulus (tractor / HUD / dwell can still see it).
            bool inOrbitRing = TryFindOrbitPlanet(
                transform.Position, mapW, mapH, in planets,
                out PlanetState orbitPlanetState, out LocalTransform orbitPlanetTransform);
            bool useOrbit = inOrbitRing && !input.Thrust && !input.Fire.IsSet;

            // --- Start from post-collision velocity (Unity Physics may have bounced us last tick) ---
            float3 vel = physicsVelocity.Linear;
            vel.y = 0f;

            if (useOrbit)
            {
                // --- Passive orbit blend (replaces thrust/coast this tick) ---
                // [TITAN-ORBIT] Continuous lerp toward tangential velocity — Starblast pillar 3.
                PlanetOrbitMath.BuildOrbitMotorParams(
                    transform.Position,
                    orbitPlanetTransform.Position,
                    orbitPlanetTransform.Scale,
                    orbitPlanetState.PlanetLevel,
                    movementMass,
                    mapW,
                    mapH,
                    out float3 desiredVel,
                    out float alignRate);
                desiredVel.y = 0f;
                float t = math.saturate(alignRate * dt);
                vel = math.lerp(vel, desiredVel, t);
                vel.y = 0f;
            }
            else
            {
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
            }

            // --- Enemy moon shield repel (deterministic; moons have no physics colliders) ---
            // [TITAN-ORBIT] Must run on client prediction + server — never client-only VFX push.
            PlanetGemMoonCombatLogic.ApplyShieldRepelIfNeeded(
                transform.Position,
                ref vel,
                shipState.Team,
                in planets,
                mapW,
                mapH,
                elapsedSeconds);

            vel.y = 0f;
            physicsVelocity = new PhysicsVelocity
            {
                Linear = vel,
                Angular = float3.zero,
            };

            // [PHYSICS] Motor owns cruise feel — clear package damping so it cannot fight our curves.
            physicsDamping = default;

            // --- Replicate orbit context for HUD, tractor beam, people transports ---
            orbitState = new ShipOrbitState
            {
                OrbitPlanetId = inOrbitRing ? orbitPlanetState.PlanetId : 0,
                InOrbitRing = inOrbitRing,
                UsingOrbitMotor = useOrbit,
            };
        }

        /// <summary>
        /// Finds the nearest planet whose orbit ring contains the ship (toroidal distance).
        /// When multiple rings overlap, the closer planet wins.
        /// </summary>
        static bool TryFindOrbitPlanet(
            in float3 shipPos,
            float mapW,
            float mapH,
            in NativeArray<PlanetMotorSnapshot> planets,
            out PlanetState planetState,
            out LocalTransform planetTransform)
        {
            planetState = default;
            planetTransform = default;

            float bestDist = float.MaxValue;
            bool found = false;

            for (int i = 0; i < planets.Length; i++)
            {
                var snapshot = planets[i];
                var state = snapshot.Planet;
                var planetXform = snapshot.Transform;
                float planetSize = math.max(0.5f, planetXform.Scale);
                PlanetOrbitMath.GetRingRadiiWorld(planetSize, state.PlanetLevel, out float inner, out float outer, out _);

                // [TITAN-ORBIT] Toroidal distance — Euclidean would fail across map seams.
                float dist = ToroidalMapEcs.ToroidalDistance(shipPos, planetXform.Position, mapW, mapH);
                if (!PlanetOrbitMath.IsInOrbitRing(dist, inner, outer))
                    continue;

                if (dist >= bestDist)
                    continue;

                bestDist = dist;
                planetState = state;
                planetTransform = planetXform;
                found = true;
            }

            return found;
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
        /// Skipped entirely on ticks where passive orbit motor owns velocity.
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

            // --- H74 hard cruise lock (thrusting, small overspeed band only) ---
            // [TITAN-ORBIT] At cruise, speed can hunt a small band above MaxSpeed even with steady
            // forward thrust. That variance makes presentation step size wobble every frame
            // (expected = speed×dt), which reads as chop on a ~60 FPS client.
            // Lock the hunt band to MaxSpeed while thrusting. Larger overspeed (impacts) still
            // uses ApplyRecoilDecay + the soft 1.35× ceiling below — not zeroed here.
            float mag = math.length(vel);
            if (thrust && mag > maxSpeed && mag <= maxSpeed * 1.08f)
                vel = math.normalize(vel) * maxSpeed;

            // Soft hard ceiling — collision overspeed above this is clipped; mid-band bleeds via recoil.
            mag = math.length(vel);
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
