using Unity.Entities;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server-side contributed gem ledger on home planets (orbit-store currency).
    /// Buffer is per-planet, keyed by player network id. Not ghost-replicated per entry.
    /// </summary>
    public static class ContributedGemsLogic
    {
        /// <summary>Reads a player's contributed balance at a home planet (0 if none).</summary>
        public static float Get(EntityManager em, Entity homePlanetEntity, int networkId)
        {
            if (networkId <= 0 || !em.HasBuffer<ContributedGemsElement>(homePlanetEntity))
                return 0f;

            var buffer = em.GetBuffer<ContributedGemsElement>(homePlanetEntity);
            for (int i = 0; i < buffer.Length; i++)
            {
                if (buffer[i].NetworkId == networkId)
                    return buffer[i].Amount;
            }

            return 0f;
        }

        /// <summary>Increments contributed gems (deposit) for a player at a home planet.</summary>
        public static void Add(EntityManager em, Entity homePlanetEntity, int networkId, float amount)
        {
            if (networkId <= 0 || amount <= 0f)
                return;

            if (!em.HasBuffer<ContributedGemsElement>(homePlanetEntity))
                em.AddBuffer<ContributedGemsElement>(homePlanetEntity);

            var buffer = em.GetBuffer<ContributedGemsElement>(homePlanetEntity);
            for (int i = 0; i < buffer.Length; i++)
            {
                if (buffer[i].NetworkId != networkId)
                    continue;

                var entry = buffer[i];
                entry.Amount += amount;
                buffer[i] = entry;
                return;
            }

            buffer.Add(new ContributedGemsElement { NetworkId = networkId, Amount = amount });
        }

        /// <summary>Atomically deducts cost if balance sufficient; returns false on insufficient funds.</summary>
        public static bool TrySpend(EntityManager em, Entity homePlanetEntity, int networkId, float cost)
        {
            if (networkId <= 0 || cost <= 0f || !em.HasBuffer<ContributedGemsElement>(homePlanetEntity))
                return false;

            var buffer = em.GetBuffer<ContributedGemsElement>(homePlanetEntity);
            for (int i = 0; i < buffer.Length; i++)
            {
                if (buffer[i].NetworkId != networkId)
                    continue;
                if (buffer[i].Amount < cost)
                    return false;

                var entry = buffer[i];
                entry.Amount -= cost;
                buffer[i] = entry;
                return true;
            }

            return false;
        }

        /// <summary>Restores gems after a failed purchase or admin correction.</summary>
        public static void Refund(EntityManager em, Entity homePlanetEntity, int networkId, float amount) =>
            Add(em, homePlanetEntity, networkId, amount);
    }
}
