using Unity.Entities;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>Predicts local ship movement on online clients so the ghost stays in sync with input between snapshots.</summary>
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    public partial class ShipClientPredictedMovementSystem : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<ShipMotorConfig>();
            RequireForUpdate<NetworkStreamInGame>();
        }

        protected override void OnUpdate()
        {
            // Local host simulates on the server; client ghost only receives snapshots.
            if (IsLocalHostPlay())
                return;

            float dt = SystemAPI.Time.DeltaTime;
            ShipMovementLogic.GetMapSize(EntityManager, out float mapW, out float mapH);

            foreach (var (input, motor, shipState, kinematics, transform, entity) in SystemAPI
                         .Query<RefRO<ShipInput>, RefRO<ShipMotorConfig>, RefRW<ShipState>, RefRW<ShipKinematics>, RefRW<LocalTransform>>()
                         .WithAll<ShipTag>()
                         .WithAny<GhostOwnerIsLocal, LocalPlayerShipTag>()
                         .WithEntityAccess())
            {
                ShipMovementLogic.StepShip(EntityManager, dt, mapW, mapH, input, motor, shipState, kinematics, transform, entity);
            }
        }

        static bool IsLocalHostPlay()
        {
            var server = ClientServerBootstrap.ServerWorld;
            if (server == null || !server.IsCreated)
                return false;

            using var query = server.EntityManager.CreateEntityQuery(typeof(NetworkStreamInGame));
            return query.CalculateEntityCount() > 0;
        }
    }
}
