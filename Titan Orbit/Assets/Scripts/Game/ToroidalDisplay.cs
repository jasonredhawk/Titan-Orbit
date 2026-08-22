using TitanOrbit.ECS;
using TitanOrbit.Generation;
using TitanOrbit.NetCode;
using TitanOrbit.Shared;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Client map-size latch and pose helpers. Movers wrap in sim, so presentation uses
    /// the same world position as <c>LocalTransform</c> — no per-body display tiles.
    /// [HYBRID] Render only — never writes ECS sim.
    /// </summary>
    public static class ToroidalDisplay
    {
        /// <summary>Always 0 — tile retile diagnostics retired with canonical wrap.</summary>
        public static int TileSwitchesThisFrame => 0;

        /// <summary>No-op. Kept so LateUpdate callers do not need a compile break.</summary>
        internal static void BeginFrame()
        {
        }

        /// <summary>No-op. Tile memory is gone; wrap snaps replace ResetSession blinks.</summary>
        public static void ResetSession() => ResetSession("ResetSession");

        /// <summary>No-op with a reason tag for existing call sites.</summary>
        /// <param name="reason">Who requested the clear (grep-friendly).</param>
        public static void ResetSession(string reason)
        {
            _ = reason;
        }

        /// <summary>
        /// Syncs map size so wrap / range math matches the rolled map.
        /// Prefers <see cref="MapSessionMetaCache"/> — avoids
        /// <c>CreateEntityQuery</c> every presentation frame.
        /// </summary>
        public static void SyncMapSize(EntityManager em)
        {
            if (MapSessionMetaCache.HasMapSize)
            {
                MapSessionMetaCache.ApplyMapSizeToToroidalHelpers(
                    MapSessionMetaCache.MapWidth,
                    MapSessionMetaCache.MapHeight);
                return;
            }

            if (em == default)
                return;

            using var mapQuery = em.CreateEntityQuery(typeof(MapStateSingleton));
            if (mapQuery.TryGetSingleton<MapStateSingleton>(out var map) &&
                ToroidalMapEcs.IsValidMapSize(map.MapWidth, map.MapHeight))
            {
                MapSessionMetaCache.ApplyMapSizeToToroidalHelpers(map.MapWidth, map.MapHeight);
            }
        }

        /// <summary>
        /// Ensures toroidal helpers match the rolled map, then returns width/height.
        /// False when size is not latched yet — never invents a 1000 period.
        /// </summary>
        public static bool ResolveMapSize(EntityManager em, out float mapW, out float mapH)
        {
            SyncMapSize(em);
            return ToroidalMapEcs.TryGetMapSize(out mapW, out mapH);
        }

        /// <summary>
        /// Local ship world position (canonical wrap). Prefer <see cref="ShipDisplayPose"/>,
        /// then live ECS when ship queries are allowed, then camera.
        /// </summary>
        public static bool TryGetReferencePosition(out Vector3 reference)
        {
            if (ShipDisplayPose.HasLocalPose)
            {
                var p = ShipDisplayPose.LocalPosition;
                reference = new Vector3(p.x, 0f, p.z);
                return true;
            }

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

        /// <summary>Identity — sim already wrapped. <paramref name="referencePosition"/> unused.</summary>
        public static Vector3 ToDisplayPosition(Vector3 logicalPosition, Vector3 referencePosition)
        {
            _ = referencePosition;
            return logicalPosition;
        }

        /// <summary>Identity — leftover hysteresis callers keep compiling.</summary>
        public static Vector3 ToDisplayPositionWithHysteresis(
            Entity entity,
            Vector3 logicalPosition,
            Vector3 referencePosition)
        {
            _ = entity;
            _ = referencePosition;
            return logicalPosition;
        }

        /// <summary>Identity — leftover hysteresis callers keep compiling.</summary>
        public static Vector3 ToDisplayPositionWithHysteresis(
            Entity entity,
            Vector3 logicalPosition,
            Vector3 referencePosition,
            float switchMarginFraction)
        {
            _ = entity;
            _ = referencePosition;
            _ = switchMarginFraction;
            return logicalPosition;
        }

        /// <summary>Identity — leftover keyed callers keep compiling.</summary>
        public static Vector3 ToDisplayPositionWithHysteresis(
            int stableKey,
            Vector3 logicalPosition,
            Vector3 referencePosition)
        {
            _ = stableKey;
            _ = referencePosition;
            return logicalPosition;
        }

        /// <summary>Identity — leftover keyed callers keep compiling.</summary>
        public static Vector3 ToDisplayPositionWithHysteresis(
            int stableKey,
            Vector3 logicalPosition,
            Vector3 referencePosition,
            float switchMarginFraction)
        {
            _ = stableKey;
            _ = referencePosition;
            _ = switchMarginFraction;
            return logicalPosition;
        }

        /// <summary>Identity — orbit planet no longer uses a tighter tile margin.</summary>
        public static Vector3 ToDisplayPositionForOrbitPlanet(
            Entity entity,
            int planetId,
            Vector3 logicalPosition,
            Vector3 referencePosition)
        {
            _ = entity;
            _ = planetId;
            _ = referencePosition;
            return logicalPosition;
        }

        /// <summary>Convenience: logical pose (map already wrapped).</summary>
        public static Vector3 ToDisplayPositionNearLocalShip(Entity entity, Vector3 logicalPosition)
        {
            _ = entity;
            return logicalPosition;
        }

        /// <summary>Convenience: logical pose (map already wrapped).</summary>
        public static Vector3 ToDisplayPositionNearLocalShip(Vector3 logicalPosition) => logicalPosition;

        /// <summary>No-op. Tile dictionary retired.</summary>
        public static void RemoveEntity(Entity entity)
        {
            _ = entity;
        }

        /// <summary>No-op. Tile dictionary retired.</summary>
        public static void PruneStale(System.Collections.Generic.HashSet<Entity> alive)
        {
            _ = alive;
        }

        /// <summary>True when this ghost is the local player's predicted ship.</summary>
        public static bool IsLocalPlayerShip(EntityManager em, Entity entity)
        {
            if (em.HasComponent<LocalPlayerShipTag>(entity))
                return true;

            return em.HasComponent<GhostOwnerIsLocal>(entity) &&
                   em.IsComponentEnabled<GhostOwnerIsLocal>(entity);
        }
    }
}
