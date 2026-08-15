using TitanOrbit.ECS;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Client-side preferred display name for the Main Menu name field.
    /// Persists across launches with PlayerPrefs so players do not re-type every session.
    /// <para>
    /// In a match, scoreboards and ship nameplates read <see cref="PlayerNameRosterCache"/>
    /// (filled by <see cref="SetPlayerNameCommand"/> / <see cref="PlayerNameAnnounceRpc"/>).
    /// This store is the local source of truth before / between matches, and the payload
    /// <see cref="PlayerNameRpcClient"/> sends after GoInGame.
    /// </para>
    /// </summary>
    public static class LocalPlayerDisplayName
    {
        /// <summary>PlayerPrefs key for the last name typed on the Main Menu.</summary>
        public const string PrefsKey = "TitanOrbit_PlayerDisplayName_v1";

        /// <summary>Max characters accepted (matches <see cref="PlayerDisplayNameUtil.MaxLength"/>).</summary>
        public const int MaxLength = PlayerDisplayNameUtil.MaxLength;

        /// <summary>Fallback when the player has never typed a name.</summary>
        public const string DefaultName = PlayerDisplayNameUtil.DefaultName;

        /// <summary>
        /// Reads the saved name, or <see cref="DefaultName"/> when empty / missing.
        /// </summary>
        public static string Get()
        {
            // --- Load + sanitize ---
            string raw = PlayerPrefs.GetString(PrefsKey, string.Empty);
            string cleaned = Sanitize(raw);
            return string.IsNullOrEmpty(cleaned) ? DefaultName : cleaned;
        }

        /// <summary>
        /// Saves a sanitized display name for the next launch and for match join RPCs.
        /// </summary>
        /// <param name="name">Raw text from the Main Menu input field.</param>
        public static void Set(string name)
        {
            // --- Persist ---
            string cleaned = Sanitize(name);
            if (string.IsNullOrEmpty(cleaned))
                cleaned = DefaultName;

            PlayerPrefs.SetString(PrefsKey, cleaned);
            PlayerPrefs.Save();
        }

        /// <summary>
        /// Trims whitespace, strips control characters, and caps length for NetCode FixedString use.
        /// Same rules the server applies to inbound <see cref="SetPlayerNameCommand"/> payloads.
        /// </summary>
        public static string Sanitize(string name) => PlayerDisplayNameUtil.Sanitize(name);
    }
}
