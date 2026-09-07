using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Inspector popup of canonical <see cref="ShipFamilyPartTypes"/> group ids for a string field.
    /// </summary>
    public sealed class ShipFamilyPartTypeAttribute : PropertyAttribute
    {
        /// <summary>
        /// When true (Name Mappings), the popup includes Unmapped and Ignore.
        /// When false (Part Profiles), only the eight core groups.
        /// </summary>
        public bool IncludeUnmappedAndIgnore { get; }

        public ShipFamilyPartTypeAttribute(bool includeUnmappedAndIgnore = true)
        {
            IncludeUnmappedAndIgnore = includeUnmappedAndIgnore;
        }
    }
}
