using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Physics;
using Unity.Physics.Systems;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// After Unity Physics integrates hull motion, mirrors linear velocity into <see cref="ShipKinematics"/>
    /// for ghosts, HUD, and combat. Clamps speed to <see cref="ShipMotorConfig.MaxSpeed"/>.
    /// </summary>
    [UpdateInGroup(typeof(PredictedFixedStepSimulationSystemGroup))]
    [UpdateAfter(typeof(PhysicsSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    public partial struct ShipKinematicsSyncSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            foreach (var (motor, velocity, kinematics, shipState) in SystemAPI
                         .Query<RefRO<ShipMotorConfig>, RefRW<PhysicsVelocity>, RefRW<ShipKinematics>, RefRO<ShipState>>()
                         .WithAll<ShipTag, Simulate>())
            {
                if (shipState.ValueRO.IsDead || shipState.ValueRO.AwaitingTeamSelection)
                {
                    velocity.ValueRW = PhysicsVelocity.Zero;
                    kinematics.ValueRW = new ShipKinematics { Velocity = float3.zero };
                    continue;
                }

                float3 linear = velocity.ValueRO.Linear;
                linear.y = 0f;

                float maxSpeed = math.max(0.1f, motor.ValueRO.MaxSpeed);
                float speed = math.length(linear);
                if (speed > maxSpeed)
                    linear = linear * (maxSpeed / speed);

                velocity.ValueRW = new PhysicsVelocity
                {
                    Linear = linear,
                    Angular = velocity.ValueRO.Angular,
                };

                kinematics.ValueRW = new ShipKinematics { Velocity = linear };
            }
        }
    }
}
