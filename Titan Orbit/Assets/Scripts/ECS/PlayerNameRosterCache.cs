using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Process-wide NetworkId → display name map for HUD, nameplates, and the team leaderboard.
    /// <para>
    /// [NETCODE] The match singleton's <see cref="PlayerNameElement"/> buffer lives on the server
    /// entity created by <see cref="GameBootstrapSystem"/>. That entity is <b>not</b> a ghost
    /// prefab, so <c>[GhostField]</c> on the buffer does not replicate to dedicated clients.
    /// This cache is the client-visible roster:
    /// </para>
    /// <list type="bullet">
    /// <item>Local Host: server handler writes here in-process — HUD sees names the same frame.</item>
    /// <item>Dedicated client: <see cref="PlayerNameAnnounceClientSystem"/> fills it from RPCs.</item>
    /// <item>Local player: client send path upserts immediately so your own plate never waits on RTT.</item>
    /// </list>
    /// Static so MonoBehaviours can read it without an ECS ship gather (join-crash safe).
    /// </summary>
    public static class PlayerNameRosterCache
    {
        /// <summary>NetworkId → sanitized display name. Capacity matches a full 5-team lobby.</summary>
        static readonly Dictionary<int, string> s_Names = new Dictionary<int, string>(32);

        /// <summary>
        /// [UNITY] Domain Reload off: static dictionaries survive Play Mode. Clear so the next
        /// Play does not show the previous match's names on the wrong NetworkIds.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStaticsForPlayMode() => Clear();

        /// <summary>Drops every stored name (session leave / Play Mode reset).</summary>
        public static void Clear() => s_Names.Clear();

        /// <summary>
        /// Inserts or replaces the display name for one player.
        /// Ignores NetworkId ≤ 0 and blank names.
        /// </summary>
        /// <param name="networkId">NetCode connection id (GhostOwner.NetworkId).</param>
        /// <param name="name">Already-sanitized display name.</param>
        public static void Upsert(int networkId, string name)
        {
            if (networkId <= 0 || string.IsNullOrWhiteSpace(name))
                return;
            s_Names[networkId] = name;
        }

        /// <summary>
        /// Inserts or replaces from a NetCode fixed string (server buffer / announce RPC).
        /// </summary>
        /// <param name="networkId">NetCode connection id.</param>
        /// <param name="name">Roster payload.</param>
        public static void Upsert(int networkId, in FixedString64Bytes name)
        {
            Upsert(networkId, name.ToString());
        }

        /// <summary>
        /// Looks up a stored display name.
        /// </summary>
        /// <param name="networkId">NetCode connection id.</param>
        /// <param name="name">Stored name when found.</param>
        /// <returns>True when this id has a non-blank name.</returns>
        public static bool TryGet(int networkId, out string name)
        {
            if (networkId > 0 &&
                s_Names.TryGetValue(networkId, out name) &&
                !string.IsNullOrWhiteSpace(name))
                return true;

            name = null;
            return false;
        }
    }
}
