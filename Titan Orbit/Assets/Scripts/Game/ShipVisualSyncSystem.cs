using TitanOrbit.ECS;
using Unity.Entities;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;

namespace TitanOrbit.Game
{
    // Order: NetCode presentation → ShipVisualSyncSystem (last in PresentationSystemGroup) → EcsWorldVisualizer LateUpdate
    /// <summary>
    /// Captures ghost <see cref="LocalTransform"/> after NetCode presentation so GameObject proxies
    /// and client VFX read the correct interpolation phase via <see cref="GhostPresentationTransformCache"/>.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(PresentationSystemGroup), OrderLast = true)]
    public partial class ShipVisualSyncSystem : SystemBase
    {
        protected override void OnUpdate()
        {
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
