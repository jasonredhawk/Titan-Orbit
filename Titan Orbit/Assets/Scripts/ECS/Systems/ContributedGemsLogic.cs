using Unity.Entities;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [ECS/DOTS] Server-side contributed gem ledger on home planets (orbit-store currency).
    /// Buffer is per-planet, keyed by player network id. Not ghost-replicated per entry — clients
    /// request balance via <see cref="RequestContributedGemsCommand"/> RPC. Used by moon orbit store
    /// purchase validation and deposit credit from gem transfers.
    /// </summary>
    public static class ContributedGemsLogic
    {
        /// <summary>
        /// [TITAN-ORBIT] Reads a player's contributed balance at a home planet (0 if none or invalid id).
        /// </summary>
        /// <param name="em">Server EntityManager.</param>
        /// <param name="homePlanetEntity">Home planet entity with ContributedGemsElement buffer.</param>
        /// <param name="networkId">Player network id.</param>
        /// <returns>Spendable contributed gem balance.</returns>
        public static float Get(EntityManager em, Entity homePlanetEntity, int networkId)
        {
            // --- Guard invalid inputs ---
            if (networkId <= 0 || !em.HasBuffer<ContributedGemsElement>(homePlanetEntity))
                return 0f;

            // --- Linear search by network id ---
            var buffer = em.GetBuffer<ContributedGemsElement>(homePlanetEntity);
            for (int i = 0; i < buffer.Length; i++)
            {
                if (buffer[i].NetworkId == networkId)
                    return buffer[i].Amount;
            }

            return 0f;
        }

        /// <summary>
        /// [TITAN-ORBIT] Increments contributed gems (deposit) for a player at a home planet.
        /// Creates buffer entry if this player has no row yet.
        /// </summary>
        public static void Add(EntityManager em, Entity homePlanetEntity, int networkId, float amount)
        {
            // --- Guard invalid deposit ---
            if (networkId <= 0 || amount <= 0f)
                return;

            // --- Ensure buffer exists ---
            if (!em.HasBuffer<ContributedGemsElement>(homePlanetEntity))
                em.AddBuffer<ContributedGemsElement>(homePlanetEntity);

            var buffer = em.GetBuffer<ContributedGemsElement>(homePlanetEntity);
            for (int i = 0; i < buffer.Length; i++)
            {
                if (buffer[i].NetworkId != networkId)
                    continue;

                // --- Update existing row ---
                var entry = buffer[i];
                entry.Amount += amount;
                buffer[i] = entry;
                return;
            }

            // --- New player row ---
            buffer.Add(new ContributedGemsElement { NetworkId = networkId, Amount = amount });
        }

        /// <summary>
        /// [TITAN-ORBIT] Atomically deducts cost if balance sufficient; returns false on insufficient funds.
        /// </summary>
        /// <returns>True if deduction succeeded.</returns>
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

                // --- Deduct in place ---
                var entry = buffer[i];
                entry.Amount -= cost;
                buffer[i] = entry;
                return true;
            }

            return false;
        }

        /// <summary>
        /// [TITAN-ORBIT] Restores gems after a failed purchase or admin correction. Delegates to Add.
        /// </summary>
        public static void Refund(EntityManager em, Entity homePlanetEntity, int networkId, float amount) =>
            Add(em, homePlanetEntity, networkId, amount);
    }
}
