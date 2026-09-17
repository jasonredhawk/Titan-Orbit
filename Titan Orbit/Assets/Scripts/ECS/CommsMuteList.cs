using System.Collections.Generic;
using UnityEngine;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Client-only set of players this machine does not want to hear from Comms Matrix.
    /// Comms Matrix is the hold-S keyword chip UI — not voice chat and not free-form text.
    /// <para>
    /// Key is <c>GhostOwner.NetworkId</c> (NetCode connection id). The same id rides on
    /// <see cref="ShipCommsInbox.Callout.NetworkId"/>, leaderboard rows, and nameplates.
    /// Mute is presentation only: the server still broadcasts; this client drops the chips
    /// and path pings. Other players still see the speaker.
    /// </para>
    /// <para>
    /// [TITAN-ORBIT] NetworkId remaps every match, so this set is match-scoped — never
    /// written to PlayerPrefs. Cleared on Play Mode reset and session leave (same places
    /// as <see cref="PlayerNameRosterCache"/>).
    /// </para>
    /// Static so the ECS inbox and UGUI leaderboard can share one list without an entity gather.
    /// </summary>
    public static class CommsMuteList
    {
        /// <summary>
        /// Muted speaker ids. Capacity 32 matches a full 5-team lobby so the first
        /// mute in a match does not allocate.
        /// </summary>
        static readonly HashSet<int> s_Muted = new HashSet<int>(32);

        /// <summary>
        /// [UNITY] Domain Reload off: static HashSets survive Play Mode. Clear so the
        /// next Play does not mute a recycled NetworkId that now belongs to someone else.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStaticsForPlayMode() => Clear();

        /// <summary>
        /// Drops every mute (session leave / Play Mode reset). After this, every
        /// incoming callout paints again.
        /// </summary>
        public static void Clear() => s_Muted.Clear();

        /// <summary>
        /// True when this client should ignore Comms Matrix from <paramref name="networkId"/>.
        /// NetworkId ≤ 0 is never muted (invalid / unassigned speaker).
        /// </summary>
        /// <param name="networkId">Speaker GhostOwner.NetworkId.</param>
        public static bool IsMuted(int networkId)
        {
            return networkId > 0 && s_Muted.Contains(networkId);
        }

        /// <summary>
        /// Adds or removes one speaker. Ignores NetworkId ≤ 0 so we never store junk keys.
        /// The leaderboard hides the button on the local row, so the local id should not
        /// arrive here — this method does not look up the local ship (ECS cannot reference
        /// <c>EcsGameBridge</c>).
        /// </summary>
        /// <param name="networkId">Speaker to mute or unmute.</param>
        /// <param name="muted">True = hide their chips and paths; false = hear them again.</param>
        public static void SetMuted(int networkId, bool muted)
        {
            if (networkId <= 0)
                return;

            if (muted)
                s_Muted.Add(networkId);
            else
                s_Muted.Remove(networkId);
        }

        /// <summary>
        /// Flips mute for one speaker. Called from the leaderboard mute button click.
        /// </summary>
        /// <param name="networkId">Speaker GhostOwner.NetworkId.</param>
        /// <returns>True when they are muted after the click; false when heard again or id is invalid.</returns>
        public static bool Toggle(int networkId)
        {
            if (networkId <= 0)
                return false;

            // --- Flip ---
            // Remove first: if they were muted, they are now heard. If they were not
            // in the set, Add puts them in and we report muted.
            if (s_Muted.Remove(networkId))
                return false;

            s_Muted.Add(networkId);
            return true;
        }
    }
}
