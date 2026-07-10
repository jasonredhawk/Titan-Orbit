using TitanOrbit.ECS;
using TitanOrbit.ECS.Authoring;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Server-only: copies weapon mount transforms from ship visual hull proxies into the ship ghost's
    /// ShipWeaponMountElement buffer each frame. Hull proxies are registered by network id in
    /// ShipWeaponProxyRegistry (EcsWorldVisualizer). Runs after ShipMovementSystem, before
    /// BulletSimulationSystem so muzzle poses are current for shooting.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(ShipMovementSystem))]
    [UpdateBefore(typeof(BulletSimulationSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    public partial class ShipWeaponMountSyncSystem : SystemBase
    {
        protected override void OnUpdate()
        {
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

                    var wt = mountAuth.transform;
                    buffer.Add(new ShipWeaponMountElement
                    {
                        LocalPosition = wt.localPosition,
                        LocalRotation = wt.localRotation,
                        DirectionAngleDeg = mountAuth.DirectionAngleDeg,
                        CannonIndex = mountAuth.CannonIndex,
                    });
                }
            }
        }
    }
}
