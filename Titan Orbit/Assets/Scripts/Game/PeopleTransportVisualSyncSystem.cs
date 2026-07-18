using System.Collections.Generic;
using TitanOrbit.ECS;
using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Client presentation magnet for people-transport floats (load and unload).
    /// Drives <see cref="PeopleTransportPresentation"/> entities created by
    /// <see cref="PeopleTransportSpawnRpcClientSystem"/> — never waits on transport ghosts.
    /// Publishes poses into <see cref="GhostPresentationTransformCache"/> for
    /// <see cref="EcsWorldVisualizer"/>. World: ClientSimulation, after ShipVisualSyncSystem.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(PresentationSystemGroup), OrderLast = true)]
    [UpdateAfter(typeof(ShipVisualSyncSystem))]
    public partial class PeopleTransportVisualSyncSystem : SystemBase
    {
        /// <summary>Cosmetic lift so spheres clear planet/ship hulls.</summary>
        const float PresentationLiftY = 0.55f;

        /// <summary>Arrival distance — despawn presentation when this close to magnet target.</summary>
        const float ArriveDistance = 0.85f;

        readonly Dictionary<int, LocalTransform> _shipByNetworkId = new Dictionary<int, LocalTransform>(16);
        readonly Dictionary<int, LocalTransform> _planetById = new Dictionary<int, LocalTransform>(64);
        EntityQuery _presentationQuery;

        /// <summary>Caches the presentation query for empty early-out.</summary>
        protected override void OnCreate()
        {
            _presentationQuery = GetEntityQuery(
                ComponentType.ReadOnly<PeopleTransportPresentationTag>(),
                ComponentType.ReadWrite<PeopleTransportPresentation>(),
                ComponentType.ReadWrite<LocalTransform>());
        }

        /// <summary>Magnet-steers every presentation float and publishes hybrid poses.</summary>
        protected override void OnUpdate()
        {
            if (_presentationQuery.IsEmptyIgnoreFilter)
                return;

            float dt = math.min(0.05f, math.max(0f, UnityEngine.Time.deltaTime));
            if (dt <= 0f)
                return;

            GetMapSize(out float mapW, out float mapH);
            BuildTargetLookups();

            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (presentation, transform, entity) in SystemAPI
                         .Query<RefRW<PeopleTransportPresentation>, RefRW<LocalTransform>>()
                         .WithAll<PeopleTransportPresentationTag>()
                         .WithEntityAccess())
            {
                ref var p = ref presentation.ValueRW;
                p.RemainingLifetime -= dt;
                if (p.RemainingLifetime <= 0f)
                {
                    GhostPresentationTransformCache.ForgetPeopleTransport(entity);
                    ecb.DestroyEntity(entity);
                    continue;
                }

                float3 pos = transform.ValueRO.Position;
                pos.y = 0f;
                bool isLoad = p.IsLoad != 0;

                if (!TryResolveTarget(in p, isLoad, pos, mapW, mapH, out float3 target))
                {
                    Publish(entity, pos, transform.ValueRO.Scale);
                    continue;
                }

                float cruise = p.CruiseSpeed > 0.01f
                    ? p.CruiseSpeed
                    : PeopleTransportMath.ComputeCruiseSpeed(pos, target, isLoad, mapW, mapH);
                p.Velocity = PeopleTransportMath.SteerMagnetVelocity(
                    pos, target, p.Velocity, dt, cruise, mapW, mapH);
                pos += p.Velocity * dt;
                pos.y = 0f;

                // --- Arrive: end the float when near the magnet point ---
                float dist = ToroidalMapEcs.ToroidalDistance(pos, target, mapW, mapH);
                if (dist <= ArriveDistance)
                {
                    GhostPresentationTransformCache.ForgetPeopleTransport(entity);
                    ecb.DestroyEntity(entity);
                    continue;
                }

                transform.ValueRW.Position = pos;
                Publish(entity, pos, transform.ValueRO.Scale);
            }

            ecb.Playback(EntityManager);
            ecb.Dispose();
        }

        /// <summary>Ship presentation poses preferred so the float tracks the visible hull.</summary>
        void BuildTargetLookups()
        {
            _shipByNetworkId.Clear();
            foreach (var (owner, transform, entity) in SystemAPI
                         .Query<RefRO<GhostOwner>, RefRO<LocalTransform>>()
                         .WithAll<ShipTag>()
                         .WithEntityAccess())
            {
                int id = owner.ValueRO.NetworkId;
                if (id == 0)
                    continue;

                if (GhostPresentationTransformCache.TryGetShip(entity, out var snap))
                {
                    _shipByNetworkId[id] = LocalTransform.FromPositionRotationScale(
                        snap.Position, snap.Rotation, snap.Scale > 0.001f ? snap.Scale : transform.ValueRO.Scale);
                }
                else
                {
                    _shipByNetworkId[id] = transform.ValueRO;
                }
            }

            _planetById.Clear();
            foreach (var (planet, transform) in SystemAPI
                         .Query<RefRO<PlanetState>, RefRO<LocalTransform>>()
                         .WithAll<PlanetTag>())
            {
                int id = planet.ValueRO.PlanetId;
                if (id != 0)
                    _planetById[id] = transform.ValueRO;
            }
        }

        /// <summary>Resolves load (ship) or unload (planet) magnet target.</summary>
        bool TryResolveTarget(
            in PeopleTransportPresentation p,
            bool isLoad,
            float3 myPos,
            float mapW,
            float mapH,
            out float3 target)
        {
            target = float3.zero;
            if (isLoad)
            {
                if (p.TargetShipNetworkId != 0 &&
                    _shipByNetworkId.TryGetValue(p.TargetShipNetworkId, out var shipXform))
                {
                    target = PeopleTransportMath.GetShipMagnetTarget(
                        shipXform.Position,
                        PeopleTransportMath.GetShipHullRadius(shipXform.Scale),
                        myPos, mapW, mapH);
                    return true;
                }

                if (p.SourcePlanetId != 0 &&
                    _planetById.TryGetValue(p.SourcePlanetId, out var sourcePlanet))
                {
                    float planetSize = math.max(0.5f, sourcePlanet.Scale);
                    target = PeopleTransportMath.GetPlanetSurfaceToward(
                        sourcePlanet.Position, planetSize, myPos, mapW, mapH);
                    return true;
                }

                return false;
            }

            int planetId = p.TargetPlanetId != 0 ? p.TargetPlanetId : p.SourcePlanetId;
            if (planetId != 0 && _planetById.TryGetValue(planetId, out var unloadPlanet))
            {
                float planetSize = math.max(0.5f, unloadPlanet.Scale);
                target = PeopleTransportMath.GetPlanetSurfaceToward(
                    unloadPlanet.Position, planetSize, myPos, mapW, mapH);
                return true;
            }

            return false;
        }

        /// <summary>Writes presentation cache for hybrid GameObject proxies.</summary>
        static void Publish(Entity entity, float3 pos, float scale)
        {
            pos.y = PresentationLiftY;
            GhostPresentationTransformCache.PublishPeopleTransport(
                entity,
                new GhostPresentationTransformCache.Snapshot
                {
                    Position = pos,
                    Rotation = quaternion.identity,
                    Scale = scale > 0.001f ? scale : 0.35f,
                });
        }

        /// <summary>Map size from singleton or ToroidalMapEcs cache.</summary>
        void GetMapSize(out float mapW, out float mapH)
        {
            mapW = ToroidalMapEcs.MapWidth;
            mapH = ToroidalMapEcs.MapHeight;
            if (SystemAPI.TryGetSingleton<MapStateSingleton>(out var map))
            {
                mapW = math.max(100f, map.MapWidth);
                mapH = math.max(100f, map.MapHeight);
            }
        }
    }
}
