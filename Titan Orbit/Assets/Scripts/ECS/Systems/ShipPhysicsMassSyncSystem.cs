using TitanOrbit.Data;
using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Physics;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Syncs each ship's <see cref="PhysicsMass"/> from gameplay movement mass
    /// (hull, HP bulk, current gems, current people) before thrust runs.
    /// [NETCODE] Server and client must use identical mass during owner prediction.
    /// Paired with <see cref="ShipPhysicsDriveLogic"/> which divides thrust by the same mass value.
    /// </summary>
    // OrderFirst: before drive systems. Do not UpdateBefore server-only or client-only drive types —
    // the missing peer on the other world spams invalid UpdateBefore warnings at bootstrap.
    [UpdateInGroup(typeof(PredictedFixedStepSimulationSystemGroup), OrderFirst = true)]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    public partial struct ShipPhysicsMassSyncSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            // [TITAN-ORBIT] Client: skip during TeamChoice / ship Instantiates holds only
            // (ShouldSkipShipSimulation). Map Instantiates backlog must not freeze mass sync or
            // thrust feels stuck after Join Team. IsClient() — Local Host shares settle statics.
            if (state.World.IsClient() && ClientJoinSettleCache.ShouldSkipShipSimulation)
                return;

            if (state.World.IsClient())
            {
                foreach (var (motor, shipState, mega, physicsMass, collider) in SystemAPI
                             .Query<RefRO<ShipMotorConfig>, RefRO<ShipState>, RefRO<MegaShipState>,
                                 RefRW<PhysicsMass>, RefRO<PhysicsCollider>>()
                             .WithAll<ShipTag, Simulate, PredictedGhost>())
                    SyncMass(motor, shipState, mega, physicsMass, collider);
            }
            else
            {
                foreach (var (motor, shipState, mega, physicsMass, collider) in SystemAPI
                             .Query<RefRO<ShipMotorConfig>, RefRO<ShipState>, RefRO<MegaShipState>,
                                 RefRW<PhysicsMass>, RefRO<PhysicsCollider>>()
                             .WithAll<ShipTag, Simulate>())
                    SyncMass(motor, shipState, mega, physicsMass, collider);
            }
        }

        /// <summary>Writes gameplay mass onto the physics body (same formula as the motor).</summary>
        static void SyncMass(
            RefRO<ShipMotorConfig> motor,
            RefRO<ShipState> shipState,
            RefRO<MegaShipState> mega,
            RefRW<PhysicsMass> physicsMass,
            RefRO<PhysicsCollider> collider)
        {
            float baseMass = motor.ValueRO.Mass > 0f ? motor.ValueRO.Mass : ShipMassLogic.DefaultBaseMass;
            float movementMass = ShipMassLogic.ComputeMovementMass(
                motor.ValueRO.HullMassReference,
                shipState.ValueRO.MaxHealth,
                motor.ValueRO.ChassisReferenceHealth,
                shipState.ValueRO.CurrentGems,
                baseMass,
                shipState.ValueRO.CurrentPeople);

            if (mega.ValueRO.IsMega)
                movementMass = math.max(movementMass, MegaShipCatalog.DefaultHullCollisionMass);

            movementMass = math.max(ShipMassLogic.MinMass, movementMass);
            physicsMass.ValueRW = PhysicsMass.CreateDynamic(
                collider.ValueRO.Value.Value.MassProperties,
                movementMass);
        }
    }
}
