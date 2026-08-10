using System.Text;

namespace TitanOrbit.Core
{
    /// <summary>
    /// Human-readable labels from machine identifiers (CamelCase family ids, component keys).
    /// Used by orbit station UI, ship tree, and store rows so "AstroEagle" and "ProtonLegacy"
    /// display as spaced words. Client-only string formatting — no gameplay effect.
    /// </summary>
    public static class DisplayNameFormatting
    {
        /// <summary>
        /// Splits CamelCase into spaced words (e.g. ProtonLegacy → Proton Legacy). Inserts a space
        /// before uppercase letters that start a new word within the identifier.
        /// </summary>
        /// <param name="value">Raw id from data assets; null/empty returned unchanged.</param>
        /// <returns>Display-friendly label for UI text.</returns>
        public static string SplitCamelCase(string value)
        {
            // --- Guard empty input ---
            if (string.IsNullOrEmpty(value))
                return value;

            // --- Walk characters and insert word breaks ---
            var sb = new StringBuilder(value.Length + 8);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                // [STANDARD] Insert space before capitals that follow lowercase or precede lowercase.
                if (i > 0 && char.IsUpper(c)
                    && (char.IsLower(value[i - 1])
                        || (i + 1 < value.Length && char.IsLower(value[i + 1]))))
                {
                    sb.Append(' ');
                }

                sb.Append(c);
            }

            return sb.ToString();
        }
    }
}
