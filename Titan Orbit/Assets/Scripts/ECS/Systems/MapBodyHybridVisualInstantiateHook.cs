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
    /// this hook once per successful Instantiates (16/frame) with the exact entity.
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
            PlanetClientEntityRegistry.Clear();
            AsteroidClientEntityRegistry.Clear();
            LocalShipEntitySeed.Clear();
            // --- Replace any prior handler (domain reload / play mode) ---
            TitanOrbitJoinLoadCounters.OnDelayedGhostInstantiate = OnDelayedGhostInstantiate;
            // Prefer ship / gem Instantiates among ready delayed ghosts (16/frame budget).
            // [TITAN-ORBIT] Ships must jump the post-settle asteroid Instantiates queue or Join Team
            // leaves the client on "Spawning your ship..." for minutes while map bodies drain.
            TitanOrbitJoinLoadCounters.IsPriorityDelayedInstantiate = IsPriorityPlaceholder;
        }

        /// <summary>
        /// True when this delayed-spawn placeholder should Instantiates before map asteroids/planets.
        /// Ships (Join Team) and gems (destroy bursts) are prioritized; capped at 16/frame.
        /// <para>
        /// [TITAN-ORBIT] Placeholders are bare <c>GhostInstance</c> + snapshot buffers — they do
        /// <b>not</b> carry <see cref="ShipTag"/> / <see cref="GemTag"/>. Checking tags on the
        /// placeholder always returned false, so TeamChoice ships waited behind the old 1/frame map
        /// Instantiates queue ("Spawning your ship..." forever). Resolve the ghost prefab via
        /// <see cref="GhostCollectionPrefab"/> and test tags on the prefab entity instead.
        /// </para>
        /// </summary>
        static bool IsPriorityPlaceholder(EntityManager em, Entity placeholder)
        {
            if (placeholder == Entity.Null || !em.Exists(placeholder))
                return false;

            // --- Instantiated ghosts (rare path if called after Instantiates) ---
            if (em.HasComponent<ShipTag>(placeholder) || em.HasComponent<GemTag>(placeholder))
                return true;

            // --- Delayed placeholders: look up prefab from GhostInstance.ghostType ---
            // [NETCODE] GhostInstance.ghostType indexes GhostCollectionPrefab on the collection singleton.
            if (!em.HasComponent<GhostInstance>(placeholder))
                return false;

            int ghostTypeIndex = em.GetComponentData<GhostInstance>(placeholder).ghostType;
            if (ghostTypeIndex < 0)
                return false;

            using var collectionQuery = em.CreateEntityQuery(ComponentType.ReadOnly<GhostCollection>());
            if (collectionQuery.IsEmptyIgnoreFilter)
                return false;

            var collectionEntity = collectionQuery.GetSingletonEntity();
            if (!em.HasBuffer<GhostCollectionPrefab>(collectionEntity))
                return false;

            var prefabs = em.GetBuffer<GhostCollectionPrefab>(collectionEntity, isReadOnly: true);
            if (ghostTypeIndex >= prefabs.Length)
                return false;

            Entity prefab = prefabs[ghostTypeIndex].GhostPrefab;
            if (prefab == Entity.Null || !em.Exists(prefab))
                return false;

            // --- Ship / gem prefabs jump the asteroid Instantiates line ---
            return em.HasComponent<ShipTag>(prefab) || em.HasComponent<GemTag>(prefab);
        }

        /// <summary>
        /// Called from patched GhostSpawn after each delayed Instantiates success.
        /// Only records map-body entities — AddComponent happens in <see cref="FlushPending"/>.
        /// Gems are also registered for tractor VFX + urgent proxy create (appear ASAP).
        /// Planets are registered for quarantine-safe orbit / moon-shield motor Collect
        /// (<see cref="PlanetClientEntityRegistry"/>).
        /// </summary>
        /// <param name="em">Client EntityManager from GhostSpawn.</param>
        /// <param name="entity">The ghost entity that just Instantiated.</param>
        static void OnDelayedGhostInstantiate(EntityManager em, Entity entity)
        {
            if (entity == Entity.Null || !em.Exists(entity))
                return;

            // --- Local ship: seed presentation cache during GhostSpawnBacklog ---
            // [TITAN-ORBIT] Post–Join Team ship Instantiates leave bridge lookup gated; without a
            // seed, hasPose stays false then the camera snaps when backlog clears.
            // One-entity Instantiates hook — no ship ToEntityArray / WithEntityAccess.
            if (em.HasComponent<ShipTag>(entity))
            {
                LocalShipEntitySeed.NotifyShipInstantiated(em, entity);
                return;
            }

            // --- Planets: track for quarantine-safe orbit / moon-shield motor Collect ---
            // [TITAN-ORBIT] Must register even when Pending already exists (early-return below).
            // Without this, client predicted drive had zero planets under TransformQuarantine →
            // coast while server orbit motor ran → choppy ring reconcile.
            if (em.HasComponent<PlanetTag>(entity))
                PlanetClientEntityRegistry.NotifyInstantiated(entity);

            // --- Asteroids: track for loading-bar proxy rebuild (second Local Host) ---
            // [TITAN-ORBIT] Registry walk is quarantine-safe; full asteroid ToEntityArray is not.
            if (em.HasComponent<AsteroidTag>(entity))
                AsteroidClientEntityRegistry.NotifyInstantiated(entity);

            // --- Gems: track for tractor beams + force an urgent visual (do not wait on asteroid drain) ---
            // [TITAN-ORBIT] Gem ghosts often already have baked Pending, so the early-return below
            // would skip SpawnRequest. They still need registry + urgent GO so destroy bursts
            // are not stuck behind a long Instantiates/Pending asteroid backlog.
            if (em.HasComponent<GemTag>(entity))
            {
                GemClientEntityRegistry.NotifyInstantiated(entity);
                if (!s_PendingQueue.Contains(entity) &&
                    !em.HasComponent<MapBodyHybridVisualSpawnRequest>(entity) &&
                    !em.HasComponent<MapBodyHybridVisualPending>(entity))
                {
                    // Ensure drain sees this gem even if a prior Linked tag outlived the GO.
                    s_PendingQueue.Add(entity);
                }
                return;
            }

            // --- Already in the drain queue (Pending / SpawnRequest) ---
            if (em.HasComponent<MapBodyHybridVisualSpawnRequest>(entity) ||
                em.HasComponent<MapBodyHybridVisualPending>(entity))
                return;

            // --- Map bodies only (ships/bullets/transports use other presentation paths) ---
            bool isMapBody =
                em.HasComponent<PlanetTag>(entity) ||
                em.HasComponent<AsteroidTag>(entity);
            if (!isMapBody)
                return;

            // [TITAN-ORBIT] Linked alone used to early-return forever. After a second Local Host /
            // Play (Domain Reload off), Instantiates may not re-fire while hybrid GOs were cleared —
            // loading stuck at 0/N with Linked orphans. Always queue SpawnRequest so drain can rebuild.
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

                // --- Already queued for drain (Pending bake or prior SpawnRequest) ---
                if (em.HasComponent<MapBodyHybridVisualSpawnRequest>(entity) ||
                    em.HasComponent<MapBodyHybridVisualPending>(entity))
                    continue;

                // [TITAN-ORBIT] Do NOT skip MapBodyHybridVisualLinked here. Linked means "had a
                // proxy once" — after second Local Host / Play (Domain Reload off) the GO dict
                // can be empty while Linked remains on Instantiated ghosts. Skipping Linked
                // left SpawnRequest empty forever → loading stuck at 0/N (proxyReady=False).
                // Drain is idempotent: existing proxy → clear tags; missing proxy → create GO.

                // [HYBRID] SpawnRequest is intentionally NOT a GhostComponent — safe runtime add.
                em.AddComponentData(entity, new MapBodyHybridVisualSpawnRequest());
            }

            s_PendingQueue.Clear();
        }
    }
}
