using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>Ensures runtime-spawned ship ghosts have kinematics even if the subscene bake is stale.</summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(ShipMovementSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    public partial struct ShipEnsureComponentsSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (_, entity) in SystemAPI.Query<RefRO<ShipTag>>()
                         .WithNone<ShipKinematics>()
                         .WithEntityAccess())
                ecb.AddComponent(entity, new ShipKinematics());

            foreach (var (_, entity) in SystemAPI.Query<RefRO<ShipTag>>()
                         .WithNone<ShipWeaponConfig>()
                         .WithEntityAccess())
            {
                ecb.AddComponent(entity, new ShipWeaponConfig
                {
                    FireRate = 2f,
                    BulletSpeed = 20f,
                    BulletDamage = 8f,
                    BulletLifetime = 3f,
                    BulletMaxDistance = 200f,
                    MuzzleOffset = 2f,
                });
            }

            foreach (var (_, entity) in SystemAPI.Query<RefRO<ShipTag>>()
                         .WithNone<ShipWeaponState>()
                         .WithEntityAccess())
                ecb.AddComponent(entity, new ShipWeaponState());

            foreach (var (_, entity) in SystemAPI.Query<RefRO<ShipTag>>().WithEntityAccess())
            {
                if (!state.EntityManager.HasBuffer<ShipWeaponMountElement>(entity))
                {
                    ecb.AddBuffer<ShipWeaponMountElement>(entity);
                    ecb.AppendToBuffer(entity, new ShipWeaponMountElement
                    {
                        LocalPosition = new float3(0f, 0f, 2f),
                        LocalRotation = quaternion.identity,
                        DirectionAngleDeg = 0f,
                        CannonIndex = 0,
                    });
                }
            }

            foreach (var (_, entity) in SystemAPI.Query<RefRO<ShipTag>>()
                         .WithNone<ShipOrbitState>()
                         .WithEntityAccess())
                ecb.AddComponent(entity, new ShipOrbitState());

            foreach (var (_, entity) in SystemAPI.Query<RefRO<ShipTag>>()
                         .WithNone<ShipMoonDockState>()
                         .WithEntityAccess())
                ecb.AddComponent(entity, new ShipMoonDockState());

            foreach (var (_, entity) in SystemAPI.Query<RefRO<ShipTag>>()
                         .WithNone<ShipPeopleTransferState>()
                         .WithEntityAccess())
                ecb.AddComponent(entity, new ShipPeopleTransferState());

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }
}
