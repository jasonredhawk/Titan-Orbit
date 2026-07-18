using Unity.Burst;
using Unity.Collections;
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
    /// Planet snapshots + map size are collected once on the main thread so every ship shares
    /// the same toroidal orbit / shield inputs this tick.
    /// </summary>
    [BurstCompile]
    [WithAll(typeof(ShipTag), typeof(Simulate))]
    public partial struct ShipPhysicsDriveJob : IJobEntity
    {
        /// <summary>Fixed delta time for this prediction step.</summary>
        public float Dt;

        /// <summary>Elapsed simulation seconds — moon orbit phase and shield repel timing.</summary>
        public double Elapsed;

        /// <summary>Toroidal map width from <see cref="MapStateSingleton"/>.</summary>
        public float MapW;

        /// <summary>Toroidal map height from <see cref="MapStateSingleton"/>.</summary>
        public float MapH;

        /// <summary>Read-only planet snapshots collected before ScheduleParallel.</summary>
        [ReadOnly] public NativeArray<PlanetMotorSnapshot> Planets;

        /// <summary>
        /// Per-ship motor tick. Writes velocity, yaw, and <see cref="ShipOrbitState"/>;
        /// position stays physics-owned.
        /// </summary>
        void Execute(
            RefRO<ShipInput> input,
            RefRO<ShipMotorConfig> motor,
            RefRO<ShipMoonDockState> moonDock,
            RefRO<ShipState> shipState,
            RefRW<PhysicsVelocity> physicsVelocity,
            RefRW<PhysicsDamping> physicsDamping,
            RefRW<LocalTransform> transform,
            RefRW<ShipOrbitState> orbitState)
        {
            ShipPhysicsDriveLogic.Step(
                input.ValueRO,
                motor.ValueRO,
                moonDock.ValueRO,
                shipState.ValueRO,
                ref physicsVelocity.ValueRW,
                ref physicsDamping.ValueRW,
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
