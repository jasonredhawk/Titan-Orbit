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
        /// Splits CamelCase and digit runs into spaced words (ProtonLegacy → Proton Legacy,
        /// DoubleBarrel → Double Barrel, GalacticOkamoto15 → Galactic Okamoto 15).
        /// Idempotent when the string is already spaced.
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
                if (i > 0 && !char.IsWhiteSpace(value[i - 1]) && !char.IsWhiteSpace(c))
                {
                    char prev = value[i - 1];
                    bool camelBreak = char.IsUpper(c)
                        && (char.IsLower(prev)
                            || (i + 1 < value.Length && char.IsLower(value[i + 1])));
                    bool letterToDigit = char.IsDigit(c) && char.IsLetter(prev);
                    bool digitToLetter = char.IsLetter(c) && char.IsDigit(prev);
                    if (camelBreak || letterToDigit || digitToLetter)
                        sb.Append(' ');
                }

                sb.Append(c);
            }

            return sb.ToString();
        }
    }
}
