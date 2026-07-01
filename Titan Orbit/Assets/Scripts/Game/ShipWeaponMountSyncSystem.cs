using TitanOrbit.ECS;
using TitanOrbit.ECS.Authoring;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;

namespace TitanOrbit.Game
{
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
