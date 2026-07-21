using System.Collections.Generic;
using TitanOrbit.ECS;
using TitanOrbit.Generation;
using TitanOrbit.Shared;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Client toroidal display for ECS presentation proxies — classic “ship flies forever” model.
    /// <para>
    /// [TITAN-ORBIT] How this is supposed to feel (matches the old ToroidalRenderer / d63ea6fd era):
    /// </para>
    /// <list type="bullet">
    /// <item>The <b>local ship does not wrap or teleport</b>. It keeps flying in world space past the
    /// map edge; the camera follows that local hull.</item>
    /// <item>Planets, asteroids, remotes, etc. each pick their own nearest map-tile copy relative to
    /// the local ship — <b>individually</b>, not as one global map snap.</item>
    /// <item>Gameplay still uses <see cref="ToroidalMapEcs.ToroidalDistance"/> / ShortestOffset so
    /// combat and docking work across seams. Remotes appear via the same per-entity display offset.</item>
    /// </list>
    /// Do not wrap local <c>LocalTransform</c> at the seam — that makes every body retile at once (the blink).
    /// [HYBRID] Render only — never writes ECS sim.
    /// </summary>
    public static class ToroidalDisplay
    {
        /// <summary>Per-entity display tile (k, m). Each body switches on its own when another tile is clearly closer.</summary>
        static readonly Dictionary<Entity, (int k, int m)> s_EntityTiles = new();

        /// <summary>Keyed tiles when the caller has a stable int id (planet id) instead of an Entity.</summary>
        static readonly Dictionary<int, (int k, int m)> s_KeyedTiles = new();

        static int s_TileSwitchesThisFrame;

        public static int TileSwitchesThisFrame => s_TileSwitchesThisFrame;

        /// <summary>Resets per-frame diagnostics.</summary>
        internal static void BeginFrame()
        {
            s_TileSwitchesThisFrame = 0;
        }

        /// <summary>Clears tile memory (despawn / leave match).</summary>
        public static void ResetSession() => ResetSession("ResetSession");

        /// <summary>
        /// Clears tile memory with a diagnostic reason. Whole-map retile after this looks like an eye blink.
        /// </summary>
        /// <param name="reason">Who requested the clear — logged by <see cref="AsteroidDestroyBlinkProbe"/>.</param>
        public static void ResetSession(string reason)
        {
            int entityTiles = s_EntityTiles.Count;
            int keyedTiles = s_KeyedTiles.Count;
            s_EntityTiles.Clear();
            s_KeyedTiles.Clear();
            s_TileSwitchesThisFrame = 0;
            AsteroidDestroyBlinkProbe.NotifyTileSessionReset(
                $"{reason} clearedEntityTiles={entityTiles} clearedKeyedTiles={keyedTiles}");
        }

        /// <summary>
        /// Syncs map size so tile math matches the rolled map (singleton, else MapSessionMetaRpc cache).
        /// </summary>
        public static void SyncMapSize(EntityManager em)
        {
            float mapW = ToroidalMapEcs.MapWidth;
            float mapH = ToroidalMapEcs.MapHeight;
            using var mapQuery = em.CreateEntityQuery(typeof(MapStateSingleton));
            if (mapQuery.TryGetSingleton<MapStateSingleton>(out var map) &&
                map.MapWidth >= 100f && map.MapHeight >= 100f)
            {
                mapW = map.MapWidth;
                mapH = map.MapHeight;
            }
            else if (NetCode.MapSessionMetaCache.HasMapSize)
            {
                mapW = NetCode.MapSessionMetaCache.MapWidth;
                mapH = NetCode.MapSessionMetaCache.MapHeight;
            }

            NetCode.MapSessionMetaCache.ApplyMapSizeToToroidalHelpers(mapW, mapH);
        }

        /// <summary>
        /// Local ship world position (unbounded). Prefer <see cref="ShipDisplayPose"/> (always safe),
        /// then live ECS when ship queries are allowed, then camera.
        /// </summary>
        public static bool TryGetReferencePosition(out Vector3 reference)
        {
            // --- Presentation pose first ---
            // [TITAN-ORBIT] During GhostSpawnBacklog (asteroid destroy → gem Instantiates),
            // EcsGameBridge ship lookups return false on purpose. Preferring ShipDisplayPose stops
            // a one-frame fallthrough that retile-blinks the map.
            if (ShipDisplayPose.HasLocalPose)
            {
                var p = ShipDisplayPose.LocalPosition;
                reference = new Vector3(p.x, 0f, p.z);
                return true;
            }

            // --- Live ECS when ship ToEntityArray is safe ---
            if (!ClientJoinSettleCache.ShouldSkipShipEntityQueries &&
                EcsGameBridge.TryGetLocalShipPosition(out var shipPos))
            {
                reference = new Vector3(shipPos.x, 0f, shipPos.z);
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

        /// <summary>
        /// Nearest tile copy of <paramref name="logicalPosition"/> to <paramref name="referencePosition"/>
        /// (immediate, no hysteresis — impacts / one-shots).
        /// </summary>
        public static Vector3 ToDisplayPosition(Vector3 logicalPosition, Vector3 referencePosition)
        {
            float3 display = ToroidalMapEcs.GetDisplayPosition(logicalPosition, referencePosition);
            return display;
        }

        /// <summary>
        /// Per-entity tile with hysteresis so each planet/asteroid keeps its current copy until another
        /// tile is clearly closer. Bodies switch one-by-one as the ship flies — not a global blink.
        /// </summary>
        public static Vector3 ToDisplayPositionWithHysteresis(
            Entity entity,
            Vector3 logicalPosition,
            Vector3 referencePosition)
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

        /// <summary>Keyed hysteresis (planet id) for appliers without an Entity handle.</summary>
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

        /// <summary>Convenience: place a body near the local ship with per-entity hysteresis.</summary>
        public static Vector3 ToDisplayPositionNearLocalShip(Entity entity, Vector3 logicalPosition)
        {
            if (!TryGetReferencePosition(out var reference))
                return logicalPosition;
            return ToDisplayPositionWithHysteresis(entity, logicalPosition, reference);
        }

        /// <summary>Convenience without entity — nearest copy, no hysteresis.</summary>
        public static Vector3 ToDisplayPositionNearLocalShip(Vector3 logicalPosition)
        {
            if (!TryGetReferencePosition(out var reference))
                return logicalPosition;
            return ToDisplayPosition(logicalPosition, reference);
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
