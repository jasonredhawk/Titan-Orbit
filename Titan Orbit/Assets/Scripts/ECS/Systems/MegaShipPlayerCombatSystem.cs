using TitanOrbit.Core;
using TitanOrbit.Data;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server: while a ship has <see cref="ShipMegaGunControlState.IsControlling"/>, Aim yaws
    /// that MEGA mount independently and Fire shoots along the barrel (not the hull).
    /// The MEGA owner's Fire only shoots unoccupied mounts (<see cref="MegaShipAutoFireSystem"/>).
    /// Damage and energy cost are that mount's FirePower only.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(MegaShipAutoFireSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    public partial class MegaShipPlayerCombatSystem : SystemBase
    {
        readonly System.Collections.Generic.Dictionary<int, float> _nextFireTime =
            new System.Collections.Generic.Dictionary<int, float>(32);

        /// <summary>Require bullets + map.</summary>
        protected override void OnCreate()
        {
            RequireForUpdate<ActiveBulletsTag>();
            RequireForUpdate<MapStateSingleton>();
        }

        /// <summary>
        /// Yaw occupied MEGA mounts toward the gunner's aim every tick; fire along the
        /// barrel when Fire is held and the mount has its own firepower.
        /// </summary>
        protected override void OnUpdate()
        {
            if (!SystemAPI.TryGetSingletonEntity<ActiveBulletsTag>(out var bulletEntity))
                return;
            if (!EntityManager.HasBuffer<BulletElement>(bulletEntity) ||
                !EntityManager.HasBuffer<BulletSpawnEventElement>(bulletEntity))
                return;

            float now = (float)SystemAPI.Time.ElapsedTime;
            var bullets = EntityManager.GetBuffer<BulletElement>(bulletEntity);
            var spawnEvents = EntityManager.GetBuffer<BulletSpawnEventElement>(bulletEntity);
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (control, input, ghostOwner, gunnerEntity) in SystemAPI
                         .Query<RefRO<ShipMegaGunControlState>, RefRO<ShipInput>, RefRO<GhostOwner>>()
                         .WithAll<ShipTag>()
                         .WithEntityAccess())
            {
                if (!control.ValueRO.IsControlling)
                    continue;
                if (!MegaShipGunnerLogic.TryFindMegaByOwnerNetworkId(
                        EntityManager, control.ValueRO.MegaOwnerNetworkId, out Entity mega))
                    continue;
                if (!EntityManager.GetComponentData<MegaShipState>(mega).IsMega)
                    continue;

                var megaShip = EntityManager.GetComponentData<ShipState>(mega);
                if (megaShip.IsDead)
                    continue;

                var xf = EntityManager.GetComponentData<LocalTransform>(mega);
                var mounts = EntityManager.GetBuffer<ShipWeaponMountElement>(mega);
                byte mountIndex = control.ValueRO.MountIndex;
                if (mountIndex >= mounts.Length)
                    continue;

                var mount = mounts[mountIndex];
                float2 aim2 = input.ValueRO.AimPlanarDir;
                float3 desiredAim = new float3(aim2.x, 0f, aim2.y);
                if (math.lengthsq(desiredAim) < 0.01f)
                    desiredAim = math.mul(xf.Rotation, new float3(0f, 0f, 1f));
                desiredAim.y = 0f;
                desiredAim = math.normalizesafe(desiredAim, new float3(0f, 0f, 1f));

                if (!input.ValueRO.Fire.IsSet)
                    continue;

                var weapon = EntityManager.HasComponent<ShipWeaponConfig>(mega)
                    ? EntityManager.GetComponentData<ShipWeaponConfig>(mega)
                    : default;
                float fireRate = math.max(0.15f, mount.FireRate > 0.01f ? mount.FireRate : weapon.FireRate);
                int cooldownKey = gunnerEntity.Index;
                if (_nextFireTime.TryGetValue(cooldownKey, out float next) && now < next)
                    continue;

                float3 muzzle = MegaShipGunnerLogic.GetMountWorldPosition(xf, mount);
                float3 aim = desiredAim;

                int bankIndex = 0;
                if (EntityManager.HasComponent<ShipLoadoutState>(mega))
                    bankIndex = BulletBankFireResolve.ResolveFireBankIndex(
                        EntityManager.GetComponentData<ShipLoadoutState>(mega));

                // Per-mount firepower only — 0 means unarmed (do not fall back to hull sum).
                float damage = mount.FirePower;
                if (damage <= 0.01f)
                    continue;

                megaShip = EntityManager.GetComponentData<ShipState>(mega);
                if (megaShip.CurrentEnergy < damage)
                    continue;
                megaShip.CurrentEnergy = math.max(0f, megaShip.CurrentEnergy - damage);
                EntityManager.SetComponentData(mega, megaShip);

                float bulletSpeed = math.max(4f, weapon.BulletSpeed);
                float range = mount.BulletRange > 0.5f
                    ? mount.BulletRange
                    : MegaShipCatalog.DefaultBulletAcquireRange;
                uint sequence = BulletVfxBridge.NextSequence();
                var spawn = new BulletElement
                {
                    Position = muzzle,
                    Velocity = aim * bulletSpeed,
                    MaxDistance = range,
                    Lifetime = 0f,
                    Damage = damage,
                    OwnerNetworkId = ghostOwner.ValueRO.NetworkId,
                    OwnerTeam = (byte)megaShip.Team,
                    Sequence = sequence,
                    BankIndex = math.max(0, bankIndex),
                    ScaleMultiplier = 1.2f,
                    DamageFilter = BulletDamageFilter.Everything,
                };

                spawnEvents.Add(new BulletSpawnEventElement
                {
                    SpawnPosition = spawn.Position,
                    Velocity = spawn.Velocity,
                    Lifetime = spawn.Lifetime,
                    MaxDistance = spawn.MaxDistance,
                    Damage = spawn.Damage,
                    OwnerTeam = spawn.OwnerTeam,
                    Sequence = spawn.Sequence,
                    BankIndex = spawn.BankIndex,
                    ScaleMultiplier = spawn.ScaleMultiplier,
                });
                BulletNetNotify.SendSpawn(ref ecb, spawn, mountIndex);
                bullets.Add(spawn);
                _nextFireTime[cooldownKey] = now + (1f / fireRate);
            }

            ecb.Playback(EntityManager);
            ecb.Dispose();
        }
    }
}
