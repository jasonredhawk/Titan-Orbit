using TitanOrbit.ECS;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Client-side preferred profile badge for the Main Menu picker.
    /// Persists across launches with PlayerPrefs so the player does not re-pick every session.
    /// <para>
    /// In a match, nameplates read <see cref="PlayerNameRosterCache"/> (filled by
    /// <see cref="SetPlayerNameCommand"/> / <see cref="PlayerNameAnnounceRpc"/>).
    /// This store is the local source of truth before / between matches, and the payload
    /// <see cref="PlayerNameRpcClient"/> sends after GoInGame.
    /// </para>
    /// Id 0 means no badge until the player picks one.
    /// </summary>
    public static class LocalPlayerBadge
    {
        /// <summary>PlayerPrefs key for the last badge chosen on the Main Menu.</summary>
        public const string PrefsKey = "TitanOrbit_PlayerBadgeId_v1";

        /// <summary>Reads the saved badge id, or <see cref="PlayerBadgeIdUtil.None"/> when missing / invalid.</summary>
        public static int Get()
        {
            int raw = PlayerPrefs.GetInt(PrefsKey, PlayerBadgeIdUtil.None);
            return PlayerBadgeIdUtil.Sanitize(raw);
        }

        /// <summary>
        /// Saves a sanitized badge id for the next launch and for match-join RPCs.
        /// </summary>
        /// <param name="badgeId">Filename-stable id from Badge (N).png, or 0 for none.</param>
        public static void Set(int badgeId)
        {
            int cleaned = PlayerBadgeIdUtil.Sanitize(badgeId);
            PlayerPrefs.SetInt(PrefsKey, cleaned);
            PlayerPrefs.Save();
        }
    }
}
