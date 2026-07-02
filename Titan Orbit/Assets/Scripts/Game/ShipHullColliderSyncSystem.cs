using TitanOrbit.ECS;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.Game
{
    public static class ShipHullColliderSyncLogic
    {
        public static void SyncFromProxyRegistry(EntityManager em)
        {
            using var query = em.CreateEntityQuery(
                ComponentType.ReadOnly<GhostOwner>(),
                ComponentType.ReadOnly<ShipTag>());
            using var entities = query.ToEntityArray(Unity.Collections.Allocator.Temp);
            using var owners = query.ToComponentDataArray<GhostOwner>(Unity.Collections.Allocator.Temp);

            for (int i = 0; i < entities.Length; i++)
            {
                int networkId = owners[i].NetworkId;
                if (networkId <= 0)
                    continue;

                if (!ShipWeaponProxyRegistry.TryGetHull(networkId, out var hullRoot) || hullRoot == null)
                    continue;

                var cache = hullRoot.GetComponent<ShipHullColliderCache>();
                if (cache == null || cache.Colliders == null || cache.Colliders.Count == 0)
                    continue;

                float hullScale = math.max(0.001f, hullRoot.lossyScale.x);
                var entity = entities[i];
                if (!em.HasBuffer<ShipHullColliderElement>(entity))
                    em.AddBuffer<ShipHullColliderElement>(entity);

                var buffer = em.GetBuffer<ShipHullColliderElement>(entity);
                buffer.Clear();
                for (int c = 0; c < cache.Colliders.Count; c++)
                {
                    var source = cache.Colliders[c];
                    buffer.Add(new ShipHullColliderElement
                    {
                        LocalCenter = source.LocalCenter * hullScale,
                        LocalRotation = source.LocalRotation,
                        HalfExtents = source.HalfExtents * hullScale,
                    });
                }
            }
        }
    }

    [UpdateInGroup(typeof(Unity.Entities.SimulationSystemGroup))]
    [UpdateBefore(typeof(ShipMovementSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    public partial class ShipHullColliderSyncSystem : Unity.Entities.SystemBase
    {
        protected override void OnUpdate()
        {
            ShipHullColliderSyncLogic.SyncFromProxyRegistry(EntityManager);
        }
    }

    [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
    [UpdateBefore(typeof(ShipClientPredictedMovementSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    public partial class ShipHullColliderClientSyncSystem : Unity.Entities.SystemBase
    {
        protected override void OnUpdate()
        {
            ShipHullColliderSyncLogic.SyncFromProxyRegistry(EntityManager);
        }
    }
}
