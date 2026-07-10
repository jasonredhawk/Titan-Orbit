using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Pre-bakes runtime ship components (kinematics, orbit/dock state, weapons) so motor hot paths
    /// never call AddComponent per tick. Runs before <see cref="ShipMovementSystem"/> and
    /// <see cref="ShipClientPredictedMovementSystem"/>.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(ShipMovementSystem))]
    [UpdateBefore(typeof(ShipClientPredictedMovementSystem))]
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
                    EnergyCostPerShot = 8f,
                    BulletLifetime = 3f,
                    BulletMaxDistance = 200f,
                    MuzzleOffset = 2f,
                    BulletScale = 1f,
                    ReferenceBulletDamage = 8f,
                    ReferenceBulletSpeed = 20f,
                });
            }

            foreach (var (_, entity) in SystemAPI.Query<RefRO<ShipTag>>()
                         .WithNone<ShipVitalsConfig>()
                         .WithEntityAccess())
            {
                ecb.AddComponent(entity, new ShipVitalsConfig
                {
                    HealthRegenPerSecond = 6f,
                    EnergyRegenPerSecond = 5f,
                    HealthRegenDelayAfterDamage = 0.35f,
                });
            }

            foreach (var (_, entity) in SystemAPI.Query<RefRO<ShipTag>>()
                         .WithNone<ShipVitalsState>()
                         .WithEntityAccess())
                ecb.AddComponent(entity, new ShipVitalsState());

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

                if (!state.EntityManager.HasBuffer<ShipWingTractorBeamElement>(entity))
                    ecb.AddBuffer<ShipWingTractorBeamElement>(entity);
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
                         .WithNone<ShipDepositIntent>()
                         .WithEntityAccess())
                ecb.AddComponent(entity, new ShipDepositIntent());

            foreach (var (_, entity) in SystemAPI.Query<RefRO<ShipTag>>()
                         .WithNone<ShipLoadoutState>()
                         .WithEntityAccess())
                ecb.AddComponent(entity, new ShipLoadoutState());

            foreach (var (_, entity) in SystemAPI.Query<RefRO<ShipTag>>().WithEntityAccess())
            {
                if (!state.EntityManager.HasBuffer<EquippedEquipmentElement>(entity))
                    ecb.AddBuffer<EquippedEquipmentElement>(entity);
            }

            foreach (var (_, entity) in SystemAPI.Query<RefRO<ShipTag>>()
                         .WithNone<ShipPeopleTransferState>()
                         .WithEntityAccess())
                ecb.AddComponent(entity, new ShipPeopleTransferState());

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }
}
