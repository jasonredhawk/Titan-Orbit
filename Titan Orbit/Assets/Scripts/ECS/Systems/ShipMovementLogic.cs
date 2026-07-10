using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Managed helpers for ship movement systems. Runs in the main-thread portion of
    /// <see cref="ShipMovementSystem"/> and <see cref="ShipClientPredictedMovementSystem"/>
    /// before the Burst <see cref="ShipMovementJob"/> is scheduled. Reads map singletons
    /// that cannot be accessed from Burst jobs without extra setup.
    /// </summary>
    public static class ShipMovementLogic
    {
        /// <summary>
        /// Reads toroidal map dimensions from <see cref="MapStateSingleton"/>, or falls back
        /// to 1000×1000 when the singleton is missing (early bootstrap).
        /// </summary>
        public static void GetMapSize(ref SystemState state, out float mapW, out float mapH)
        {
            // [STANDARD] Safe defaults so orbit/distance math never divides by zero.
            mapW = 1000f;
            mapH = 1000f;

            // [ECS/DOTS] CreateEntityQuery (not state.GetEntityQuery) — caller-owned; safe to dispose.
            using var query = state.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<MapStateSingleton>());
            if (query.TryGetSingleton<MapStateSingleton>(out var map))
            {
                mapW = math.max(100f, map.MapWidth);
                mapH = math.max(100f, map.MapHeight);
            }
        }
    }

    /// <summary>
    /// Burst-inlined ship motor step shared by server authority and client prediction.
    /// Called from <see cref="ShipMovementJob"/> — logic lives here (not on individual helpers)
    /// because per-method [BurstCompile] on static helpers causes BC1064 AOT failures.
    /// Paired with <see cref="ShipMovementSystem"/> (server) and
    /// <see cref="ShipClientPredictedMovementSystem"/> (local owner).
    /// Writes <see cref="PhysicsVelocity"/> and <see cref="LocalTransform.Rotation"/> only;
    /// Unity Physics integrates hull position next (see ship-simulation rule).
    /// </summary>
    public static class ShipMovementBurstLogic
    {
        const float FixedY = 0f;
        const float AimPointDistance = 100f;

        /// <summary>
        /// One fixed-timestep motor tick for a single ship entity. Reads player input and planet
        /// snapshots, runs <see cref="ShipMotorSimulator"/>, applies moon-shield repel, then
        /// hands velocity and facing to Unity Physics.
        /// </summary>
        public static void Step(
            in ShipInput input,
            in ShipMotorConfig motor,
            in ShipMoonDockState moonDock,
            ref ShipState shipState,
            ref ShipKinematics kinematics,
            ref PhysicsVelocity physicsVelocity,
            ref LocalTransform transform,
            ref ShipOrbitState orbitState,
            in NativeArray<PlanetMotorSnapshot> planets,
            float dt,
            float mapW,
            float mapH,
            double elapsedSeconds)
        {
            // --- Early out: dead, team select, or docked on moon ---
            // [TITAN-ORBIT] Dead ships and team-pick screens must not receive motor thrust.
            if (shipState.IsDead || shipState.AwaitingTeamSelection)
            {
                physicsVelocity = PhysicsVelocity.Zero;
                return;
            }

            // [TITAN-ORBIT] Fully landed on a friendly moon with no thrust — pin in place.
            // ShipMoonDockSystem handles the cinematic landing; motor yields until thrust undocks.
            if (moonDock.MoonPlanetId != 0 &&
                moonDock.LandingProgress >= GemEconomyConstants.MoonLandingCompleteThreshold &&
                !input.Thrust)
            {
                kinematics.Velocity = float3.zero;
                physicsVelocity = PhysicsVelocity.Zero;
                orbitState = default;
                return;
            }

            // --- Gather state for the shared motor simulator ---
            float3 pos = transform.Position;
            // [TITAN-ORBIT] Heavier ships (more HP bulk, gems) accelerate slower but keep top speed.
            float effectiveMass = ShipMassLogic.ComputeMovementMass(
                motor.HullMassReference,
                shipState.MaxHealth,
                motor.ChassisReferenceHealth,
                shipState.CurrentGems,
                motor.Mass > 0f ? motor.Mass : ShipMassLogic.DefaultBaseMass);

            var motorState = new ShipMotorState
            {
                Position = pos,
                Rotation = transform.Rotation,
                // [TITAN-ORBIT] Start from post-physics velocity so collision bounce carries into the next step.
                Velocity = physicsVelocity.Linear,
                Mass = effectiveMass,
            };

            // --- Aim target: mouse direction or current ship forward ---
            AimWorldPoint(in pos, in transform.Rotation, in input.AimPlanarDir, out float2 aimWorldXz);

            // --- Orbit ring detection (auto-orbit when coasting near a planet) ---
            bool inOrbitRing = TryFindOrbitPlanet(pos, mapW, mapH, in planets, out var orbitPlanetState, out var orbitPlanetTransform);
            // [TITAN-ORBIT] Thrust or firing cancels passive orbit — player intent overrides auto-orbit.
            bool useOrbit = inOrbitRing && !input.Thrust && !input.Fire.IsSet;

            var tickParams = new ShipMotorTickParams
            {
                FixedDeltaTime = dt,
                EngineThrust = motor.EngineThrust,
                MaxSpeed = motor.MaxSpeed,
                RotationSpeedDegPerSec = motor.RotationSpeed,
                BrakeDeceleration = motor.BrakeDeceleration,
                RecoilDecayPerSecond = motor.RecoilDecayPerSecond > 0f ? motor.RecoilDecayPerSecond : 6f,
                FixedY = FixedY,
                UseOrbit = useOrbit,
            };

            if (useOrbit)
            {
                // [TITAN-ORBIT] PlanetOrbitMath computes tangential velocity for the ring the ship is in.
                PlanetOrbitMath.BuildOrbitMotorParams(
                    pos,
                    orbitPlanetTransform.Position,
                    orbitPlanetTransform.Scale,
                    orbitPlanetState.PlanetLevel,
                    effectiveMass,
                    mapW,
                    mapH,
                    out float3 desiredVel,
                    out float alignRate);
                tickParams.OrbitDesiredVelocity = new float3(desiredVel.x, 0f, desiredVel.z);
                tickParams.OrbitAlignRate = alignRate;
            }

            // --- Motor tick: thrust, brakes, aim rotation (no position integration) ---
            // [TITAN-ORBIT] integratePosition: false — PhysicsSystemGroup owns hull position and bounce contacts.
            ShipMotorSimulator.Step(
                ref motorState,
                in tickParams,
                in aimWorldXz,
                input.Thrust,
                input.SpaceBrakes,
                integratePosition: false);

            // --- Shield repel (deterministic gameplay overlay; moons have no physics colliders) ---
            PlanetGemMoonCombatLogic.ApplyShieldRepelIfNeeded(
                ref motorState,
                shipState.Team,
                in planets,
                mapW,
                mapH,
                elapsedSeconds);

            // --- Physics handoff: motor owns facing + desired velocity; physics owns position ---
            float3 vel = motorState.Velocity;
            vel.y = 0f; // [TITAN-ORBIT] Top-down space — Y is locked at zero.
            transform.Rotation = motorState.Rotation;
            physicsVelocity = new PhysicsVelocity { Linear = vel, Angular = float3.zero };
            // [TITAN-ORBIT] ShipKinematics mirrors physics linear vel for gameplay reads (HUD, tractor beam).
            kinematics.Velocity = vel;

            // --- Replicate orbit context for HUD and downstream systems ---
            orbitState = new ShipOrbitState
            {
                OrbitPlanetId = inOrbitRing ? orbitPlanetState.PlanetId : 0,
                InOrbitRing = inOrbitRing,
                UsingOrbitMotor = useOrbit,
            };
        }

        /// <summary>
        /// Finds the nearest planet whose orbit ring contains the ship position.
        /// Uses toroidal distance so wrap-around maps behave correctly.
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
                float dist = ToroidalMapEcs.ToroidalDistance(shipPos, planetXform.Position, mapW, mapH);
                if (!PlanetOrbitMath.IsInOrbitRing(dist, inner, outer))
                    continue;

                // [STANDARD] Prefer the closest planet when multiple rings overlap.
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
        /// Converts aim input into a world-space XZ point the motor rotates toward.
        /// Falls back to ship forward when the player is not actively aiming.
        /// </summary>
        static void AimWorldPoint(in float3 shipPos, in quaternion rot, in float2 aimPlanarDir, out float2 aimWorldXz)
        {
            if (math.lengthsq(aimPlanarDir) > 0.01f)
            {
                float2 dir = math.normalize(aimPlanarDir);
                aimWorldXz = new float2(
                    shipPos.x + dir.x * AimPointDistance,
                    shipPos.z + dir.y * AimPointDistance);
                return;
            }

            // [STANDARD] No aim input — keep facing current forward direction.
            float3 forward = math.mul(rot, new float3(0f, 0f, 1f));
            forward.y = 0f;
            if (math.lengthsq(forward) < 0.0001f)
                forward = new float3(0f, 0f, 1f);
            else
                forward = math.normalize(forward);

            aimWorldXz = new float2(
                shipPos.x + forward.x * AimPointDistance,
                shipPos.z + forward.z * AimPointDistance);
        }
    }
}
