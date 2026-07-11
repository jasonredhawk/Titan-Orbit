using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using Unity.Physics;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Burst-compiled parallel job — one invocation per ship entity in the NetCode prediction loop.
    /// Scheduled by <see cref="ShipMovementSystem"/> (server authority) and
    /// <see cref="ShipClientPredictedMovementSystem"/> (local owner). All motor math lives in
    /// <see cref="ShipMovementBurstLogic.Step"/> so server and client stay deterministic.
    /// </summary>
    [BurstCompile]
    [WithAll(typeof(ShipTag), typeof(Simulate))]
    public partial struct ShipMovementJob : IJobEntity
    {
        // --- Type members ---
        /// <summary>Fixed delta time for this simulation step.</summary>
        public float Dt;

        /// <summary>Elapsed simulation time in seconds (moon dock dwell, etc.).</summary>
        public double Elapsed;

        /// <summary>Toroidal/flat map width for orbit and shield math.</summary>
        public float MapW;

        /// <summary>Toroidal/flat map height for orbit and shield math.</summary>
        public float MapH;

        /// <summary>Read-only planet snapshots collected on main thread before ScheduleParallel.</summary>
        [ReadOnly] public NativeArray<PlanetMotorSnapshot> Planets;

        /// <summary>
        /// Per-ship motor tick. Component refs map directly to ghost/sim components on the entity.
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
            // [ECS/DOTS] Single shared Burst entry — no forked client/server motor paths.
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
