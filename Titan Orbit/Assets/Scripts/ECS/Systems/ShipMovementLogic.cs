using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>Map size lookup for movement systems (runs in managed OnUpdate, not Burst).</summary>
    public static class ShipMovementLogic
    {
        public static void GetMapSize(ref SystemState state, out float mapW, out float mapH)
        {
            mapW = 1000f;
            mapH = 1000f;
            // CreateEntityQuery (not state.GetEntityQuery) — caller-owned; safe to dispose.
            using var query = state.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<MapStateSingleton>());
            if (query.TryGetSingleton<MapStateSingleton>(out var map))
            {
                mapW = math.max(100f, map.MapWidth);
                mapH = math.max(100f, map.MapHeight);
            }
        }
    }

    /// <summary>
    /// Ship motor step shared by <see cref="ShipMovementSystem"/> (server) and
    /// <see cref="ShipClientPredictedMovementSystem"/> (client prediction). Inlined into
    /// <see cref="ShipMovementJob"/> — do not add [BurstCompile] on helpers (BC1064 AOT).
    /// </summary>
    public static class ShipMovementBurstLogic
    {
        const float FixedY = 0f;
        const float AimPointDistance = 100f;

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
            if (shipState.IsDead || shipState.AwaitingTeamSelection)
            {
                physicsVelocity = PhysicsVelocity.Zero;
                return;
            }

            if (moonDock.MoonPlanetId != 0 &&
                moonDock.LandingProgress >= GemEconomyConstants.MoonLandingCompleteThreshold &&
                !input.Thrust)
            {
                kinematics.Velocity = float3.zero;
                physicsVelocity = PhysicsVelocity.Zero;
                orbitState = default;
                return;
            }

            // --- Motor tick: thrust, orbit, aim rotation (no position integration) ---
            float3 pos = transform.Position;
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
                // Start from post-physics velocity so collision bounce carries into the next step.
                Velocity = physicsVelocity.Linear,
                Mass = effectiveMass,
            };

            AimWorldPoint(in pos, in transform.Rotation, in input.AimPlanarDir, out float2 aimWorldXz);

            bool inOrbitRing = TryFindOrbitPlanet(pos, mapW, mapH, in planets, out var orbitPlanetState, out var orbitPlanetTransform);
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

            // integratePosition: false — PhysicsSystemGroup owns hull position and bounce contacts.
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
            vel.y = 0f;
            transform.Rotation = motorState.Rotation;
            physicsVelocity = new PhysicsVelocity { Linear = vel, Angular = float3.zero };
            kinematics.Velocity = vel;

            orbitState = new ShipOrbitState
            {
                OrbitPlanetId = inOrbitRing ? orbitPlanetState.PlanetId : 0,
                InOrbitRing = inOrbitRing,
                UsingOrbitMotor = useOrbit,
            };
        }

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

                if (dist >= bestDist)
                    continue;

                bestDist = dist;
                planetState = state;
                planetTransform = planetXform;
                found = true;
            }

            return found;
        }

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
