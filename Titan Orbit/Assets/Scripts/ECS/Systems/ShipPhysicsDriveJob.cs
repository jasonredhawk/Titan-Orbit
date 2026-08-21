using TitanOrbit.Data;
using TitanOrbit.Simulation;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Physics;
using Unity.Transforms;
using RuntimeTriangle = TitanOrbit.Simulation.PlanetConnectionGraphLogic.RuntimeTriangle;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Burst parallel job — applies shared <see cref="ShipPhysicsDriveLogic"/> for each predicted
    /// ship before the Unity Physics solver. [NETCODE] <see cref="Simulate"/> limits client work to
    /// owner-predicted ghosts; server runs all simulated ships.
    /// Planet snapshots + map size + territory triangles are collected once on the main thread so
    /// every ship shares the same toroidal orbit / shield / territory inputs this tick.
    /// Mass-tax weights are copied from <see cref="ShipCargoMobilitySettings"/> on the main thread
    /// (Burst cannot read ScriptableObjects).
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

        /// <summary>Baked planet-center territory triangles (may be empty).</summary>
        [ReadOnly] public NativeArray<RuntimeTriangle> TerritoryTriangles;

        /// <summary>Home planet level per TeamId byte index (length ≥ 6).</summary>
        [ReadOnly] public NativeArray<int> HomeLevelByTeam;

        // --- Subtractive mass tax (from ShipCargoMobilitySettings, main-thread copy) ---
        public float MassPerGem;
        public float MassPerPerson;
        public float MassPerComponentSize;
        public float SpeedWeightPerMass;
        public float AccelWeightPerMass;
        public float TurnWeightPerMass;
        public float MinSpeed;
        public float MinAccel;
        public float MinTurn;

        /// <summary>
        /// Per-ship motor tick. Writes velocity, yaw, <see cref="ShipOrbitState"/>,
        /// <see cref="ShipTerritoryBoostLatch"/>, <see cref="ShipMoonDockState"/> takeoff
        /// fields, and <see cref="ShipState.OverdriveLockout"/>.
        /// Position stays physics-owned except while fully moon-docked (surface attach)
        /// or taking off (forced outward exit).
        /// </summary>
        void Execute(
            RefRO<ShipInput> input,
            RefRO<ShipMotorConfig> motor,
            RefRW<ShipMoonDockState> moonDock,
            RefRO<ShipTurretControlState> turretControl,
            RefRW<ShipState> shipState,
            RefRW<PhysicsVelocity> physicsVelocity,
            RefRW<PhysicsDamping> physicsDamping,
            RefRW<LocalTransform> transform,
            RefRW<ShipOrbitState> orbitState,
            RefRW<ShipTerritoryBoostLatch> territoryLatch,
            RefRO<ShipAsteroidContactState> asteroidContact,
            RefRO<ShipElectricShockState> electricShock,
            RefRO<MegaShipState> megaState)
        {
            // --- Stowed in planetary defense turret: freeze hull (server + predicted client) ---
            // [TITAN-ORBIT] Aim/Fire still flow through ShipInput for the pad; thrust = exit on server.
            if (turretControl.ValueRO.IsControlling)
            {
                physicsVelocity.ValueRW = PhysicsVelocity.Zero;
                physicsDamping.ValueRW = default;
                return;
            }

            // --- Electric shock: lock thrust, turn, and leave velocity at zero ---
            if (electricShock.ValueRO.IsActive(Elapsed))
            {
                physicsVelocity.ValueRW = PhysicsVelocity.Zero;
                physicsDamping.ValueRW = default;
                return;
            }

            // Ship↔ship inward reject is unused (PhysX owns those pairs). This Execute stays at
            // 13 component params. A 14th Ref / Entity / ComponentLookup Burst-NRE'd Player 2.
            ShipPhysicsDriveLogic.Step(
                input.ValueRO,
                motor.ValueRO,
                ref moonDock.ValueRW,
                ref shipState.ValueRW,
                ref physicsVelocity.ValueRW,
                ref physicsDamping.ValueRW,
                ref transform.ValueRW,
                ref orbitState.ValueRW,
                ref territoryLatch.ValueRW,
                asteroidContact.ValueRO,
                default,
                in Planets,
                Dt,
                MapW,
                MapH,
                Elapsed,
                in TerritoryTriangles,
                in HomeLevelByTeam,
                MassPerGem,
                MassPerPerson,
                MassPerComponentSize,
                SpeedWeightPerMass,
                AccelWeightPerMass,
                TurnWeightPerMass,
                MinSpeed,
                MinAccel,
                MinTurn,
                skipMassTax: motor.ValueRO.SkipMassTax != 0,
                isMegaShip: megaState.ValueRO.IsMega);
        }
    }
}
