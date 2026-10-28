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

        /// <summary>Elapsed simulation seconds — moon orbit phase and dock attach / takeoff.</summary>
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

        [ReadOnly] public ComponentLookup<PhysicsCollider> PhysicsColliders;

        /// <summary>1 on clients so unowned AI hulls stay on the interpolated snapshot.</summary>
        public byte SkipBots;

        /// <summary>
        /// Per-ship motor tick. Writes velocity and yaw. Position is integrated by
        /// <see cref="ShipDeterministicTickSystem"/> except while moon-docked or taking off,
        /// where this motor already pinned the hull.
        /// </summary>
        void Execute(
            RefRO<ShipInput> input,
            RefRO<ShipMotorConfig> motor,
            RefRW<ShipMoonDockState> moonDock,
            RefRO<ShipTurretControlState> turretControl,
            RefRW<ShipState> shipState,
            RefRW<PhysicsVelocity> physicsVelocity,
            RefRW<ShipKinematics> kinematics,
            RefRW<PhysicsDamping> physicsDamping,
            RefRW<LocalTransform> transform,
            RefRW<ShipOrbitState> orbitState,
            RefRW<ShipTerritoryBoostLatch> territoryLatch,
            RefRO<ShipAsteroidContactState> asteroidContact,
            RefRO<ShipElectricShockState> electricShock,
            RefRO<MegaShipState> megaState,
            RefRO<GhostOwner> ghostOwner,
            Entity entity)
        {
            if (SkipBots != 0 && BotShipIds.IsBot(ghostOwner.ValueRO.NetworkId))
            {
                // Leave the ghost velocity alone. Zero only the solver body so Unity Physics
                // cannot walk an interpolated bot off the snapshot.
                physicsVelocity.ValueRW = PhysicsVelocity.Zero;
                return;
            }

            // Flight speed lives on the ghosted kinematics. The solver body is zeroed
            // after the tick so ExportPhysicsWorld cannot wipe the accumulated speed.
            var seeded = physicsVelocity.ValueRO;
            seeded.Linear = kinematics.ValueRO.Velocity;
            seeded.Linear.y = 0f;
            seeded.Angular = float3.zero;
            physicsVelocity.ValueRW = seeded;

            // --- Stowed in planetary defense turret: freeze hull (server + predicted client) ---
            // [TITAN-ORBIT] Aim/Fire still flow through ShipInput for the pad; thrust = exit on server.
            if (turretControl.ValueRO.IsControlling)
            {
                CommitFlight(ref physicsVelocity, ref kinematics, float3.zero);
                physicsDamping.ValueRW = default;
                shipState.ValueRW.OverdriveBurstActive = false;
                ShipPhysicsDriveLogic.ClearManualAim(ref shipState.ValueRW);
                return;
            }

            // --- Electric shock: lock thrust, turn, and leave velocity at zero ---
            if (electricShock.ValueRO.IsActive(Elapsed))
            {
                CommitFlight(ref physicsVelocity, ref kinematics, float3.zero);
                physicsDamping.ValueRW = default;
                shipState.ValueRW.OverdriveBurstActive = false;
                ShipPhysicsDriveLogic.ClearManualAim(ref shipState.ValueRW);
                return;
            }

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
                isMegaShip: megaState.ValueRO.IsMega,
                shipPhysicsRadius: PhysicsColliders.HasComponent(entity)
                    ? ShipPhysicsDriveLogic.MeasurePhysicsHullRadiusXZ(
                        PhysicsColliders[entity], transform.ValueRO)
                    : -1f);

            CommitFlight(ref physicsVelocity, ref kinematics, physicsVelocity.ValueRO.Linear);
        }

        /// <summary>
        /// Stores the motor result on <see cref="ShipKinematics"/>. That is the value the next
        /// tick integrates. <see cref="PhysicsVelocity"/> is kept in step until the tick zeros it.
        /// </summary>
        static void CommitFlight(ref RefRW<PhysicsVelocity> physicsVelocity, ref RefRW<ShipKinematics> kinematics, float3 linear)
        {
            linear.y = 0f;
            var kin = kinematics.ValueRO;
            kin.Velocity = linear;
            kinematics.ValueRW = kin;
            var pv = physicsVelocity.ValueRO;
            pv.Linear = linear;
            pv.Angular = float3.zero;
            physicsVelocity.ValueRW = pv;
        }
    }
}
