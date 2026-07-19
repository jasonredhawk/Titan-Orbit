using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.ECS.Authoring;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;

namespace TitanOrbit.Game
{
    // --- Type members ---
    /// <summary>
    /// Server-only: copies weapon mount transforms from ship visual hull proxies into the ship ghost's
    /// ShipWeaponMountElement buffer each frame. Hull proxies are registered by network id in
    /// Reads weapon mount transforms from ship entity buffers. Runs after ShipPhysicsDriveSystem, before
    /// BulletSimulationSystem so muzzle poses are current for shooting.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(ShipPhysicsDriveSystem))]
    [UpdateBefore(typeof(BulletSimulationSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    public partial class ShipWeaponMountSyncSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            if (TitanOrbitPresentationConfig.UseEntitiesGraphicsForShips)
                return;

            foreach (var (owner, entity) in SystemAPI.Query<RefRO<GhostOwner>>()
                         .WithAll<ShipTag>()
                         .WithEntityAccess())
            {
                // [TITAN-ORBIT] Visual hull is GameObject-only; sim reads baked buffer on ghost.
                if (!ShipWeaponProxyRegistry.TryGetHull(owner.ValueRO.NetworkId, out var hullRoot))
                    continue;

                var mountAuthorings = hullRoot.GetComponentsInChildren<ShipWeaponMountAuthoring>(true);
                if (mountAuthorings == null || mountAuthorings.Length == 0)
                    continue;

                if (!EntityManager.HasBuffer<ShipWeaponMountElement>(entity))
                    EntityManager.AddBuffer<ShipWeaponMountElement>(entity);

                var buffer = EntityManager.GetBuffer<ShipWeaponMountElement>(entity);
                buffer.Clear();

                for (int i = 0; i < mountAuthorings.Length; i++)
                {
                    var mountAuth = mountAuthorings[i];
                    if (mountAuth == null || mountAuth.transform == hullRoot)
                        continue;

                    // [TITAN-ORBIT] Hull-root-local (nested Weapon children) — same as catalog bake.
                    ShipChassisPrefabBakeUtility.GetHullRootLocalPose(
                        hullRoot, mountAuth.transform, out var localPos, out var localRot);
                    buffer.Add(new ShipWeaponMountElement
                    {
                        LocalPosition = localPos,
                        LocalRotation = localRot,
                        DirectionAngleDeg = mountAuth.DirectionAngleDeg,
                        CannonIndex = mountAuth.CannonIndex,
                    });
                }
            }
        }
    }
}
