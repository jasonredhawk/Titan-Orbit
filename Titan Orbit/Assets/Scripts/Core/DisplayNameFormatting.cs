using System;
using System.Text;

namespace TitanOrbit.Core
{
    /// <summary>
    /// Human-readable labels from machine identifiers (CamelCase family ids, prefab names, component ids).
    /// Used by orbit station UI, ship tree, and store rows so "AstroEagle",
    /// "SpaceExcalibur_7", and "SpaceExcalibur_Tiny_Thrusters" display as spaced words.
    /// Client-only string formatting — no gameplay effect.
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

        /// <summary>
        /// Player-facing part label from a family-prefixed component id.
        /// <c>SpaceExcalibur_Tiny_Thrusters</c> → Tiny Thrusters.
        /// Strips <paramref name="familyId"/> when provided; otherwise drops a leading
        /// PascalCase family token (two or more capitals) so <c>Engine_2</c> stays Engine 2.
        /// </summary>
        /// <param name="componentId">Catalog id such as SpaceExcalibur_Tiny_Thrusters.</param>
        /// <param name="familyId">Optional family prefix to strip (SpaceExcalibur).</param>
        public static string FormatComponentDisplayName(string componentId, string familyId = null)
        {
            if (string.IsNullOrWhiteSpace(componentId))
                return string.Empty;

            string id = componentId.Trim();
            id = StripFamilyPrefix(id, familyId);
            return SplitCamelCase(id);
        }

        /// <summary>
        /// Removes <c>FamilyId_</c> from a component id. When family id is unknown,
        /// only the first underscore token is dropped if it looks like a family name
        /// (letters only, at least two capitals) and the remainder still has a letter.
        /// </summary>
        static string StripFamilyPrefix(string componentId, string familyId)
        {
            if (string.IsNullOrEmpty(componentId))
                return componentId;

            if (!string.IsNullOrWhiteSpace(familyId))
            {
                string fid = familyId.Trim();
                if (componentId.StartsWith(fid + "_", StringComparison.OrdinalIgnoreCase))
                    return componentId.Substring(fid.Length + 1);
                return componentId;
            }

            int underscore = componentId.IndexOf('_');
            if (underscore <= 0 || underscore >= componentId.Length - 1)
                return componentId;

            string head = componentId.Substring(0, underscore);
            if (!LooksLikeFamilyPrefix(head))
                return componentId;

            string rest = componentId.Substring(underscore + 1);
            if (!ContainsLetter(rest))
                return componentId;

            return rest;
        }

        /// <summary>
        /// True for tokens like SpaceExcalibur / AstroEagle (compound PascalCase, no digits).
        /// False for part tokens like Engine, Tiny, Wing.
        /// </summary>
        static bool LooksLikeFamilyPrefix(string token)
        {
            if (string.IsNullOrEmpty(token))
                return false;

            int uppers = 0;
            for (int i = 0; i < token.Length; i++)
            {
                char c = token[i];
                if (!char.IsLetter(c))
                    return false;
                if (char.IsUpper(c))
                    uppers++;
            }

            return uppers >= 2;
        }

        static bool ContainsLetter(string value)
        {
            if (string.IsNullOrEmpty(value))
                return false;
            for (int i = 0; i < value.Length; i++)
            {
                if (char.IsLetter(value[i]))
                    return true;
            }

            return false;
        }
    }
}
