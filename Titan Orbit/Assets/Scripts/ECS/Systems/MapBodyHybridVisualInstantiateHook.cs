using System.Collections.Generic;
using TitanOrbit.ECS;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Registers a per-Instantiates hook on <see cref="TitanOrbitJoinLoadCounters"/> so Windows
    /// clients can queue hybrid map visuals when EntityScenes lack baked
    /// <see cref="MapBodyHybridVisualPending"/>.
    /// <para>
    /// [TITAN-ORBIT] Player.log 2026-07-19: loading stuck at 0/N then starve-escape → Join Team →
    /// Crash!!!. Root cause: no Pending on Instantiated ghosts, so Pending drain created zero GOs.
    /// Scanning all asteroids to AddComponent SpawnRequest also Crash!!! — instead GhostSpawn calls
    /// this hook once per successful Instantiates (1/frame) with the exact entity.
    /// </para>
    /// <para>
    /// Structural AddComponent is deferred to <see cref="FlushPending"/> (called from the visualizer)
    /// so we do not mutate archetypes mid-<see cref="GhostSpawnSystem"/>.
    /// </para>
    /// </summary>
    public static class MapBodyHybridVisualInstantiateHook
    {
        /// <summary>Entities that need SpawnRequest after GhostSpawn finishes this frame.</summary>
        static readonly List<Entity> s_PendingQueue = new List<Entity>(8);

        /// <summary>[UNITY] Register once after assemblies load (client + editor).</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Register()
        {
            s_PendingQueue.Clear();
            GemClientEntityRegistry.Clear();
            LocalShipEntitySeed.Clear();
            // --- Replace any prior handler (domain reload / play mode) ---
            TitanOrbitJoinLoadCounters.OnDelayedGhostInstantiate = OnDelayedGhostInstantiate;
            // Prefer gem Instantiates among ready delayed ghosts (still 1/frame).
            TitanOrbitJoinLoadCounters.IsPriorityDelayedInstantiate = IsGemPlaceholder;
        }

        /// <summary>True when the delayed-spawn placeholder is a gem ghost (priority Instantiates).</summary>
        static bool IsGemPlaceholder(EntityManager em, Entity placeholder) =>
            placeholder != Entity.Null && em.Exists(placeholder) && em.HasComponent<GemTag>(placeholder);

        /// <summary>
        /// Called from patched GhostSpawn after each delayed Instantiates success.
        /// Only records map-body entities — AddComponent happens in <see cref="FlushPending"/>.
        /// Gems are also registered for tractor VFX + urgent proxy create (appear ASAP).
        /// </summary>
        /// <param name="em">Client EntityManager from GhostSpawn.</param>
        /// <param name="entity">The ghost entity that just Instantiated.</param>
        static void OnDelayedGhostInstantiate(EntityManager em, Entity entity)
        {
            if (entity == Entity.Null || !em.Exists(entity))
                return;

            // --- Local ship: seed presentation cache during GhostSpawnBacklog ---
            // [TITAN-ORBIT] debug-604d3d: post–Join Team ship Instantiates left hasPose=false
            // (lookup gated, cache empty) then !!CAM_JUMP 113m + !!RETILE when backlog cleared.
            // One-entity Instantiates hook — no ship ToEntityArray / WithEntityAccess.
            if (em.HasComponent<ShipTag>(entity))
            {
                LocalShipEntitySeed.NotifyShipInstantiated(em, entity);
                return;
            }

            // --- Gems: track for tractor beams + force an urgent visual (do not wait on asteroid drain) ---
            // [TITAN-ORBIT] Gem ghosts often already have baked Pending, so the early-return below
            // would skip SpawnRequest. They still need registry + urgent GO so destroy bursts
            // are not stuck behind a long Instantiates/Pending asteroid backlog.
            if (em.HasComponent<GemTag>(entity))
            {
                GemClientEntityRegistry.NotifyInstantiated(entity);
                if (!em.HasComponent<MapBodyHybridVisualLinked>(entity) &&
                    !s_PendingQueue.Contains(entity) &&
                    !em.HasComponent<MapBodyHybridVisualSpawnRequest>(entity))
                {
                    // Ensure drain sees this gem even if Pending was already consumed incorrectly.
                    if (!em.HasComponent<MapBodyHybridVisualPending>(entity))
                        s_PendingQueue.Add(entity);
                }
                return;
            }

            // --- Already queued or already has a GameObject proxy ---
            if (em.HasComponent<MapBodyHybridVisualSpawnRequest>(entity) ||
                em.HasComponent<MapBodyHybridVisualLinked>(entity) ||
                em.HasComponent<MapBodyHybridVisualPending>(entity))
                return;

            // --- Map bodies only (ships/bullets/transports use other presentation paths) ---
            bool isMapBody =
                em.HasComponent<PlanetTag>(entity) ||
                em.HasComponent<AsteroidTag>(entity);
            if (!isMapBody)
                return;

            if (!s_PendingQueue.Contains(entity))
                s_PendingQueue.Add(entity);
        }

        /// <summary>
        /// Applies deferred <see cref="MapBodyHybridVisualSpawnRequest"/> tags.
        /// Call from <see cref="EcsWorldVisualizer"/> before Pending drain (main thread, after ECS).
        /// </summary>
        /// <param name="em">Visualization / client EntityManager.</param>
        public static void FlushPending(EntityManager em)
        {
            if (s_PendingQueue.Count == 0)
                return;

            for (int i = 0; i < s_PendingQueue.Count; i++)
            {
                Entity entity = s_PendingQueue[i];
                if (!em.Exists(entity))
                    continue;
                if (em.HasComponent<MapBodyHybridVisualSpawnRequest>(entity) ||
                    em.HasComponent<MapBodyHybridVisualLinked>(entity) ||
                    em.HasComponent<MapBodyHybridVisualPending>(entity))
                    continue;

                // [HYBRID] SpawnRequest is intentionally NOT a GhostComponent — safe runtime add.
                em.AddComponentData(entity, new MapBodyHybridVisualSpawnRequest());
            }

            s_PendingQueue.Clear();
        }
    }
}
