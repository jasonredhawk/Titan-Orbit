using TitanOrbit.Core;
using TitanOrbit.Generation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
    public partial struct BulletPresentationSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<ActiveBulletsTag>();
        }

        public void OnUpdate(ref SystemState state)
        {
            if (!SystemAPI.TryGetSingletonEntity<ActiveBulletsTag>(out var bulletEntity))
                return;

            var events = state.EntityManager.GetBuffer<BulletSpawnEventElement>(bulletEntity);
            if (events.Length == 0)
                return;

            var ecb = new EntityCommandBuffer(Unity.Collections.Allocator.Temp);
            foreach (var evt in events)
            {
                var tracer = ecb.CreateEntity();
                ecb.AddComponent(tracer, LocalTransform.FromPositionRotationScale(evt.SpawnPosition, quaternion.identity, 0.3f));
                ecb.AddComponent(tracer, new BulletTracerState
                {
                    Position = evt.SpawnPosition,
                    SpawnPosition = evt.SpawnPosition,
                    Velocity = evt.Velocity,
                    RemainingLifetime = evt.Lifetime,
                    MaxDistance = math.max(0.5f, evt.MaxDistance),
                    Scale = 0.3f,
                    ScaleMultiplier = evt.ScaleMultiplier > 0f ? evt.ScaleMultiplier : 1f,
                    Damage = evt.Damage,
                    OwnerTeam = evt.OwnerTeam,
                    BankIndex = evt.BankIndex,
                });
            }
            events.Clear();
            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }

    [UpdateInGroup(typeof(PresentationSystemGroup))]
    [UpdateAfter(typeof(BulletPresentationSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
    public partial struct BulletTracerUpdateSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            float dt = SystemAPI.Time.DeltaTime;
            float mapW = ToroidalMapEcs.MapWidth;
            float mapH = ToroidalMapEcs.MapHeight;
            if (SystemAPI.TryGetSingleton<MapStateSingleton>(out var mapState))
            {
                mapW = mapState.MapWidth;
                mapH = mapState.MapHeight;
            }

            var ecb = new EntityCommandBuffer(Unity.Collections.Allocator.Temp);
            DynamicBuffer<BulletHitEventElement> hitEvents = default;
            bool hasHitEvents = SystemAPI.TryGetSingletonEntity<ActiveBulletsTag>(out var bulletEntity)
                                && state.EntityManager.HasBuffer<BulletHitEventElement>(bulletEntity);
            if (hasHitEvents)
                hitEvents = state.EntityManager.GetBuffer<BulletHitEventElement>(bulletEntity);

            foreach (var (tracer, transform, entity) in SystemAPI
                         .Query<RefRW<BulletTracerState>, RefRW<LocalTransform>>()
                         .WithEntityAccess())
            {
                tracer.ValueRW.RemainingLifetime -= dt;
                if (tracer.ValueRW.RemainingLifetime <= 0f)
                {
                    ecb.DestroyEntity(entity);
                    continue;
                }

                float3 prevPos = tracer.ValueRO.Position;
                float3 newPos = prevPos + tracer.ValueRO.Velocity * dt;
                bool hit = false;
                float3 hitPoint = newPos;

                foreach (var (shipState, shipTransform) in SystemAPI
                             .Query<RefRO<ShipState>, RefRO<LocalTransform>>()
                             .WithAll<ShipTag>())
                {
                    if (shipState.ValueRO.IsDead) continue;
                    if (shipState.ValueRO.Team == (TeamId)tracer.ValueRO.OwnerTeam) continue;

                    if (BulletCollision.SegmentHitsSphereToroidal(
                            prevPos, newPos, shipTransform.ValueRO.Position, 2f, mapW, mapH, out hitPoint))
                    {
                        hit = true;
                        break;
                    }
                }

                if (!hit)
                {
                    foreach (var (asteroidState, asteroidTransform) in SystemAPI
                                 .Query<RefRO<AsteroidState>, RefRO<LocalTransform>>()
                                 .WithAll<AsteroidTag>())
                    {
                        if (asteroidState.ValueRO.IsDestroyed)
                            continue;

                        float hitRadius = BulletCollision.AsteroidHitRadius(asteroidTransform.ValueRO.Scale);
                        if (BulletCollision.SegmentHitsSphereToroidal(
                                prevPos, newPos, asteroidTransform.ValueRO.Position, hitRadius, mapW, mapH, out hitPoint))
                        {
                            hit = true;
                            break;
                        }
                    }
                }

                if (hit)
                {
                    tracer.ValueRW.Position = hitPoint;
                    transform.ValueRW.Position = hitPoint;
                    if (hasHitEvents)
                    {
                        hitEvents.Add(new BulletHitEventElement
                        {
                            HitPosition = hitPoint,
                            Damage = tracer.ValueRO.Damage,
                            OwnerTeam = tracer.ValueRO.OwnerTeam,
                            BankIndex = tracer.ValueRO.BankIndex,
                            ScaleMultiplier = tracer.ValueRO.ScaleMultiplier > 0f ? tracer.ValueRO.ScaleMultiplier : 1f,
                        });
                    }
                    ecb.DestroyEntity(entity);
                    continue;
                }

                tracer.ValueRW.Position = newPos;
                transform.ValueRW.Position = newPos;
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }
}
