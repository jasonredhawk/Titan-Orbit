using TitanOrbit.ECS;
using Unity.Entities;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;

namespace TitanOrbit.Game
{
    // [TITAN-ORBIT] Pipeline order: NetCode presentation → ShipVisualSyncSystem (last) → EcsWorldVisualizer LateUpdate
    /// <summary>
    /// Captures ghost <see cref="LocalTransform"/> after NetCode presentation interpolation so
    /// GameObject visual proxies and client VFX read the correct phase via
    /// <see cref="GhostPresentationTransformCache"/>. Runs last in PresentationSystemGroup —
    /// do not read raw sim transforms in MonoBehaviour LateUpdate for movement (see ship-simulation rule).
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(PresentationSystemGroup), OrderLast = true)]
    public partial class ShipVisualSyncSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            // [TITAN-ORBIT] Frame-stamped cache — EcsWorldVisualizer reads this in LateUpdate.
            GhostPresentationTransformCache.BeginPublish(UnityEngine.Time.frameCount);

            foreach (var (lt, entity) in SystemAPI
                         .Query<RefRO<LocalTransform>>()
                         .WithAll<ShipTag>()
                         .WithEntityAccess())
            {
                PublishShip(entity, lt.ValueRO);
            }

            foreach (var (lt, entity) in SystemAPI
                         .Query<RefRO<LocalTransform>>()
                         .WithAll<PeopleTransportTag>()
                         .WithEntityAccess())
            {
                GhostPresentationTransformCache.PublishPeopleTransport(entity, ToSnapshot(lt.ValueRO));
            }
        }

        static void PublishShip(Entity entity, in LocalTransform transform) =>
            GhostPresentationTransformCache.PublishShip(entity, ToSnapshot(transform));

        static GhostPresentationTransformCache.Snapshot ToSnapshot(in LocalTransform transform) =>
            new GhostPresentationTransformCache.Snapshot
            {
                Position = transform.Position,
                Rotation = transform.Rotation,
                Scale = transform.Scale,
            };
    }
}
