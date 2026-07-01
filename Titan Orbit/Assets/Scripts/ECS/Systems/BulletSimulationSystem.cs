using TitanOrbit.Core;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Physics;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    [BurstCompile]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    public partial struct BulletSimulationSystem : ISystem
    {
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<ActiveBulletsTag>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var bulletEntity = SystemAPI.GetSingletonEntity<ActiveBulletsTag>();
            var bullets = state.EntityManager.GetBuffer<BulletElement>(bulletEntity);
            var spawnEvents = state.EntityManager.GetBuffer<BulletSpawnEventElement>(bulletEntity);
            float dt = SystemAPI.Time.DeltaTime;

            for (int i = bullets.Length - 1; i >= 0; i--)
            {
                var b = bullets[i];
                b.Age += dt;
                b.Traveled += math.length(b.Velocity) * dt;
                b.Position += b.Velocity * dt;

                if (b.Age >= b.Lifetime || b.Traveled >= b.MaxDistance)
                {
                    bullets.RemoveAtSwapBack(i);
                    continue;
                }

                foreach (var (shipState, shipTransform, shipEntity) in SystemAPI
                             .Query<RefRO<ShipState>, RefRO<LocalTransform>>()
                             .WithAll<ShipTag>()
                             .WithEntityAccess())
                {
                    if (shipState.ValueRO.IsDead) continue;
                    float dist = math.distance(b.Position, shipTransform.ValueRO.Position);
                    if (dist < 2f && shipState.ValueRO.Team != (TeamId)b.OwnerTeam)
                    {
                        var writable = SystemAPI.GetComponentRW<ShipState>(shipEntity);
                        writable.ValueRW.Health -= b.Damage;
                        if (writable.ValueRW.Health <= 0f)
                            writable.ValueRW.IsDead = true;
                        bullets.RemoveAtSwapBack(i);
                        goto nextBullet;
                    }
                }

                bullets[i] = b;
                nextBullet: ;
            }

            foreach (var (input, shipState, transform, ghostOwner) in SystemAPI
                         .Query<RefRO<ShipInput>, RefRO<ShipState>, RefRO<LocalTransform>, RefRO<GhostOwner>>()
                         .WithAll<ShipTag>())
            {
                if (shipState.ValueRO.IsDead || !input.ValueRO.Fire.IsSet)
                    continue;

                float3 fwd = math.mul(transform.ValueRO.Rotation, new float3(0f, 0f, 1f));
                var spawn = new BulletElement
                {
                    Position = transform.ValueRO.Position + fwd * 2f,
                    Velocity = fwd * 80f,
                    MaxDistance = 200f,
                    Lifetime = 3f,
                    Damage = 10f,
                    OwnerNetworkId = ghostOwner.ValueRO.NetworkId,
                    OwnerTeam = (byte)shipState.ValueRO.Team,
                    Sequence = (uint)state.WorldUnmanaged.Time.ElapsedTime,
                };
                bullets.Add(spawn);
                spawnEvents.Add(new BulletSpawnEventElement
                {
                    SpawnPosition = spawn.Position,
                    Velocity = spawn.Velocity,
                    Lifetime = spawn.Lifetime,
                    Damage = spawn.Damage,
                    OwnerTeam = spawn.OwnerTeam,
                    Sequence = spawn.Sequence,
                });
            }
        }
    }
}
