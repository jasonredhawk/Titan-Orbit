using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Physics;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Syncs each ship's <see cref="PhysicsMass"/> from gameplay movement mass (hull, HP bulk, gems)
    /// before thrust runs. [NETCODE] Server and client must use identical mass during owner prediction.
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
            foreach (var (motor, shipState, physicsMass, collider) in SystemAPI
                         .Query<RefRO<ShipMotorConfig>, RefRO<ShipState>, RefRW<PhysicsMass>, RefRO<PhysicsCollider>>()
                         .WithAll<ShipTag, Simulate>())
            {
                float baseMass = motor.ValueRO.Mass > 0f ? motor.ValueRO.Mass : ShipMassLogic.DefaultBaseMass;
                float movementMass = ShipMassLogic.ComputeMovementMass(
                    motor.ValueRO.HullMassReference,
                    shipState.ValueRO.MaxHealth,
                    motor.ValueRO.ChassisReferenceHealth,
                    shipState.ValueRO.CurrentGems,
                    baseMass);

                movementMass = math.max(ShipMassLogic.MinMass, movementMass);
                physicsMass.ValueRW = PhysicsMass.CreateDynamic(
                    collider.ValueRO.Value.Value.MassProperties,
                    movementMass);
            }
        }
    }
}
