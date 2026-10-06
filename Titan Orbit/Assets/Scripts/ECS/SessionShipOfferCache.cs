using TitanOrbit.Core;
using Unity.Entities;
using UnityEngine;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Client copy of the server's saved-ship summary. The rejoin screen reads this
    /// when the hull itself is no longer in the world. Cleared on leave and on a choice.
    /// </summary>
    public static class SessionShipOfferCache
    {
        public static bool HasOffer { get; private set; }
        public static ShipState Summary { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStaticsForPlayMode() => Clear();

        public static void Set(in ShipState ship)
        {
            if (ship.Team == TeamId.None || ship.AwaitingTeamSelection)
            {
                Clear();
                return;
            }

            HasOffer = true;
            Summary = ship;
        }

        public static void Clear()
        {
            HasOffer = false;
            Summary = default;
        }
    }
}
