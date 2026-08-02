using TitanOrbit.Core;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server: keeps each owned planet's <see cref="PlanetaryDefenseSlotElement"/> buffer sized to
    /// planet level, and wipes all slots when ownership flips (capture / lose claim) or the planet
    /// becomes neutral.
    /// <para>
    /// [TITAN-ORBIT] When the planet levels up, existing turrets keep their level/HP/progress and
    /// only move to new even-ring angles (angle depends on slot count, not stored in the buffer).
    /// Capture always destroys every turret — slots become empty placeholders for the new owner.
    /// </para>
    /// <para>
    /// [ECS/DOTS] Uses <c>ToEntityArray</c> then mutates — never
    /// <c>AddComponent</c> inside a live <c>SystemAPI.Query</c> foreach (structural-change exception).
    /// </para>
    /// World: ServerSimulation.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct PlanetaryDefenseSlotSyncSystem : ISystem
    {
        EntityQuery _planetQuery;

        /// <summary>Cache planet query; require planets before ticking.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<PlanetTag>();
            _planetQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<PlanetTag>(),
                ComponentType.ReadOnly<PlanetState>());
        }

        /// <summary>
        /// Sync buffer length / wipe on ownership or level changes for every planet.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            var em = state.EntityManager;

            // --- Snapshot entities first ---
            // [ECS/DOTS] Structural adds (cache / missing buffer) are illegal during Query foreach.
            using var entities = _planetQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
            {
                Entity entity = entities[i];
                if (!em.Exists(entity) || !em.HasComponent<PlanetState>(entity))
                    continue;

                // --- Ensure ghosted buffer exists (baked on prefab; defensive for old ghosts) ---
                if (!em.HasBuffer<PlanetaryDefenseSlotElement>(entity))
                    em.AddBuffer<PlanetaryDefenseSlotElement>(entity);

                // Server-only sync cache — not ghosted.
                if (!em.HasComponent<PlanetaryDefenseServerCache>(entity))
                    em.AddComponentData(entity, new PlanetaryDefenseServerCache());

                var cache = em.GetComponentData<PlanetaryDefenseServerCache>(entity);
                var planet = em.GetComponentData<PlanetState>(entity);
                var buffer = em.GetBuffer<PlanetaryDefenseSlotElement>(entity);

                TeamId ownership = planet.Ownership;
                int level = planet.PlanetLevel;

                // --- First tick: size for current ownership without inventing a false "capture" ---
                if (!cache.Initialized)
                {
                    // Homes spawn already owned — create empty pads. Neutrals stay length 0.
                    ApplyOwnershipAndLevel(buffer, ownership, level, wipe: true);
                    cache.LastOwnership = ownership;
                    cache.LastPlanetLevel = level;
                    cache.Initialized = true;
                    em.SetComponentData(entity, cache);
                    continue;
                }

                bool ownershipChanged = cache.LastOwnership != ownership;
                bool becameNeutral = ownership == TeamId.None;
                // Capture / team flip: wipe all turrets (plan rule).
                bool mustWipe = ownershipChanged &&
                                (becameNeutral ||
                                 cache.LastOwnership != TeamId.None ||
                                 ownership != TeamId.None);

                // Neutral → owned: fresh empty slots for the new owner.
                // Owned A → owned B: wipe then resize.
                // Owned → neutral: clear to length 0.
                // Level-up only: grow/shrink without wiping existing turrets.
                if (ownershipChanged || cache.LastPlanetLevel != level)
                {
                    ApplyOwnershipAndLevel(buffer, ownership, level, wipe: mustWipe || becameNeutral);
                    cache.LastOwnership = ownership;
                    cache.LastPlanetLevel = level;
                    em.SetComponentData(entity, cache);
                }
            }
        }

        /// <summary>
        /// Resizes (and optionally wipes) the slot buffer for the planet's current ownership/level.
        /// </summary>
        static void ApplyOwnershipAndLevel(
            DynamicBuffer<PlanetaryDefenseSlotElement> buffer,
            TeamId ownership,
            int planetLevel,
            bool wipe)
        {
            if (ownership == TeamId.None)
            {
                buffer.Clear();
                return;
            }

            int count = PlanetaryDefenseMath.GetSlotCountForOwnedPlanet(planetLevel);
            PlanetaryDefenseLogic.EnsureSlotCount(buffer, count, wipeExisting: wipe);
        }

        /// <summary>
        /// Immediate wipe + resize for a planet entity after an ownership write in the same frame
        /// (capture / starting claim / home spawn). SlotSync also detects the flip next tick — this
        /// avoids one frame of enemy turrets firing for the old owner.
        /// </summary>
        public static void WipeSlotsForOwnershipChange(
            EntityManager em,
            Entity planetEntity,
            TeamId newOwner,
            int planetLevel)
        {
            if (planetEntity == Entity.Null || !em.Exists(planetEntity))
                return;

            if (!em.HasBuffer<PlanetaryDefenseSlotElement>(planetEntity))
                em.AddBuffer<PlanetaryDefenseSlotElement>(planetEntity);

            var buffer = em.GetBuffer<PlanetaryDefenseSlotElement>(planetEntity);
            if (newOwner == TeamId.None)
            {
                buffer.Clear();
            }
            else
            {
                int count = PlanetaryDefenseMath.GetSlotCountForOwnedPlanet(planetLevel);
                PlanetaryDefenseLogic.EnsureSlotCount(buffer, count, wipeExisting: true);
            }

            if (!em.HasComponent<PlanetaryDefenseServerCache>(planetEntity))
                em.AddComponentData(planetEntity, new PlanetaryDefenseServerCache());

            em.SetComponentData(planetEntity, new PlanetaryDefenseServerCache
            {
                LastOwnership = newOwner,
                LastPlanetLevel = planetLevel,
                Initialized = true,
            });
        }
    }
}
