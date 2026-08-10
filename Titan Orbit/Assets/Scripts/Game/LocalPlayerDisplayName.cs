using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Client-side preferred display name for the Main Menu name field.
    /// Persists across launches with PlayerPrefs so players do not re-type every session.
    /// Gameplay scoreboards read replicated <c>PlayerNameElement</c> once a match is joined;
    /// this store is the local source of truth before / between matches.
    /// </summary>
    public static class LocalPlayerDisplayName
    {
        /// <summary>PlayerPrefs key for the last name typed on the Main Menu.</summary>
        public const string PrefsKey = "TitanOrbit_PlayerDisplayName_v1";

        /// <summary>Max characters accepted (matches <c>FixedString64Bytes</c> budget with margin).</summary>
        public const int MaxLength = 24;

        /// <summary>Fallback when the player has never typed a name.</summary>
        public const string DefaultName = "Pilot";

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
        /// </summary>
        public static string Sanitize(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return string.Empty;

            // [STANDARD] Collapse whitespace and reject empty / oversized names.
            char[] chars = name.Trim().ToCharArray();
            int write = 0;
            for (int i = 0; i < chars.Length && write < MaxLength; i++)
            {
                char c = chars[i];
                if (char.IsControl(c))
                    continue;
                chars[write++] = c;
            }

            return write <= 0 ? string.Empty : new string(chars, 0, write);
        }
    }
}
