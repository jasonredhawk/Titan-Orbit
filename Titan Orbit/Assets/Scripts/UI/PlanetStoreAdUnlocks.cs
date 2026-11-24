using System.Collections.Generic;
using TitanOrbit.Data;
using UnityEngine;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Planets this client already opened with one rewarded ad during the current match.
    /// The server keeps the same list on the connection and is what actually allows a
    /// purchase. This set only hides the lock plate without waiting for a ghost.
    /// A new match seed clears it, because planet ids are handed out again from 100.
    /// Client only. Not saved. Paired with <c>PlanetStoreUnlock</c> on the server.
    /// </summary>
    public static class PlanetStoreAdUnlocks
    {
        /// <summary>Planet ids unlocked since <see cref="_seed"/> was stored.</summary>
        static readonly HashSet<int> Ids = new HashSet<int>();

        /// <summary>Match seed these ids belong to. 0 until the map seed is known.</summary>
        static uint _seed;

        /// <summary>
        /// [UNITY] Domain Reload off: last match's planet ids must not open the next map.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Ids.Clear();
            _seed = 0;
        }

        /// <summary>
        /// Drops unlocks when the map seed changes. A zero seed is ignored so a menu
        /// that opens before hydrate does not wipe an unlock from this same match.
        /// </summary>
        public static void SyncMatch()
        {
            uint seed = ShipFamilyMatchBonuses.ActiveMatchSeed;
            if (seed == 0 || seed == _seed)
                return;
            _seed = seed;
            Ids.Clear();
        }

        /// <summary>True after a completed ad for this planet in the current match.</summary>
        public static bool Contains(int planetId)
        {
            return planetId > 0 && Ids.Contains(planetId);
        }

        /// <summary>Hides the lock plate for this planet until the match seed changes.</summary>
        public static void Remember(int planetId)
        {
            SyncMatch();
            if (planetId > 0)
                Ids.Add(planetId);
        }
    }
}
