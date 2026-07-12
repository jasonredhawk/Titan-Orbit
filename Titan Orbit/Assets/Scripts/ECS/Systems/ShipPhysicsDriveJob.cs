using Unity.Burst;
using Unity.Entities;
using Unity.NetCode;
using Unity.Physics;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Burst parallel job — applies standard physics input for each predicted ship before the solver.
    /// Scheduled by <see cref="ShipPhysicsDriveSystem"/> (server) and
    /// <see cref="ShipClientPredictedPhysicsDriveSystem"/> (client owner).
    /// </summary>
    [BurstCompile]
    [WithAll(typeof(ShipTag), typeof(Simulate))]
    public partial struct ShipPhysicsDriveJob : IJobEntity
    {
        /// <summary>Fixed delta time for this prediction step.</summary>
        public float Dt;

        void Execute(
            RefRO<ShipInput> input,
            RefRO<ShipMotorConfig> motor,
            RefRO<ShipState> shipState,
            RefRO<PhysicsMass> physicsMass,
            RefRW<PhysicsVelocity> physicsVelocity,
            RefRW<PhysicsDamping> physicsDamping,
            RefRW<LocalTransform> transform)
        {
            ShipPhysicsDriveLogic.Step(
                input.ValueRO,
                motor.ValueRO,
                shipState.ValueRO,
                ref physicsVelocity.ValueRW,
                ref physicsDamping.ValueRW,
                ref transform.ValueRW,
                physicsMass.ValueRO,
                Dt);
        }
    }
}
