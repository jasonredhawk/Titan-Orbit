using System.Collections.Generic;
using TitanOrbit.ECS;
using TitanOrbit.Generation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Client toroidal display for ECS presentation proxies.
    /// Static bodies use per-entity tile hysteresis: they stay on a fixed map copy until the
    /// ship crosses a toroidal wrap boundary, not every frame.
    /// </summary>
    public static class ToroidalDisplay
    {
        static readonly Dictionary<Entity, (int k, int m)> s_EntityTiles = new();
        static readonly Dictionary<int, (int k, int m)> s_KeyedTiles = new();
        static int s_TileSwitchesThisFrame;

        public static int TileSwitchesThisFrame => s_TileSwitchesThisFrame;

        internal static void BeginFrame()
        {
            s_TileSwitchesThisFrame = 0;
        }

        public static void SyncMapSize(EntityManager em)
        {
            float mapW = ToroidalMapEcs.MapWidth;
            float mapH = ToroidalMapEcs.MapHeight;
            using var mapQuery = em.CreateEntityQuery(typeof(MapStateSingleton));
            if (mapQuery.TryGetSingleton<MapStateSingleton>(out var map))
            {
                mapW = map.MapWidth;
                mapH = map.MapHeight;
            }

            ToroidalMapEcs.SetMapSize(mapW, mapH);
        }

        /// <summary>
        /// Nearest toroidal copy with per-entity tile memory. Position is stable while the ship
        /// remains in the same map tile; only changes on wrap.
        /// </summary>
        public static Vector3 ToDisplayPosition(Entity entity, Vector3 logicalPosition, Vector3 referencePosition)
        {
            if (!s_EntityTiles.TryGetValue(entity, out var tile))
                tile = (int.MinValue, int.MinValue);

            int tileK = tile.k;
            int tileM = tile.m;
            float3 display = ToroidalMapEcs.GetDisplayPositionWithHysteresis(
                logicalPosition,
                referencePosition,
                ref tileK,
                ref tileM);

            if (tile.k != int.MinValue && (tileK != tile.k || tileM != tile.m))
                s_TileSwitchesThisFrame++;

            s_EntityTiles[entity] = (tileK, tileM);
            return display;
        }

        public static Vector3 ToDisplayPositionWithHysteresis(
            int stableKey,
            Vector3 logicalPosition,
            Vector3 referencePosition)
        {
            if (!s_KeyedTiles.TryGetValue(stableKey, out var tile))
                tile = (int.MinValue, int.MinValue);

            int tileK = tile.k;
            int tileM = tile.m;
            float3 display = ToroidalMapEcs.GetDisplayPositionWithHysteresis(
                logicalPosition,
                referencePosition,
                ref tileK,
                ref tileM);

            if (tile.k != int.MinValue && (tileK != tile.k || tileM != tile.m))
                s_TileSwitchesThisFrame++;

            s_KeyedTiles[stableKey] = (tileK, tileM);
            return display;
        }

        public static bool TryGetReferencePosition(out Vector3 reference)
        {
            if (EcsGameBridge.TryGetLocalShipPosition(out var shipPos))
            {
                reference = new Vector3(shipPos.x, 0f, shipPos.z);
                return true;
            }

            if (ShipDisplayPose.HasLocalPose)
            {
                var p = ShipDisplayPose.LocalPosition;
                reference = new Vector3(p.x, 0f, p.z);
                return true;
            }

            var cam = UnityEngine.Camera.main;
            if (cam != null && cam.isActiveAndEnabled)
            {
                var camPos = cam.transform.position;
                reference = new Vector3(camPos.x, 0f, camPos.z);
                return true;
            }

            reference = default;
            return false;
        }

        [System.Obsolete("Repositions every frame as the ship moves. Use ToDisplayPosition with hysteresis instead.")]
        public static Vector3 ToDisplayPositionContinuous(Vector3 logicalPosition, Vector3 referencePosition)
        {
            float3 offset = ToroidalMapEcs.ShortestOffsetXZ(referencePosition, logicalPosition, ToroidalMapEcs.MapWidth, ToroidalMapEcs.MapHeight);
            return new Vector3(
                referencePosition.x + offset.x,
                logicalPosition.y,
                referencePosition.z + offset.z);
        }

        public static void RemoveEntity(Entity entity) => s_EntityTiles.Remove(entity);

        public static void PruneStale(HashSet<Entity> alive)
        {
            if (s_EntityTiles.Count == 0)
                return;

            var remove = new List<Entity>();
            foreach (var kv in s_EntityTiles)
            {
                if (!alive.Contains(kv.Key))
                    remove.Add(kv.Key);
            }

            for (int i = 0; i < remove.Count; i++)
                s_EntityTiles.Remove(remove[i]);
        }

        public static bool IsLocalPlayerShip(EntityManager em, Entity entity)
        {
            if (em.HasComponent<LocalPlayerShipTag>(entity))
                return true;

            return em.HasComponent<GhostOwnerIsLocal>(entity) &&
                   em.IsComponentEnabled<GhostOwnerIsLocal>(entity);
        }
    }
}
