using UnityEngine;

namespace TitanOrbit
{
    /// <summary>
    /// What this client believes about its own beta-tester toggles.
    /// The Escape menu writes <see cref="RequestedFlags"/>. The server answers
    /// with a session RPC, and only then do ship upgrades and gear show as free.
    /// Other players never see these bits. Not a ghost.
    /// Paired with <c>BetaTesterMatch</c> on the server and <c>BetaTesterMenuPanel</c>.
    /// </summary>
    public static class BetaTesterClientFlags
    {
        /// <summary>Skip the gem cost of the next ship-upgrade purchase.</summary>
        public const byte FreeShipTree = 1;

        /// <summary>Skip the gem cost of gear and ship-component purchases.</summary>
        public const byte FreeGear = 2;

        /// <summary>Bits the server will store. Anything else is dropped.</summary>
        public const byte Mask = FreeShipTree | FreeGear;

        /// <summary>True after the server says this connection owns the beta-tester item.</summary>
        public static bool VerifiedOwned { get; private set; }

        /// <summary>
        /// True after at least one session reply. Until then prices stay normal
        /// even if the local cache says the item is owned.
        /// </summary>
        public static bool SessionKnown { get; private set; }

        /// <summary>Toggle bits the server will honor. Zero until the session reply.</summary>
        public static byte ConfirmedFlags { get; private set; }

        /// <summary>What the player last asked for. Saved in PlayerPrefs by the menu.</summary>
        public static byte RequestedFlags;

        /// <summary>True when this client's upgrade tree should render and click as free.</summary>
        public static bool FreeShipTreeActive =>
            VerifiedOwned && (ConfirmedFlags & FreeShipTree) != 0;

        /// <summary>True when this client's gear prices should render and click as free.</summary>
        public static bool FreeGearActive =>
            VerifiedOwned && (ConfirmedFlags & FreeGear) != 0;

        /// <summary>True when the server has confirmed the bits the menu is showing.</summary>
        public static bool IsConfirmed =>
            SessionKnown && VerifiedOwned && ConfirmedFlags == (byte)(RequestedFlags & Mask);

        /// <summary>[UNITY] Domain Reload off: the last match's free prices must not survive.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            ClearVerified();
            RequestedFlags = 0;
        }

        /// <summary>Stores the server's answer. Prices follow <see cref="ConfirmedFlags"/>.</summary>
        public static void ApplyServer(bool owned, byte flags)
        {
            SessionKnown = true;
            VerifiedOwned = owned;
            ConfirmedFlags = (byte)(flags & Mask);
        }

        /// <summary>Drops the server answer when the match ends. The saved request stays.</summary>
        public static void ClearVerified()
        {
            SessionKnown = false;
            VerifiedOwned = false;
            ConfirmedFlags = 0;
        }
    }
}
