using TitanOrbit.Generation;
using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    [BurstCompile]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    public partial struct ShipMovementSystem : ISystem
    {
        const float FixedY = 0f;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<ShipMotorConfig>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            float dt = SystemAPI.Time.DeltaTime;
            float mapW = 1000f;
            float mapH = 1000f;
            if (SystemAPI.TryGetSingleton<MapStateSingleton>(out var map))
            {
                mapW = math.max(100f, map.MapWidth);
                mapH = math.max(100f, map.MapHeight);
            }

            foreach (var (input, motor, shipState, kinematics, transform) in SystemAPI
                         .Query<RefRO<ShipInput>, RefRO<ShipMotorConfig>, RefRW<ShipState>, RefRW<ShipKinematics>, RefRW<LocalTransform>>()
                         .WithAll<ShipTag>())
            {
                if (shipState.ValueRO.IsDead)
                    continue;
                if (shipState.ValueRO.AwaitingTeamSelection)
                    continue;

                var cfg = motor.ValueRO;
                var inp = input.ValueRO;
                float3 pos = transform.ValueRO.Position;
                pos.y = FixedY;

                quaternion rot = transform.ValueRO.Rotation;
                float3 aimDir = new float3(inp.AimPlanarDir.x, 0f, inp.AimPlanarDir.y);
                if (math.lengthsq(aimDir) > 0.01f)
                {
                    quaternion target = quaternion.LookRotationSafe(math.normalize(aimDir), math.up());
                    rot = math.slerp(rot, target, math.min(1f, cfg.RotationSpeed * dt * math.PI / 180f));
                }

                float3 vel = kinematics.ValueRO.Velocity;
                vel.y = 0f;

                float3 thrustDir = float3.zero;
                if (math.lengthsq(inp.MovePlanarDir) > 0.01f)
                    thrustDir = math.normalize(new float3(inp.MovePlanarDir.x, 0f, inp.MovePlanarDir.y));
                else if (inp.Thrust)
                {
                    thrustDir = math.mul(rot, new float3(0f, 0f, 1f));
                    thrustDir.y = 0f;
                    if (math.lengthsq(thrustDir) > 0.0001f)
                        thrustDir = math.normalize(thrustDir);
                }

                if (inp.Thrust && math.lengthsq(thrustDir) > 0.0001f)
                    vel += thrustDir * cfg.EngineThrust * dt / math.max(0.5f, cfg.Mass);
                else if (inp.SpaceBrakes && math.lengthsq(vel) > 0.0001f)
                    vel += -math.normalize(vel) * cfg.BrakeDeceleration * dt;

                float speed = math.length(vel);
                if (speed > cfg.MaxSpeed && cfg.MaxSpeed > 0.001f)
                    vel = vel * (cfg.MaxSpeed / speed);

                pos += vel * dt;
                pos = ToroidalMapEcs.Wrap(pos, mapW, mapH);
                transform.ValueRW.Position = pos;
                transform.ValueRW.Rotation = rot;
                kinematics.ValueRW.Velocity = vel;
            }
        }
    }
}
