using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server: slews occupied MEGA mounts toward the gunner's aim. Fire itself is
    /// <see cref="BulletSimulationSystem"/> Phase B (same spawn + collide as every ship).
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(MegaShipAutoFireSystem))]
    [UpdateBefore(typeof(BulletSimulationSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    public partial class MegaShipPlayerCombatSystem : SystemBase
    {
        /// <summary>Yaw occupied mounts toward the gunner's planar aim.</summary>
        protected override void OnUpdate()
        {
            float dt = SystemAPI.Time.DeltaTime;

            foreach (var (control, input) in SystemAPI
                         .Query<RefRO<ShipMegaGunControlState>, RefRO<ShipInput>>()
                         .WithAll<ShipTag>())
            {
                if (!control.ValueRO.IsControlling)
                    continue;
                if (!MegaShipGunnerLogic.TryFindMegaByOwnerNetworkId(
                        EntityManager, control.ValueRO.MegaOwnerNetworkId, out Entity mega))
                    continue;
                if (!EntityManager.GetComponentData<MegaShipState>(mega).IsMega)
                    continue;
                if (EntityManager.GetComponentData<ShipState>(mega).IsDead)
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

                MegaShipWeaponAim.RotateMountTowardWorldDir(in xf, ref mount, desiredAim, dt);
                mounts[mountIndex] = mount;

                if (EntityManager.HasBuffer<MegaShipGunnerSlotElement>(mega))
                {
                    MegaShipWeaponAim.WriteGhostedYaw(
                        EntityManager.GetBuffer<MegaShipGunnerSlotElement>(mega),
                        mountIndex, in mount);
                }
            }
        }
    }
}
