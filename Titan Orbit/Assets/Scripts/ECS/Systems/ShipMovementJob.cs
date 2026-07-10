using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using Unity.Physics;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Burst-compiled parallel job — one invocation per ship entity in the prediction loop.
    /// Scheduled by both <see cref="ShipMovementSystem"/> (server) and
    /// <see cref="ShipClientPredictedMovementSystem"/> (client owner). Delegates all motor math
    /// to <see cref="ShipMovementBurstLogic.Step"/> so server and client stay bit-identical.
    /// </summary>
    [BurstCompile]
    [WithAll(typeof(ShipTag), typeof(Simulate))]
    public partial struct ShipMovementJob : IJobEntity
    {
        public float Dt;
        public double Elapsed;
        public float MapW;
        public float MapH;
        [ReadOnly] public NativeArray<PlanetMotorSnapshot> Planets;

        /// <summary>
        /// Per-ship motor tick. Component refs map directly to the entity's ghost/sim components.
        /// </summary>
        void Execute(
            RefRO<ShipInput> input,
            RefRO<ShipMotorConfig> motor,
            RefRO<ShipMoonDockState> moonDock,
            RefRW<ShipState> shipState,
            RefRW<ShipKinematics> kinematics,
            RefRW<PhysicsVelocity> physicsVelocity,
            RefRW<LocalTransform> transform,
            RefRW<ShipOrbitState> orbitState)
        {
            ShipMovementBurstLogic.Step(
                input.ValueRO,
                motor.ValueRO,
                moonDock.ValueRO,
                ref shipState.ValueRW,
                ref kinematics.ValueRW,
                ref physicsVelocity.ValueRW,
                ref transform.ValueRW,
                ref orbitState.ValueRW,
                in Planets,
                Dt,
                MapW,
                MapH,
                Elapsed);
        }
    }
}
