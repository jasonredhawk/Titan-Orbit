using System.Collections.Generic;
using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Physics;
using Unity.Physics.Systems;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Retired: ship↔asteroid is Unity Physics sphere↔sphere. Kept disabled so
    /// <c>UpdateAfter</c> references still compile. Do not re-enable on top of PhysX.
    /// Map size from <see cref="MapStateSingleton"/> (never invent 1000×1000).
    /// Client asteroid list is the hybrid registry — no map-body ToEntityArray.
    /// </summary>
    [UpdateInGroup(typeof(AfterPhysicsSystemGroup))]
    [UpdateAfter(typeof(ShipPhysicsContactCollectSystem))]
    [UpdateBefore(typeof(ShipCollisionBounceSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    public partial struct ShipCircleOverlapSystem : ISystem
    {
        /// <summary>Client registry scratch — quarantine-safe, reused every step.</summary>
        static readonly List<Entity> RegistryScratch = new List<Entity>(512);

        EntityQuery _serverAsteroidQuery;

        /// <summary>Require the classified-contact queue and a rolled map size.</summary>
        public void OnCreate(ref SystemState state)
        {
            // PhysX spheres own ship↔asteroid now. This pass was a second solver on top.
            state.Enabled = false;
            state.RequireForUpdate<ShipPhysicsContactQueueTag>();
            state.RequireForUpdate<MapStateSingleton>();
            _serverAsteroidQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<AsteroidTag>(),
                ComponentType.ReadOnly<AsteroidState>(),
                ComponentType.ReadOnly<LocalTransform>());
        }

        /// <summary>
        /// One ship × live-rock circle test. MEGA hulls emit contact (plow / ram) but are not
        /// pushed out — bounce restores unconstrained motion.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            bool isClient = state.World.IsClient();
            if (isClient && ClientJoinSettleCache.ShouldSkipShipSimulation)
                return;
            if (isClient && ClientJoinSettleCache.ShouldSkipMapBodyQueries)
                return;

            if (!SystemAPI.TryGetSingleton<MapStateSingleton>(out var map) ||
                !ToroidalMapEcs.IsValidMapSize(map.MapWidth, map.MapHeight))
                return;

            if (!SystemAPI.TryGetSingletonBuffer<ShipPhysicsContactElement>(out var queue))
                return;

            float mapW = map.MapWidth;
            float mapH = map.MapHeight;

            var rocks = new NativeList<Rock>(64, Allocator.Temp);
            GatherRocks(ref state, isClient, ref rocks);
            if (rocks.Length == 0)
            {
                rocks.Dispose();
                return;
            }

            var velocityLookup = SystemAPI.GetComponentLookup<PhysicsVelocity>(true);
            var preCollisionLookup = SystemAPI.GetComponentLookup<ShipPreCollisionVelocity>(true);
            var megaLookup = SystemAPI.GetComponentLookup<MegaShipState>(true);

            foreach (var (transform, shipState, shipEntity) in SystemAPI
                         .Query<RefRW<LocalTransform>, RefRO<ShipState>>()
                         .WithAll<ShipTag, PhysicsVelocity>()
                         .WithEntityAccess())
            {
                if (shipState.ValueRO.IsDead || shipState.ValueRO.AwaitingTeamSelection)
                    continue;

                float3 shipPos = transform.ValueRO.Position;
                shipPos.y = 0f;
                float shipRadius = BodyCollisionMath.GetShipHullRadiusWorld(transform.ValueRO.Scale);
                bool mega = megaLookup.HasComponent(shipEntity) && megaLookup[shipEntity].IsMega;

                float3 vShip = float3.zero;
                if (preCollisionLookup.HasComponent(shipEntity))
                    vShip = preCollisionLookup[shipEntity].Linear;
                else if (velocityLookup.HasComponent(shipEntity))
                    vShip = velocityLookup[shipEntity].Linear;
                vShip.y = 0f;

                bool moved = false;
                for (int i = 0; i < rocks.Length; i++)
                {
                    Rock rock = rocks[i];
                    float3 offset = ToroidalMapEcs.ShortestOffsetXZ(rock.Position, shipPos, mapW, mapH);
                    float distSq = math.lengthsq(offset);
                    float minDist = shipRadius + rock.Radius;
                    if (distSq >= minDist * minDist)
                        continue;

                    float dist = math.sqrt(math.max(distSq, 1e-12f));
                    float3 n = distSq > 1e-8f ? offset / dist : new float3(0f, 0f, 1f);

                    if (!mega)
                    {
                        float penetration = minDist - dist;
                        if (penetration > 0f)
                        {
                            shipPos += n * penetration;
                            shipPos.y = 0f;
                            moved = true;
                        }
                    }

                    float closing = math.max(0f, -math.dot(vShip, n));
                    queue.Add(new ShipPhysicsContactElement
                    {
                        Ship = shipEntity,
                        Other = rock.Entity,
                        NormalShipFromOther = n,
                        ClosingSpeed = closing,
                        Kind = ShipPhysicsContactKind.Asteroid,
                    });
                }

                if (!moved)
                    continue;

                shipPos = ToroidalMapEcs.Wrap(shipPos, mapW, mapH);
                var lt = transform.ValueRW;
                lt.Position = shipPos;
                transform.ValueRW = lt;
            }

            rocks.Dispose();
        }

        /// <summary>One live asteroid for the circle test.</summary>
        struct Rock
        {
            public Entity Entity;
            public float3 Position;
            public float Radius;
        }

        /// <summary>
        /// Client: registry walk. Server: cached asteroid query. Skips dead / culled rocks.
        /// </summary>
        void GatherRocks(ref SystemState state, bool isClient, ref NativeList<Rock> rocks)
        {
            var em = state.EntityManager;
            if (isClient)
            {
                AsteroidClientEntityRegistry.CopyLive(RegistryScratch);
                for (int i = 0; i < RegistryScratch.Count; i++)
                    TryAddRock(em, RegistryScratch[i], ref rocks);
                return;
            }

            var entities = _serverAsteroidQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
                TryAddRock(em, entities[i], ref rocks);
            entities.Dispose();
        }

        /// <summary>Adds one live rock. False when the entity is dead, culled, or missing pose.</summary>
        static bool TryAddRock(EntityManager em, Entity entity, ref NativeList<Rock> rocks)
        {
            if (!em.Exists(entity) ||
                !em.HasComponent<AsteroidState>(entity) ||
                !em.HasComponent<LocalTransform>(entity))
                return false;
            if (em.HasComponent<AsteroidClientCulledTag>(entity))
                return false;

            var asteroid = em.GetComponentData<AsteroidState>(entity);
            if (asteroid.IsDestroyed || asteroid.Health <= 0.01f)
                return false;

            var lt = em.GetComponentData<LocalTransform>(entity);
            float3 pos = lt.Position;
            pos.y = 0f;
            rocks.Add(new Rock
            {
                Entity = entity,
                Position = pos,
                Radius = BodyCollisionMath.GetAsteroidBodyRadiusWorld(lt.Scale),
            });
            return true;
        }
    }
}
