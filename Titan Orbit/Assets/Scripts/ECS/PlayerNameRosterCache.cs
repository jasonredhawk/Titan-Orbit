using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Process-wide NetworkId → display name + badge map for HUD, nameplates, and the team leaderboard.
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
        struct Entry
        {
            public string DisplayName;
            public int BadgeId;
        }

        /// <summary>NetworkId → sanitized identity. Capacity matches a full 5-team lobby.</summary>
        static readonly Dictionary<int, Entry> s_Entries = new Dictionary<int, Entry>(32);

        /// <summary>
        /// [UNITY] Domain Reload off: static dictionaries survive Play Mode. Clear so the next
        /// Play does not show the previous match's names on the wrong NetworkIds.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStaticsForPlayMode() => Clear();

        /// <summary>Drops every stored name and badge (session leave / Play Mode reset).</summary>
        public static void Clear() => s_Entries.Clear();

        /// <summary>
        /// Inserts or replaces the display name for one player without wiping a known badge.
        /// Ignores NetworkId ≤ 0 and blank names.
        /// </summary>
        /// <param name="networkId">NetCode connection id (GhostOwner.NetworkId).</param>
        /// <param name="name">Already-sanitized display name.</param>
        public static void Upsert(int networkId, string name)
        {
            if (networkId <= 0 || string.IsNullOrWhiteSpace(name))
                return;

            if (s_Entries.TryGetValue(networkId, out Entry existing))
            {
                existing.DisplayName = name;
                s_Entries[networkId] = existing;
                return;
            }

            s_Entries[networkId] = new Entry
            {
                DisplayName = name,
                BadgeId = PlayerBadgeIdUtil.None,
            };
        }

        /// <summary>
        /// Inserts or replaces name and badge together (announce RPC / local publish).
        /// </summary>
        /// <param name="networkId">NetCode connection id.</param>
        /// <param name="name">Already-sanitized display name.</param>
        /// <param name="badgeId">Filename-stable badge id, or 0 for none.</param>
        public static void Upsert(int networkId, string name, int badgeId)
        {
            if (networkId <= 0 || string.IsNullOrWhiteSpace(name))
                return;

            s_Entries[networkId] = new Entry
            {
                DisplayName = name,
                BadgeId = PlayerBadgeIdUtil.Sanitize(badgeId),
            };
        }

        /// <summary>
        /// Inserts or replaces from a NetCode fixed string (server buffer / announce RPC).
        /// Name-only — does not wipe a known badge.
        /// </summary>
        /// <param name="networkId">NetCode connection id.</param>
        /// <param name="name">Roster payload.</param>
        public static void Upsert(int networkId, in FixedString64Bytes name)
        {
            Upsert(networkId, name.ToString());
        }

        /// <summary>
        /// Inserts or replaces name and badge from a NetCode fixed string.
        /// </summary>
        public static void Upsert(int networkId, in FixedString64Bytes name, int badgeId)
        {
            Upsert(networkId, name.ToString(), badgeId);
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
                s_Entries.TryGetValue(networkId, out Entry entry) &&
                !string.IsNullOrWhiteSpace(entry.DisplayName))
            {
                name = entry.DisplayName;
                return true;
            }

            name = null;
            return false;
        }

        /// <summary>
        /// Looks up a stored profile badge id (0 when unknown or none).
        /// </summary>
        public static bool TryGetBadgeId(int networkId, out int badgeId)
        {
            if (networkId > 0 && s_Entries.TryGetValue(networkId, out Entry entry))
            {
                badgeId = entry.BadgeId;
                return true;
            }

            badgeId = PlayerBadgeIdUtil.None;
            return false;
        }
    }
}
