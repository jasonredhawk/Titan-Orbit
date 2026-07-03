using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace TitanOrbit.ECS
{
    /// <summary>Shared deterministic ship motor step for server authority and client prediction.</summary>
    public static class ShipMovementLogic
    {
        const float FixedY = 0f;
        const float AimPointDistance = 100f;

        public static void GetMapSize(EntityManager em, out float mapW, out float mapH)
        {
            mapW = 1000f;
            mapH = 1000f;
            using var query = em.CreateEntityQuery(typeof(MapStateSingleton));
            if (query.TryGetSingleton<MapStateSingleton>(out var map))
            {
                mapW = math.max(100f, map.MapWidth);
                mapH = math.max(100f, map.MapHeight);
            }
        }

        public static void StepShip(
            EntityManager em,
            float dt,
            float mapW,
            float mapH,
            double elapsedSeconds,
            RefRO<ShipInput> input,
            RefRO<ShipMotorConfig> motor,
            RefRW<ShipState> shipState,
            RefRW<ShipKinematics> kinematics,
            RefRW<LocalTransform> transform,
            Entity entity)
        {
            if (shipState.ValueRO.IsDead || shipState.ValueRO.AwaitingTeamSelection)
                return;

            if (!em.HasComponent<ShipOrbitState>(entity))
                em.AddComponentData(entity, new ShipOrbitState());
            if (!em.HasComponent<ShipMoonDockState>(entity))
                em.AddComponentData(entity, new ShipMoonDockState());

            var inp = input.ValueRO;
            var moonDock = em.GetComponentData<ShipMoonDockState>(entity);
            if (moonDock.MoonPlanetId != 0 &&
                moonDock.LandingProgress >= GemEconomyConstants.MoonLandingCompleteThreshold &&
                !inp.Thrust)
            {
                kinematics.ValueRW = new ShipKinematics { Velocity = float3.zero };
                em.SetComponentData(entity, new ShipOrbitState());
                return;
            }

            var cfg = motor.ValueRO;
            float3 pos = transform.ValueRO.Position;

            var motorState = new ShipMotorState
            {
                Position = pos,
                Rotation = transform.ValueRO.Rotation,
                Velocity = kinematics.ValueRO.Velocity,
                Mass = cfg.Mass,
            };

            Vector2 aimWorldXz = AimWorldPoint(pos, transform.ValueRO.Rotation, inp.AimPlanarDir);

            bool inOrbitRing = TryFindOrbitPlanet(em, pos, mapW, mapH, out var orbitPlanetState, out var orbitPlanetTransform);
            bool useOrbit = inOrbitRing && !inp.Thrust && !inp.Fire.IsSet;

            var tickParams = new ShipMotorTickParams
            {
                FixedDeltaTime = dt,
                EngineThrust = cfg.EngineThrust,
                MaxSpeed = cfg.MaxSpeed,
                RotationSpeedDegPerSec = cfg.RotationSpeed,
                BrakeDeceleration = cfg.BrakeDeceleration,
                RecoilDecayPerSecond = cfg.RecoilDecayPerSecond > 0f ? cfg.RecoilDecayPerSecond : 6f,
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
                    cfg.Mass,
                    mapW,
                    mapH,
                    out float3 desiredVel,
                    out float alignRate);
                tickParams.OrbitDesiredVelocity = new Vector3(desiredVel.x, 0f, desiredVel.z);
                tickParams.OrbitAlignRate = alignRate;
            }

            ShipMotorSimulator.Step(
                ref motorState,
                in tickParams,
                aimWorldXz,
                inp.Thrust,
                inp.SpaceBrakes);

            ShipCollisionLogic.ResolveMovement(
                em,
                entity,
                pos,
                transform.ValueRO.Rotation,
                ref motorState,
                transform.ValueRO.Scale,
                mapW,
                mapH,
                elapsedSeconds);

            // Ships stay in unwrapped world space; presentation repositions other bodies via ToroidalDisplay.
            transform.ValueRW.Position = motorState.Position;
            transform.ValueRW.Rotation = motorState.Rotation;
            kinematics.ValueRW.Velocity = motorState.Velocity;

            em.SetComponentData(entity, new ShipOrbitState
            {
                OrbitPlanetId = inOrbitRing ? orbitPlanetState.PlanetId : 0,
                InOrbitRing = inOrbitRing,
                UsingOrbitMotor = useOrbit,
            });
        }

        static bool TryFindOrbitPlanet(
            EntityManager em,
            float3 shipPos,
            float mapW,
            float mapH,
            out PlanetState planetState,
            out LocalTransform planetTransform)
        {
            planetState = default;
            planetTransform = default;

            float bestDist = float.MaxValue;
            bool found = false;

            using var query = em.CreateEntityQuery(
                ComponentType.ReadOnly<PlanetTag>(),
                ComponentType.ReadOnly<PlanetState>(),
                ComponentType.ReadOnly<LocalTransform>());
            using var states = query.ToComponentDataArray<PlanetState>(Allocator.Temp);
            using var transforms = query.ToComponentDataArray<LocalTransform>(Allocator.Temp);

            for (int i = 0; i < states.Length; i++)
            {
                var state = states[i];
                var planetXform = transforms[i];
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

        static Vector2 AimWorldPoint(float3 shipPos, quaternion rot, float2 aimPlanarDir)
        {
            if (math.lengthsq(aimPlanarDir) > 0.01f)
            {
                float2 dir = math.normalize(aimPlanarDir);
                return new Vector2(
                    shipPos.x + dir.x * AimPointDistance,
                    shipPos.z + dir.y * AimPointDistance);
            }

            float3 forward = math.mul(rot, new float3(0f, 0f, 1f));
            forward.y = 0f;
            if (math.lengthsq(forward) < 0.0001f)
                forward = new float3(0f, 0f, 1f);
            else
                forward = math.normalize(forward);

            return new Vector2(
                shipPos.x + forward.x * AimPointDistance,
                shipPos.z + forward.z * AimPointDistance);
        }
    }
}
