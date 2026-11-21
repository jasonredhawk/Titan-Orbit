using TitanOrbit.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Physics;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Ships do not solve pairs against other ships. The deterministic tick owns
    /// that bounce. Planets, moons, and asteroids stay in the mask. Runs once per
    /// frame before prediction so the blob swap is not repeated on resim.
    /// Both worlds: a solver shove on top of the tick was a second, conflicting move.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(PredictedSimulationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
    public partial struct ShipClientRemoteShipPairSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            if (state.World.IsClient() && ClientJoinSettleCache.ShouldSkipShipEntityQueries)
                return;

            var ecb = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (collider, shipState, entity) in SystemAPI
                         .Query<RefRW<PhysicsCollider>, RefRO<ShipState>>()
                         .WithAll<ShipTag>()
                         .WithNone<Prefab>()
                         .WithEntityAccess())
            {
                if (shipState.ValueRO.IsDead)
                    continue;
                if (!collider.ValueRO.Value.IsCreated)
                    continue;

                var current = collider.ValueRO.Value;
                var filter = current.Value.GetCollisionFilter();
                bool pairsWithShips = (filter.CollidesWith & TitanOrbitPhysicsLayers.Ships) != 0;
                if (SystemAPI.HasComponent<ShipClientShipPairsStripped>(entity)
                    && SystemAPI.GetComponent<ShipClientShipPairsStripped>(entity).Collider == current)
                    pairsWithShips = false;

                if (!pairsWithShips)
                {
                    if (!SystemAPI.HasComponent<ShipPlanarHullRadius>(entity))
                    {
                        ecb.AddComponent(entity, new ShipPlanarHullRadius
                        {
                            Unscaled = UnscaledRadius(current),
                        });
                    }

                    continue;
                }

                var clone = current.Value.Clone();
                filter.CollidesWith &= ~TitanOrbitPhysicsLayers.Ships;
                clone.Value.SetCollisionFilter(filter);
                collider.ValueRW = new PhysicsCollider { Value = clone };

                bool owned = SystemAPI.HasComponent<ShipRuntimeHullColliderBlob>(entity);
                if (owned)
                {
                    var previous = SystemAPI.GetComponent<ShipRuntimeHullColliderBlob>(entity);
                    if (previous.Value.IsCreated && previous.Value == current)
                        current.Dispose();
                    else if (previous.Value.IsCreated && previous.Value != clone)
                        previous.Value.Dispose();
                    SystemAPI.SetComponent(entity, new ShipRuntimeHullColliderBlob { Value = clone });
                }
                else
                {
                    ecb.AddComponent(entity, new ShipRuntimeHullColliderBlob { Value = clone });
                }

                var radius = new ShipPlanarHullRadius { Unscaled = UnscaledRadius(clone) };
                if (SystemAPI.HasComponent<ShipPlanarHullRadius>(entity))
                    SystemAPI.SetComponent(entity, radius);
                else
                    ecb.AddComponent(entity, radius);

                var stripped = new ShipClientShipPairsStripped { Collider = clone };
                if (SystemAPI.HasComponent<ShipClientShipPairsStripped>(entity))
                    SystemAPI.SetComponent(entity, stripped);
                else
                    ecb.AddComponent(entity, stripped);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }

        /// <summary>Collider-local XZ half-extent. Entity scale is applied by the touch test.</summary>
        static float UnscaledRadius(BlobAssetReference<Unity.Physics.Collider> collider)
        {
            if (!collider.IsCreated)
                return 0.7f;
            Aabb aabb = collider.Value.CalculateAabb();
            float radius = math.max(aabb.Extents.x, aabb.Extents.z) * 0.5f;
            if (radius < 0.05f)
                return 0.7f;
            return radius;
        }
    }

    /// <summary>
    /// Marks a remote hull whose collider blob already had ship pairs removed.
    /// Stops the client from cloning that blob again every frame.
    /// </summary>
    struct ShipClientShipPairsStripped : IComponentData
    {
        public BlobAssetReference<Unity.Physics.Collider> Collider;
    }
}
