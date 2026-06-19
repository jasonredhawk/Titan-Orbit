using System.Text;

namespace TitanOrbit.Core
{
    /// <summary>Human-readable labels from identifiers (e.g. CamelCase family ids).</summary>
    public static class DisplayNameFormatting
    {
        /// <summary>Splits CamelCase into spaced words (e.g. ProtonLegacy → Proton Legacy).</summary>
        public static string SplitCamelCase(string value)
        {
            if (string.IsNullOrEmpty(value))
                return value;

            var sb = new StringBuilder(value.Length + 8);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
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
