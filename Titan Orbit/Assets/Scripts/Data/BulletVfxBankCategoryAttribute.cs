using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Inspector popup of <see cref="BulletVfxBank"/> category names for an int bank index.
    /// Optional inherit row writes <c>-1</c> (type-table / named default).
    /// </summary>
    public sealed class BulletVfxBankCategoryAttribute : PropertyAttribute
    {
        /// <summary>When true, the first popup row is <see cref="InheritLabel"/> at index -1.</summary>
        public bool IncludeInheritOption { get; }

        /// <summary>Label for the -1 inherit row.</summary>
        public string InheritLabel { get; }

        public BulletVfxBankCategoryAttribute(
            bool includeInheritOption = false,
            string inheritLabel = "Type table default")
        {
            IncludeInheritOption = includeInheritOption;
            InheritLabel = string.IsNullOrWhiteSpace(inheritLabel)
                ? "Type table default"
                : inheritLabel;
        }
    }
}
