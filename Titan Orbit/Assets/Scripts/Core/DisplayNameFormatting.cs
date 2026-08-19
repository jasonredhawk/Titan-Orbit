using System.Text;

namespace TitanOrbit.Core
{
    /// <summary>
    /// Human-readable labels from machine identifiers (CamelCase family ids, prefab names).
    /// Used by orbit station UI, ship tree, and store rows so "AstroEagle" and
    /// "SpaceExcalibur_7" display as spaced words. Client-only string formatting — no gameplay effect.
    /// </summary>
    public static class DisplayNameFormatting
    {
        /// <summary>
        /// Splits CamelCase, digit runs, and underscores into spaced words
        /// (ProtonLegacy → Proton Legacy, SpaceExcalibur_7 → Space Excalibur 7,
        /// GalacticOkamoto15 → Galactic Okamoto 15). Idempotent when the string is already spaced.
        /// </summary>
        /// <param name="value">Raw id from data assets; null/empty returned unchanged.</param>
        /// <returns>Display-friendly label for UI text.</returns>
        public static string SplitCamelCase(string value)
        {
            // --- Guard empty input ---
            if (string.IsNullOrEmpty(value))
                return value;

            // --- Walk characters and insert word breaks ---
            // Underscores are treated as spaces (SpaceExcalibur_7 → Space Excalibur 7).
            // CamelCase and letter↔digit edges get a space so NightAye16 → Night Aye 16.
            var sb = new StringBuilder(value.Length + 8);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];

                // Prefab tokens use Family_Index (SpaceExcalibur_7). Collapse "_" to one space.
                if (c == '_')
                {
                    if (sb.Length > 0 && sb[sb.Length - 1] != ' ')
                        sb.Append(' ');
                    continue;
                }

                if (sb.Length > 0 && !char.IsWhiteSpace(c))
                {
                    char prev = sb[sb.Length - 1];
                    if (!char.IsWhiteSpace(prev))
                    {
                        // lower→Upper, or acronym→Word (XMLParser), plus letter↔digit.
                        bool camelBreak = char.IsUpper(c)
                            && (char.IsLower(prev)
                                || (i + 1 < value.Length && char.IsLower(value[i + 1])));
                        bool letterToDigit = char.IsDigit(c) && char.IsLetter(prev);
                        bool digitToLetter = char.IsLetter(c) && char.IsDigit(prev);
                        if (camelBreak || letterToDigit || digitToLetter)
                            sb.Append(' ');
                    }
                }

                sb.Append(c);
            }

            return sb.ToString().Trim();
        }

        /// <summary>
        /// Player-facing ship name from a prefab or chassis token.
        /// Strips Unity <c>(Clone)</c> then runs <see cref="SplitCamelCase"/>.
        /// <c>SpaceExcalibur_7</c> → Space Excalibur 7.
        /// </summary>
        /// <param name="prefabName">Prefab GameObject name or chassis id. Null/blank → empty.</param>
        public static string FormatPrefabShipName(string prefabName)
        {
            if (string.IsNullOrWhiteSpace(prefabName))
                return string.Empty;

            // Unity instances append " (Clone)" — strip so the Orbit Menu stays clean.
            string name = prefabName.Replace("(Clone)", string.Empty).Trim();
            return SplitCamelCase(name);
        }
    }
}
