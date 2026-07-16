using Unity.Burst;
using Unity.Entities;
using Unity.NetCode;
using Unity.Physics;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Burst parallel job — applies shared <see cref="ShipPhysicsDriveLogic"/> for each predicted
    /// ship before the Unity Physics solver. [NETCODE] <see cref="Simulate"/> limits client work to
    /// owner-predicted ghosts; server runs all simulated ships.
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
            RefRO<ShipMoonDockState> moonDock,
            RefRO<ShipState> shipState,
            RefRW<PhysicsVelocity> physicsVelocity,
            RefRW<PhysicsDamping> physicsDamping,
            RefRW<LocalTransform> transform)
        {
            ShipPhysicsDriveLogic.Step(
                input.ValueRO,
                motor.ValueRO,
                moonDock.ValueRO,
                shipState.ValueRO,
                ref physicsVelocity.ValueRW,
                ref physicsDamping.ValueRW,
                ref transform.ValueRW,
                Dt);
        }
    }
}
