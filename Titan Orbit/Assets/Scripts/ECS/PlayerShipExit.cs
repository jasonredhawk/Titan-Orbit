using TitanOrbit.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Destroys a player's hull the moment they leave, and frees a titan (MEGA) bay the same
    /// way death does. The match snapshot in <see cref="MatchPlayerShipStore"/> keeps their
    /// ship, gear, and cargo until a new game. NetCode does not despawn owned ghosts on
    /// disconnect, and the local host parks ServerWorld before the next sim tick, so the
    /// exit path has to destroy the hull itself.
    /// </summary>
    public static class PlayerShipExit
    {
        /// <summary>
        /// Destroys every ship owned by <paramref name="networkId"/>. On the server, frees that
        /// player's titan bay and one team slot.
        /// </summary>
        /// <param name="em">Server world when <paramref name="releaseRoster"/> is true.</param>
        /// <param name="networkId">GhostOwner id of the player who is leaving.</param>
        /// <param name="releaseRoster">True on the authoritative server. False for client ghost copies.</param>
        /// <returns>How many hulls were destroyed.</returns>
        public static int DespawnOwner(EntityManager em, int networkId, bool releaseRoster)
        {
            if (networkId <= 0)
                return 0;

            using var query = em.CreateEntityQuery(
                ComponentType.ReadOnly<ShipTag>(),
                ComponentType.ReadOnly<GhostOwner>());
            using var entities = query.ToEntityArray(Allocator.Temp);
            using var owners = query.ToComponentDataArray<GhostOwner>(Allocator.Temp);
            var kill = new NativeList<Entity>(4, Allocator.Temp);
            for (int i = 0; i < owners.Length; i++)
            {
                if (owners[i].NetworkId == networkId)
                    kill.Add(entities[i]);
            }

            if (releaseRoster)
            {
                int best = ShipGhostAge.IndexOfNewest(em, entities, owners, networkId);
                if (best >= 0)
                    MatchPlayerShipStore.TryCapture(em, entities[best]);
            }

            int destroyed = DestroyHulls(em, kill, releaseRoster, networkId);
            if (releaseRoster)
                MatchPlayerShipStore.Unbind(networkId);
            kill.Dispose();
            return destroyed;
        }

        /// <summary>
        /// Destroys every player hull. Used when the local host leaves and parks the match,
        /// so the next Play does not inherit those ships.
        /// </summary>
        /// <param name="em">Authoritative server EntityManager.</param>
        /// <returns>How many hulls were destroyed.</returns>
        public static int DespawnAllPlayerShips(EntityManager em)
        {
            using var query = em.CreateEntityQuery(
                ComponentType.ReadOnly<ShipTag>(),
                ComponentType.ReadOnly<GhostOwner>());
            using var entities = query.ToEntityArray(Allocator.Temp);
            using var owners = query.ToComponentDataArray<GhostOwner>(Allocator.Temp);

            var seen = new NativeHashSet<int>(8, Allocator.Temp);
            int destroyed = 0;
            for (int i = 0; i < owners.Length; i++)
            {
                int id = owners[i].NetworkId;
                if (id <= 0 || seen.Contains(id))
                    continue;
                seen.Add(id);
                destroyed += DespawnOwner(em, id, releaseRoster: true);
            }

            // Hulls that never received an owner still block the map if we leave them behind.
            for (int i = 0; i < entities.Length; i++)
            {
                if (!em.Exists(entities[i]))
                    continue;
                if (owners[i].NetworkId > 0)
                    continue;
                em.DestroyEntity(entities[i]);
                destroyed++;
            }

            seen.Dispose();
            if (destroyed > 0)
                ZeroTeamCounts(em);
            return destroyed;
        }

        /// <summary>
        /// Deletes ship ghosts on the client world so the minimap cannot keep a blip for a hull
        /// that the server already removed.
        /// </summary>
        public static int DestroyClientShipGhosts(EntityManager em)
        {
            using var query = em.CreateEntityQuery(ComponentType.ReadOnly<ShipTag>());
            using var entities = query.ToEntityArray(Allocator.Temp);
            int destroyed = 0;
            for (int i = 0; i < entities.Length; i++)
            {
                if (!em.Exists(entities[i]))
                    continue;
                em.DestroyEntity(entities[i]);
                destroyed++;
            }

            return destroyed;
        }

        static int DestroyHulls(EntityManager em, NativeList<Entity> kill, bool releaseRoster, int networkId)
        {
            bool releasedSlot = false;
            int destroyed = 0;
            for (int i = 0; i < kill.Length; i++)
            {
                Entity ship = kill[i];
                if (!em.Exists(ship))
                    continue;

                // Same bay free as MEGA death — the titan goes back to the planet store.
                if (releaseRoster && em.HasComponent<MegaShipState>(ship))
                    MegaShipStatApplyLogic.ReleaseMegaOccupancy(em, ship);

                if (releaseRoster && !releasedSlot)
                {
                    MegaShipPlanetLogic.FreeSlotsOccupiedBy(em, networkId);
                    DecrementTeamOnce(em, ship);
                    releasedSlot = true;
                }

                em.DestroyEntity(ship);
                destroyed++;
            }

            if (destroyed > 0)
            {
                UnityEngine.Debug.Log(
                    $"[PlayerShipExit] Destroyed {destroyed} ship(s) for networkId={networkId} " +
                    $"(releaseRoster={releaseRoster}).");
            }

            return destroyed;
        }

        static void DecrementTeamOnce(EntityManager em, Entity ship)
        {
            if (!em.HasComponent<ShipState>(ship))
                return;
            using var teamQuery = em.CreateEntityQuery(ComponentType.ReadWrite<TeamStateSingleton>());
            if (teamQuery.CalculateEntityCount() != 1)
                return;
            var teamEntity = teamQuery.GetSingletonEntity();
            var team = em.GetComponentData<TeamStateSingleton>(teamEntity);
            switch (em.GetComponentData<ShipState>(ship).Team)
            {
                case TeamId.TeamA: team.TeamACount = Unity.Mathematics.math.max(0, team.TeamACount - 1); break;
                case TeamId.TeamB: team.TeamBCount = Unity.Mathematics.math.max(0, team.TeamBCount - 1); break;
                case TeamId.TeamC: team.TeamCCount = Unity.Mathematics.math.max(0, team.TeamCCount - 1); break;
                case TeamId.TeamD: team.TeamDCount = Unity.Mathematics.math.max(0, team.TeamDCount - 1); break;
                case TeamId.TeamE: team.TeamECount = Unity.Mathematics.math.max(0, team.TeamECount - 1); break;
            }

            em.SetComponentData(teamEntity, team);
        }

        /// <summary>Host parked the whole match — roster slots go to zero with the hulls.</summary>
        static void ZeroTeamCounts(EntityManager em)
        {
            using var teamQuery = em.CreateEntityQuery(ComponentType.ReadWrite<TeamStateSingleton>());
            if (teamQuery.CalculateEntityCount() != 1)
                return;
            var teamEntity = teamQuery.GetSingletonEntity();
            var team = em.GetComponentData<TeamStateSingleton>(teamEntity);
            team.TeamACount = 0;
            team.TeamBCount = 0;
            team.TeamCCount = 0;
            team.TeamDCount = 0;
            team.TeamECount = 0;
            em.SetComponentData(teamEntity, team);
        }
    }
}
