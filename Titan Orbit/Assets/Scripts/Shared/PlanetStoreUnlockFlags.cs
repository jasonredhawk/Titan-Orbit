using UnityEngine;

namespace TitanOrbit
{
    /// <summary>
    /// What this process believes about its own Orbit Unlocked purchase.
    /// The local-host server reads <see cref="LocalAccountOwned"/> so Editor play
    /// and a listen server can open every planet store without the checkout Worker.
    /// A dedicated server process never sets this. Remote clients still need the
    /// Worker lookup on their connection. Not a ghost. Other players never see it.
    /// Paired with <c>PlanetStoreUnlock</c> on the server and
    /// <c>TitanOrbitEntitlements</c> on the client.
    /// </summary>
    public static class PlanetStoreUnlockFlags
    {
        /// <summary>
        /// True when this machine's signed-in account owns Orbit Unlocked.
        /// Guests and dedicated servers stay false. Set from the entitlement
        /// session, including the Editor override, so a domain reload that
        /// keeps the entitlement also keeps this bit.
        /// </summary>
        public static bool LocalAccountOwned { get; private set; }

        /// <summary>
        /// [UNITY] Domain Reload off: do not keep last session's purchase into the next Play.
        /// The entitlement static ctor and session load write the bit again.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            LocalAccountOwned = false;
        }

        /// <summary>
        /// Publishes the account flag for the local-host server in this process.
        /// Called even when the value did not change, so a domain-reload reset of
        /// this static still catches a purchase that was already true.
        /// </summary>
        public static void SetLocalAccountOwned(bool owned)
        {
            LocalAccountOwned = owned;
        }
    }
}
