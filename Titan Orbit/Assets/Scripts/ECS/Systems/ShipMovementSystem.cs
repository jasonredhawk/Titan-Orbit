using Unity.Entities;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>Authoritative ship motor using the same deterministic logic as the legacy Starship motor.</summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
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
            ShipMovementLogic.GetMapSize(EntityManager, out float mapW, out float mapH);

            foreach (var (input, motor, shipState, kinematics, transform, entity) in SystemAPI
                         .Query<RefRO<ShipInput>, RefRO<ShipMotorConfig>, RefRW<ShipState>, RefRW<ShipKinematics>, RefRW<LocalTransform>>()
                         .WithAll<ShipTag>()
                         .WithEntityAccess())
            {
                ShipMovementLogic.StepShip(EntityManager, dt, mapW, mapH, input, motor, shipState, kinematics, transform, entity);
            }
        }
    }
}
