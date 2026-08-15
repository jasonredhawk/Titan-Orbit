using Unity.Collections;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Shared sanitizer for player display names used on scoreboards and ship nameplates.
    /// The Main Menu stores a preferred name in PlayerPrefs; this helper is the server-safe
    /// version of that cleanup so a crafted RPC cannot inject control characters or overflow
    /// the NetCode <see cref="FixedString64Bytes"/> field.
    /// </summary>
    public static class PlayerDisplayNameUtil
    {
        /// <summary>
        /// Max characters accepted after trim. Matches the Main Menu input cap and leaves room
        /// inside <see cref="FixedString64Bytes"/> (UTF-8 bytes, not C# chars).
        /// </summary>
        public const int MaxLength = 24;

        /// <summary>Fallback when the player has never typed a name, or the RPC was empty.</summary>
        public const string DefaultName = "Pilot";

        /// <summary>
        /// Trims whitespace, strips control characters, and caps length.
        /// Returns empty when nothing usable remains — callers then apply <see cref="DefaultName"/>.
        /// </summary>
        /// <param name="name">Raw text from the Main Menu or an inbound RPC.</param>
        /// <returns>Cleaned name, or empty when the input had no printable characters.</returns>
        public static string Sanitize(string name)
        {
            // --- Reject empty ---
            if (string.IsNullOrWhiteSpace(name))
                return string.Empty;

            // --- Copy printable chars only ---
            // [STANDARD] Control characters (newline, bell, etc.) would break TMP nameplates.
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

        /// <summary>
        /// Sanitizes then packs into a NetCode-friendly fixed string.
        /// Empty input becomes <see cref="DefaultName"/> so the roster never stores a blank row.
        /// </summary>
        /// <param name="name">Raw display name.</param>
        /// <returns>Fixed string ready for <see cref="PlayerNameElement"/> / RPCs.</returns>
        public static FixedString64Bytes ToFixed(string name)
        {
            string cleaned = Sanitize(name);
            if (string.IsNullOrEmpty(cleaned))
                cleaned = DefaultName;

            FixedString64Bytes fs = default;
            fs.CopyFromTruncated(cleaned);
            return fs;
        }

        /// <summary>
        /// Sanitizes an inbound RPC payload. Empty / whitespace becomes <see cref="DefaultName"/>.
        /// </summary>
        /// <param name="rpcName">Name bytes from <see cref="SetPlayerNameCommand"/>.</param>
        /// <returns>Sanitized fixed string for the roster buffer.</returns>
        public static FixedString64Bytes SanitizeFixed(in FixedString64Bytes rpcName)
        {
            return ToFixed(rpcName.ToString());
        }
    }
}
