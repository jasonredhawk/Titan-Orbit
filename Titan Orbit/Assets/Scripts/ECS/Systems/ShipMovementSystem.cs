using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;

namespace TitanOrbit.ECS
{
    /// <summary>Authoritative ship motor using the same deterministic logic as the legacy Starship motor.</summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    public partial class ShipMovementSystem : SystemBase
    {
        const float FixedY = 0f;
        const float AimPointDistance = 100f;

        protected override void OnCreate()
        {
            RequireForUpdate<ShipMotorConfig>();
        }

        protected override void OnUpdate()
        {
            float dt = SystemAPI.Time.DeltaTime;
            float mapW = 1000f;
            float mapH = 1000f;
            if (SystemAPI.TryGetSingleton<MapStateSingleton>(out var map))
            {
                mapW = math.max(100f, map.MapWidth);
                mapH = math.max(100f, map.MapHeight);
            }

            foreach (var (input, motor, shipState, kinematics, transform, entity) in SystemAPI
                         .Query<RefRO<ShipInput>, RefRO<ShipMotorConfig>, RefRW<ShipState>, RefRW<ShipKinematics>, RefRW<LocalTransform>>()
                         .WithAll<ShipTag>()
                         .WithEntityAccess())
            {
                if (shipState.ValueRO.IsDead || shipState.ValueRO.AwaitingTeamSelection)
                    continue;

                if (!EntityManager.HasComponent<ShipOrbitState>(entity))
                    EntityManager.AddComponentData(entity, new ShipOrbitState());
                if (!EntityManager.HasComponent<ShipMoonDockState>(entity))
                    EntityManager.AddComponentData(entity, new ShipMoonDockState());

                var cfg = motor.ValueRO;
                var inp = input.ValueRO;
                float3 pos = transform.ValueRO.Position;

                var motorState = new ShipMotorState
                {
                    Position = pos,
                    Rotation = transform.ValueRO.Rotation,
                    Velocity = kinematics.ValueRO.Velocity,
                    Mass = cfg.Mass,
                };

                Vector2 aimWorldXz = AimWorldPoint(pos, transform.ValueRO.Rotation, inp.AimPlanarDir);

                bool inOrbitRing = TryFindOrbitPlanet(pos, mapW, mapH, out var orbitPlanet, out var orbitPlanetState, out var orbitPlanetTransform);
                bool useOrbit = inOrbitRing && !inp.Thrust;

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

                motorState.Position = ToroidalMapEcs.Wrap(motorState.Position, mapW, mapH);

                transform.ValueRW.Position = motorState.Position;
                transform.ValueRW.Rotation = motorState.Rotation;
                kinematics.ValueRW.Velocity = motorState.Velocity;

                EntityManager.SetComponentData(entity, new ShipOrbitState
                {
                    OrbitPlanetId = inOrbitRing ? orbitPlanetState.PlanetId : 0,
                    InOrbitRing = inOrbitRing,
                    UsingOrbitMotor = useOrbit,
                });
            }
        }

        bool TryFindOrbitPlanet(
            float3 shipPos,
            float mapW,
            float mapH,
            out Entity planetEntity,
            out PlanetState planetState,
            out LocalTransform planetTransform)
        {
            planetEntity = Entity.Null;
            planetState = default;
            planetTransform = default;

            float bestDist = float.MaxValue;
            bool found = false;

            foreach (var (state, planetXform, entity) in SystemAPI
                         .Query<RefRO<PlanetState>, RefRO<LocalTransform>>()
                         .WithAll<PlanetTag>()
                         .WithEntityAccess())
            {
                float planetSize = math.max(0.5f, planetXform.ValueRO.Scale);
                PlanetOrbitMath.GetRingRadiiWorld(planetSize, state.ValueRO.PlanetLevel, out float inner, out float outer, out _);
                float dist = ToroidalMapEcs.ToroidalDistance(shipPos, planetXform.ValueRO.Position, mapW, mapH);
                if (!PlanetOrbitMath.IsInOrbitRing(dist, inner, outer))
                    continue;

                if (dist >= bestDist)
                    continue;

                bestDist = dist;
                planetEntity = entity;
                planetState = state.ValueRO;
                planetTransform = planetXform.ValueRO;
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
