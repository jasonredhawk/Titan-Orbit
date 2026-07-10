using Unity.Entities;
using Unity.NetCode;
using Unity.Physics;
using Unity.Physics.Systems;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Authoritative ship motor (server only). Client owner prediction runs in
    /// <see cref="ShipClientPredictedMovementSystem"/>.
    /// </summary>
    [UpdateInGroup(typeof(PredictedFixedStepSimulationSystemGroup))]
    [UpdateBefore(typeof(PhysicsSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    public partial class ShipMovementSystem : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<ShipMotorConfig>();
        }

        protected override void OnUpdate()
        {
            float dt = SystemAPI.Time.DeltaTime;
            double elapsed = SystemAPI.Time.ElapsedTime;
            ShipMovementLogic.GetMapSize(EntityManager, out float mapW, out float mapH);

            foreach (var (input, motor, shipState, kinematics, physicsVelocity, transform, entity) in SystemAPI
                         .Query<RefRO<ShipInput>, RefRO<ShipMotorConfig>, RefRW<ShipState>, RefRW<ShipKinematics>, RefRW<PhysicsVelocity>, RefRW<LocalTransform>>()
                         .WithAll<ShipTag, Simulate>()
                         .WithEntityAccess())
            {
                ShipMovementLogic.StepShip(EntityManager, dt, mapW, mapH, elapsed, input, motor, shipState, kinematics, physicsVelocity, transform, entity);
            }
        }
    }
}
