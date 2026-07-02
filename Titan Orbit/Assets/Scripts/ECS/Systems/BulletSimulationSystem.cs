using TitanOrbit.Core;
using TitanOrbit.Simulation;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    [BurstCompile]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(ShipMovementSystem))]
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
            if (!SystemAPI.TryGetSingletonEntity<ActiveBulletsTag>(out var bulletEntity))
                return;

            if (!state.EntityManager.HasBuffer<BulletElement>(bulletEntity) ||
                !state.EntityManager.HasBuffer<BulletSpawnEventElement>(bulletEntity))
                return;

            var bullets = state.EntityManager.GetBuffer<BulletElement>(bulletEntity);
            var spawnEvents = state.EntityManager.GetBuffer<BulletSpawnEventElement>(bulletEntity);
            float dt = SystemAPI.Time.DeltaTime;

            for (int i = bullets.Length - 1; i >= 0; i--)
            {
                var b = bullets[i];
                float3 prevPos = b.Position;
                float3 newPos = prevPos + b.Velocity * dt;
                float stepDistance = math.distance(prevPos, newPos);

                b.Age += dt;
                b.Traveled += stepDistance;

                if (b.Age >= b.Lifetime || b.Traveled >= b.MaxDistance)
                {
                    bullets.RemoveAtSwapBack(i);
                    continue;
                }

                bool hit = false;

                foreach (var (shipState, shipTransform, shipEntity) in SystemAPI
                             .Query<RefRO<ShipState>, RefRO<LocalTransform>>()
                             .WithAll<ShipTag>()
                             .WithEntityAccess())
                {
                    if (shipState.ValueRO.IsDead) continue;
                    if (shipState.ValueRO.Team == (TeamId)b.OwnerTeam) continue;

                    if (!BulletCollision.SegmentHitsSphere(prevPos, newPos, shipTransform.ValueRO.Position, 2f, out float3 hitPoint))
                        continue;

                    var writable = SystemAPI.GetComponentRW<ShipState>(shipEntity);
                    writable.ValueRW.Health -= b.Damage;
                    if (writable.ValueRW.Health <= 0f)
                        writable.ValueRW.IsDead = true;
                    hit = true;
                    break;
                }

                if (!hit)
                {
                    foreach (var (asteroidState, asteroidTransform, asteroidEntity) in SystemAPI
                                 .Query<RefRW<AsteroidState>, RefRO<LocalTransform>>()
                                 .WithAll<AsteroidTag>()
                                 .WithEntityAccess())
                    {
                        if (asteroidState.ValueRO.IsDestroyed)
                            continue;

                        float hitRadius = BulletCollision.AsteroidHitRadius(asteroidTransform.ValueRO.Scale);
                        if (!BulletCollision.SegmentHitsSphere(
                                prevPos, newPos, asteroidTransform.ValueRO.Position, hitRadius, out _))
                            continue;

                        var asteroid = asteroidState.ValueRO;
                        asteroid.Health -= b.Damage;
                        if (asteroid.Health <= 0f)
                        {
                            asteroid.Health = 0f;
                            asteroid.IsDestroyed = true;
                        }

                        asteroidState.ValueRW = asteroid;
                        hit = true;
                        break;
                    }
                }

                if (!hit)
                {
                    foreach (var (transport, transform, transportEntity) in SystemAPI
                                 .Query<RefRW<PeopleTransportState>, RefRO<LocalTransform>>()
                                 .WithAll<PeopleTransportTag>()
                                 .WithEntityAccess())
                    {
                        ref var t = ref transport.ValueRW;
                        if (t.Amount <= 0f || t.Health <= 0f)
                            continue;

                        var sourceTeam = (TeamId)t.Team;
                        var ownerTeam = (TeamId)b.OwnerTeam;
                        if (sourceTeam == TeamId.None || sourceTeam == ownerTeam)
                            continue;

                        float hitRadius = PeopleTransportMath.GetBulletHitRadius(transform.ValueRO.Scale);
                        if (!BulletCollision.SegmentHitsSphere(
                                prevPos, newPos, transform.ValueRO.Position, hitRadius, out _))
                            continue;

                        t.Health -= b.Damage;
                        if (t.Health <= 0f)
                            PeopleTransportSimulationSystem.DestroyFromBulletDamage(ref state, transportEntity, t);

                        hit = true;
                        break;
                    }
                }

                if (hit)
                {
                    bullets.RemoveAtSwapBack(i);
                    continue;
                }

                b.Position = newPos;
                bullets[i] = b;
            }

            foreach (var (input, weaponCfg, weaponState, shipState, kinematics, transform, ghostOwner, entity) in SystemAPI
                         .Query<RefRO<ShipInput>, RefRO<ShipWeaponConfig>, RefRW<ShipWeaponState>, RefRO<ShipState>, RefRO<ShipKinematics>, RefRO<LocalTransform>, RefRO<GhostOwner>>()
                         .WithAll<ShipTag>()
                         .WithEntityAccess())
            {
                if (shipState.ValueRO.IsDead)
                    continue;

                float cooldown = weaponState.ValueRO.FireCooldown;
                if (cooldown > 0f)
                {
                    cooldown = math.max(0f, cooldown - dt);
                    weaponState.ValueRW.FireCooldown = cooldown;
                }

                if (!input.ValueRO.Fire.IsSet)
                    continue;

                float fireRate = math.max(0.1f, weaponCfg.ValueRO.FireRate);
                if (cooldown > 0f)
                    continue;

                if (!SystemAPI.HasBuffer<ShipWeaponMountElement>(entity))
                    continue;

                var mounts = SystemAPI.GetBuffer<ShipWeaponMountElement>(entity);
                if (mounts.Length == 0)
                    continue;

                int mountIdx = weaponState.ValueRO.NextMountIndex;
                if (mountIdx < 0)
                    mountIdx = 0;
                mountIdx %= mounts.Length;
                var mount = mounts[mountIdx];

                float3 fireOrigin;
                float3 fireForward;
                if (!ShipWeaponPose.TryResolve(transform.ValueRO, mount, out fireOrigin, out fireForward))
                {
                    float3 localFwd = math.mul(mount.LocalRotation, new float3(0f, 0f, 1f));
                    localFwd.y = 0f;
                    if (math.lengthsq(localFwd) < 0.0001f)
                        localFwd = new float3(0f, 0f, 1f);
                    else
                        localFwd = math.normalize(localFwd);
                    fireForward = math.rotate(transform.ValueRO.Rotation, localFwd);
                    fireForward.y = 0f;
                    if (math.lengthsq(fireForward) < 0.0001f)
                        fireForward = new float3(0f, 0f, 1f);
                    else
                        fireForward = math.normalize(fireForward);
                    fireOrigin = transform.ValueRO.Position + math.rotate(transform.ValueRO.Rotation, mount.LocalPosition);
                    fireOrigin.y = transform.ValueRO.Position.y;
                }

                float3 shipVel = kinematics.ValueRO.Velocity;
                shipVel.y = 0f;
                float3 bulletVel = fireForward * math.max(1f, weaponCfg.ValueRO.BulletSpeed) + shipVel;
                var spawn = new BulletElement
                {
                    Position = fireOrigin,
                    Velocity = bulletVel,
                    MaxDistance = math.max(10f, weaponCfg.ValueRO.BulletMaxDistance),
                    Lifetime = math.max(0.1f, weaponCfg.ValueRO.BulletLifetime),
                    Damage = math.max(1f, weaponCfg.ValueRO.BulletDamage),
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

                weaponState.ValueRW.FireCooldown = 1f / fireRate;
                weaponState.ValueRW.NextMountIndex = (mountIdx + 1) % mounts.Length;
            }
        }
    }
}
