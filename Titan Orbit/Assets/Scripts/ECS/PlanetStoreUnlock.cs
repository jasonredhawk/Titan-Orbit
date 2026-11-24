using TitanOrbit;
using TitanOrbit.Core;
using Unity.Entities;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [NETCODE] Server-only memory of one connection's planet-store access.
    /// Not a ghost. Other clients never see it. Dies with the connection, so a
    /// new match starts locked again. <see cref="OrbitUnlocked"/> is the game
    /// purchase (every planet). Per-planet ads live in
    /// <see cref="PlanetStoreUnlockedPlanet"/> on the same connection.
    /// </summary>
    public struct PlanetStoreUnlockState : IComponentData
    {
        /// <summary>
        /// 1 when the checkout Worker (or a local-host entitlement) says this
        /// account bought the game. 0 until that answer arrives. A free player
        /// stays 0 for the whole match.
        /// </summary>
        public byte OrbitUnlocked;
    }

    /// <summary>
    /// [NETCODE] One planet whose ships, gear, and cards this connection unlocked
    /// with a rewarded ad during this match. The buffer lives on the connection
    /// entity next to <see cref="PlanetStoreUnlockState"/>. A second ad for the
    /// same <see cref="PlanetId"/> does not add a row.
    /// </summary>
    public struct PlanetStoreUnlockedPlanet : IBufferElementData
    {
        /// <summary>[TITAN-ORBIT] Stable <see cref="PlanetState.PlanetId"/> for this match only.</summary>
        public int PlanetId;
    }

    /// <summary>
    /// Server checks for the free-player planet store lock. Home worlds and
    /// Orbit Unlocked owners skip the ad. Everyone else needs this planet's id
    /// in the connection buffer. Called from purchase RPCs, not every tick.
    /// Paired with <see cref="MoonOrbitStoreSystem"/> and the orbit menu lock plate.
    /// </summary>
    public static class PlanetStoreUnlock
    {
        /// <summary>
        /// True when this ship may buy ships, gear, and cards at
        /// <paramref name="planet"/>. The team spawn capital is always open.
        /// A captured moon, including another team's capital, is not.
        /// </summary>
        public static bool IsTeamSpawnHome(in PlanetState planet, TeamId team)
        {
            // Homes are stamped PlanetId == team number at map build. A captured
            // enemy capital keeps that other team's id, so owning it does not
            // make it this ship's free store.
            return planet.IsHomePlanet
                && planet.Ownership == team
                && team != TeamId.None
                && planet.PlanetId == (int)team;
        }

        /// <summary>
        /// True when this connection may use a non-home store: they bought the
        /// game, or they already watched this match's ad for <paramref name="planetId"/>.
        /// </summary>
        public static bool Allows(EntityManager em, int networkId, int planetId)
        {
            if (OwnsOrbitUnlocked(em, networkId))
                return true;
            return IsPlanetUnlocked(em, networkId, planetId);
        }

        /// <summary>
        /// True when this connection owns Orbit Unlocked. The local host uses
        /// the in-process entitlement so Editor revoke locks the store again
        /// without waiting for Cloud Save. Dedicated-server players use the
        /// Worker bit on the connection. Missing bit fails closed.
        /// </summary>
        public static bool OwnsOrbitUnlocked(EntityManager em, int networkId)
        {
            // --- Local host: this process is the buyer ---
            if (OrbitUnlockedGiftServerSystem.IsLocalHostNetwork(networkId))
                return PlanetStoreUnlockFlags.LocalAccountOwned;

            // --- Dedicated / remote: Worker result on the connection ---
            if (!BetaTesterMatch.TryFindConnection(em, networkId, out Entity connection))
                return false;
            if (!em.HasComponent<PlanetStoreUnlockState>(connection))
                return false;
            return em.GetComponentData<PlanetStoreUnlockState>(connection).OrbitUnlocked != 0;
        }

        /// <summary>
        /// True when this match already recorded an ad unlock for
        /// <paramref name="planetId"/> on this connection.
        /// </summary>
        public static bool IsPlanetUnlocked(EntityManager em, int networkId, int planetId)
        {
            if (planetId <= 0)
                return false;
            if (!BetaTesterMatch.TryFindConnection(em, networkId, out Entity connection))
                return false;
            if (!em.HasBuffer<PlanetStoreUnlockedPlanet>(connection))
                return false;

            DynamicBuffer<PlanetStoreUnlockedPlanet> buffer =
                em.GetBuffer<PlanetStoreUnlockedPlanet>(connection);
            for (int i = 0; i < buffer.Length; i++)
            {
                if (buffer[i].PlanetId == planetId)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Records a completed ad for <paramref name="planetId"/>. No-op when
        /// that id is already stored. The caller has already checked the ship
        /// is fully docked there. Returns false when the connection is gone.
        /// </summary>
        public static bool RememberPlanet(EntityManager em, int networkId, int planetId)
        {
            if (planetId <= 0)
                return false;
            if (!BetaTesterMatch.TryFindConnection(em, networkId, out Entity connection))
                return false;

            // --- One row per planet ---
            DynamicBuffer<PlanetStoreUnlockedPlanet> buffer =
                em.HasBuffer<PlanetStoreUnlockedPlanet>(connection)
                    ? em.GetBuffer<PlanetStoreUnlockedPlanet>(connection)
                    : em.AddBuffer<PlanetStoreUnlockedPlanet>(connection);
            for (int i = 0; i < buffer.Length; i++)
            {
                if (buffer[i].PlanetId == planetId)
                    return true;
            }

            buffer.Add(new PlanetStoreUnlockedPlanet { PlanetId = planetId });
            return true;
        }

        /// <summary>
        /// Copies a Worker <c>orbitUnlocked</c> answer onto the connection.
        /// A false answer does not clear a purchase already stored this match
        /// (a gift or an earlier true lookup). A missing connection is ignored.
        /// </summary>
        public static void ApplyOrbitUnlocked(EntityManager em, int networkId, bool owned)
        {
            if (!owned)
                return;
            if (!BetaTesterMatch.TryFindConnection(em, networkId, out Entity connection))
                return;

            PlanetStoreUnlockState row = em.HasComponent<PlanetStoreUnlockState>(connection)
                ? em.GetComponentData<PlanetStoreUnlockState>(connection)
                : default;
            row.OrbitUnlocked = 1;
            if (em.HasComponent<PlanetStoreUnlockState>(connection))
                em.SetComponentData(connection, row);
            else
                em.AddComponentData(connection, row);
        }
    }
}
