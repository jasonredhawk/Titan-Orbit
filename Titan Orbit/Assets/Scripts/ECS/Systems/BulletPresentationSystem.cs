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
                    Velocity = evt.Velocity,
                    RemainingLifetime = evt.Lifetime,
                    Scale = 0.3f,
                });
            }
            events.Clear();
            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }

    [UpdateInGroup(typeof(PresentationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
    public partial struct BulletTracerUpdateSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            float dt = SystemAPI.Time.DeltaTime;
            var ecb = new EntityCommandBuffer(Unity.Collections.Allocator.Temp);

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
                tracer.ValueRW.Position += tracer.ValueRO.Velocity * dt;
                transform.ValueRW.Position = tracer.ValueRO.Position;
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }
}
