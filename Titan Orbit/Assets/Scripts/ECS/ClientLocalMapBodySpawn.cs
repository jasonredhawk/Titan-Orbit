using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [TITAN-ORBIT] Tag on client-only map bodies created from the match seed.
    /// These are not NetCode ghosts — layout comes from <see cref="MapLayoutBlueprint"/>,
    /// mutable state arrives via sparse RPCs (ownership, HitRpc HP, etc.).
    /// </summary>
    public struct ClientSeedHydratedMapBody : IComponentData { }

    /// <summary>
    /// [TITAN-ORBIT] Instantiates planet/asteroid prefabs on the client without registering them
    /// as ghosts. Strips GhostInstance / GhostType so GhostSpawn / GhostUpdate never own these entities.
    /// </summary>
    public static class ClientLocalMapBodySpawn
    {
        /// <summary>
        /// Removes NetCode ghost identity from <paramref name="entity"/> and every member of its
        /// <see cref="LinkedEntityGroup"/> (ghost prefabs Instantiates child entities that also carry
        /// <see cref="GhostInstance"/> — stripping only the root leaves ghostId==0 children that
        /// GhostUpdateSystem rejects every frame).
        /// <para>
        /// Must not be called while a SystemAPI foreach query is still iterating — copy RPC
        /// payloads out first, then spawn (see <c>AsteroidRespawnRpcClientSystem</c>).
        /// </para>
        /// </summary>
        public static void StripGhostNetworking(EntityManager em, Entity entity)
        {
            if (!em.Exists(entity))
                return;

            // --- Root + LinkedEntityGroup children ---
            // [NETCODE] Ghost prefabs bake a LinkedEntityGroup. Instantiates copies the whole group;
            // each child can still have GhostInstance with ghostId 0 until stripped.
            if (em.HasBuffer<LinkedEntityGroup>(entity))
            {
                var group = em.GetBuffer<LinkedEntityGroup>(entity);
                // Copy indices first — RemoveComponent on one member must not invalidate the buffer mid-loop.
                var members = new NativeArray<Entity>(group.Length, Allocator.Temp);
                for (int i = 0; i < group.Length; i++)
                    members[i] = group[i].Value;

                for (int i = 0; i < members.Length; i++)
                {
                    if (em.Exists(members[i]))
                        StripGhostNetworkingOnSingleEntity(em, members[i]);
                }

                members.Dispose();
                return;
            }

            StripGhostNetworkingOnSingleEntity(em, entity);
        }

        /// <summary>
        /// Removes all ghost identity components present on a single entity.
        /// Safe here because callers never invoke this while a SystemAPI foreach is iterating.
        /// </summary>
        static void StripGhostNetworkingOnSingleEntity(EntityManager em, Entity entity)
        {
            // --- Drop NetCode ghost identity ---
            // [ECS/DOTS] Sequential RemoveComponent is fine outside a query. ComponentTypeSet's
            // NativeArray ctor is not available on this Entities version (CS1503).
            void RemoveIfPresent<T>() where T : unmanaged, IComponentData
            {
                if (em.HasComponent<T>(entity))
                    em.RemoveComponent<T>(entity);
            }

            RemoveIfPresent<GhostInstance>();
            RemoveIfPresent<GhostType>();
            RemoveIfPresent<PredictedGhost>();
            RemoveIfPresent<GhostOwner>();
            RemoveIfPresent<GhostOwnerIsLocal>();
            RemoveIfPresent<PredictedGhostSpawnRequest>();
            RemoveIfPresent<Prefab>();

            // Shared component — same RemoveComponent path.
            if (em.HasComponent<GhostTypePartition>(entity))
                em.RemoveComponent<GhostTypePartition>(entity);
        }

        /// <summary>
        /// Queues hybrid GameObject creation without scanning asteroids (SpawnRequest is non-ghost).
        /// </summary>
        public static void QueueHybridVisual(EntityManager em, Entity entity)
        {
            if (!em.Exists(entity))
                return;
            if (em.HasComponent<MapBodyHybridVisualLinked>(entity))
                return;
            if (!em.HasComponent<MapBodyHybridVisualSpawnRequest>(entity))
                em.AddComponent<MapBodyHybridVisualSpawnRequest>(entity);
        }

        /// <summary>Spawns one local planet from a blueprint body.</summary>
        public static Entity SpawnPlanet(EntityManager em, Entity planetPrefab, in MapLayoutBlueprint.Body body)
        {
            if (planetPrefab == Entity.Null)
                return Entity.Null;

            Entity e = em.Instantiate(planetPrefab);
            StripGhostNetworking(em, e);

            float scale = math.max(0.25f, body.Scale);
            em.SetComponentData(e, LocalTransform.FromPositionRotationScale(
                body.Position, quaternion.identity, scale));

            bool isHome = body.EntityKind == 1;
            int level = math.max(1, body.Level);
            int maxPopulation = PlanetPopulationMath.GetMaxPopulation(scale, level);
            var planetState = new PlanetState
            {
                Ownership = body.Team,
                Population = maxPopulation,
                PlanetLevel = level,
                PlanetId = body.PlanetId,
                IsHomePlanet = isHome,
                ShipFamilyConfigIndex = isHome
                    ? PlanetShipFamilyAssignment.HomeFamilyConfigIndex
                    : body.ShipFamilyConfigIndex,
            };
            SetOrAdd(em, e, planetState);

            if (!em.HasComponent<PlanetTag>(e))
                em.AddComponent<PlanetTag>(e);
            if (isHome && !em.HasComponent<HomePlanetTag>(e))
                em.AddComponent<HomePlanetTag>(e);
            if (isHome && !em.HasBuffer<ContributedGemsElement>(e))
                em.AddBuffer<ContributedGemsElement>(e);

            SetOrAdd(em, e, new PlanetGrowthState { FractionalPopulation = maxPopulation });

            float maxShield = PlanetGemMoonMath.GetMaxShieldForLevel(level);
            var moonState = new PlanetGemMoonState
            {
                CurrentShield = maxShield,
                MaxShield = maxShield,
            };
            PlanetGemMoonCombatLogic.InitMoonGems(ref moonState);
            SetOrAdd(em, e, moonState);

            TagSeedHydratedGroup(em, e);
            QueueHybridVisual(em, e);
            return e;
        }

        /// <summary>Spawns one local asteroid from a blueprint body.</summary>
        public static Entity SpawnAsteroid(EntityManager em, Entity asteroidPrefab, in MapLayoutBlueprint.Body body)
        {
            if (asteroidPrefab == Entity.Null)
                return Entity.Null;

            Entity e = AsteroidSpawning.Spawn(
                em,
                asteroidPrefab,
                body.Position,
                body.Scale,
                body.GemValue,
                body.MaxHealth,
                body.Size);

            if (e == Entity.Null)
                return Entity.Null;

            StripGhostNetworking(em, e);
            TagSeedHydratedGroup(em, e);
            // [TITAN-ORBIT] Combat sync / HitRpc matching uses this registry before the hybrid GO
            // drain finishes — register as soon as the local ECS body exists.
            AsteroidClientEntityRegistry.NotifyInstantiated(e);
            QueueHybridVisual(em, e);
            return e;
        }

        /// <summary>
        /// Marks the Instantiates root and every <see cref="LinkedEntityGroup"/> member so the
        /// ghost-strip safety net can find leftover GhostInstance on child entities too.
        /// </summary>
        public static void TagSeedHydratedGroup(EntityManager em, Entity root)
        {
            if (!em.Exists(root))
                return;

            if (em.HasBuffer<LinkedEntityGroup>(root))
            {
                // Copy first — AddComponent is structural and invalidates the DynamicBuffer handle.
                var group = em.GetBuffer<LinkedEntityGroup>(root);
                var members = new NativeArray<Entity>(group.Length, Allocator.Temp);
                for (int i = 0; i < group.Length; i++)
                    members[i] = group[i].Value;

                for (int i = 0; i < members.Length; i++)
                {
                    Entity member = members[i];
                    if (!em.Exists(member))
                        continue;
                    if (!em.HasComponent<ClientSeedHydratedMapBody>(member))
                        em.AddComponent<ClientSeedHydratedMapBody>(member);
                }

                members.Dispose();
                return;
            }

            if (!em.HasComponent<ClientSeedHydratedMapBody>(root))
                em.AddComponent<ClientSeedHydratedMapBody>(root);
        }

        /// <summary>Applies a starting claim onto a hydrated neutral planet by PlanetId.</summary>
        public static bool TryApplyClaim(EntityManager em, int planetId, TeamId team)
        {
            using var query = em.CreateEntityQuery(
                ComponentType.ReadWrite<PlanetState>(),
                ComponentType.ReadOnly<PlanetTag>(),
                ComponentType.ReadOnly<ClientSeedHydratedMapBody>());
            using var entities = query.ToEntityArray(Allocator.Temp);
            using var states = query.ToComponentDataArray<PlanetState>(Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
            {
                if (states[i].PlanetId != planetId)
                    continue;

                var ps = states[i];
                ps.Ownership = team;
                em.SetComponentData(entities[i], ps);
                return true;
            }

            return false;
        }

        static void SetOrAdd<T>(EntityManager em, Entity e, T value) where T : unmanaged, IComponentData
        {
            if (em.HasComponent<T>(e))
                em.SetComponentData(e, value);
            else
                em.AddComponentData(e, value);
        }
    }
}
