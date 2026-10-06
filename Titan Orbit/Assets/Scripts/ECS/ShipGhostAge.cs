using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Picks the newest player hull when a reconnect left two ships with the same
    /// <see cref="GhostOwner.NetworkId"/>. Ghost id is recycled, so age is
    /// <see cref="GhostInstance.spawnTick"/>. A hull Instantiated this tick often has no
    /// assigned tick yet — that one is the new ship.
    /// </summary>
    public static class ShipGhostAge
    {
        /// <summary>
        /// True when <paramref name="candidate"/> was spawned after <paramref name="incumbent"/>.
        /// </summary>
        public static bool IsNewerThan(EntityManager em, Entity candidate, Entity incumbent)
        {
            if (incumbent == Entity.Null || !em.Exists(incumbent))
                return candidate != Entity.Null && em.Exists(candidate);
            if (candidate == Entity.Null || !em.Exists(candidate))
                return false;

            bool candidateHasTick = TryGetSpawnTick(em, candidate, out var candidateTick);
            bool incumbentHasTick = TryGetSpawnTick(em, incumbent, out var incumbentTick);
            if (candidateHasTick && incumbentHasTick)
                return candidateTick.IsNewerThan(incumbentTick);

            // Fresh Instantiates copies the prefab ghost (tick not assigned yet). That hull is newer.
            if (candidateHasTick != incumbentHasTick)
                return !candidateHasTick;

            if (candidate.Index != incumbent.Index)
                return candidate.Index > incumbent.Index;
            return candidate.Version > incumbent.Version;
        }

        /// <summary>
        /// Index of the newest ship whose <see cref="GhostOwner.NetworkId"/> equals
        /// <paramref name="networkId"/>, or -1 when none match.
        /// </summary>
        public static int IndexOfNewest(
            EntityManager em,
            NativeArray<Entity> entities,
            NativeArray<GhostOwner> owners,
            int networkId)
        {
            int best = -1;
            for (int i = 0; i < owners.Length; i++)
            {
                if (owners[i].NetworkId != networkId)
                    continue;
                if (best < 0 || IsNewerThan(em, entities[i], entities[best]))
                    best = i;
            }

            return best;
        }

        static bool TryGetSpawnTick(EntityManager em, Entity ship, out NetworkTick spawnTick)
        {
            spawnTick = default;
            if (!em.HasComponent<GhostInstance>(ship))
                return false;
            spawnTick = em.GetComponentData<GhostInstance>(ship).spawnTick;
            return spawnTick.IsValid;
        }
    }
}
