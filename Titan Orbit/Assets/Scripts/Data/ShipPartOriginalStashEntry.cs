using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Marks a disabled clone under <see cref="ShipFamilyPartMatch.OriginalStashName"/> so
    /// a moon-store discard can put the authored host part back — not delete the slot.
    /// <para>
    /// [HYBRID] Presentation only. Written at proxy Bind (before any remap) and read by
    /// <c>ShipComponentVisualSwapLogic</c> when the ghosted equipment buffer no longer
    /// owns that hardpoint. Paired with <see cref="ShipPartVisualSource"/> on the live
    /// remapped mesh (that marker is how we know a slot is foreign).
    /// </para>
    /// </summary>
    public sealed class ShipPartOriginalStashEntry : MonoBehaviour
    {
        /// <summary>
        /// Host slot name at stash time (e.g. <c>AstroEagle_Cockpit</c>). Clones keep this
        /// name after a remap, so restore can match the live hardpoint.
        /// </summary>
        public string HostSlotName;

        /// <summary>Relative path from the hull root to the original parent (empty = root child).</summary>
        public string ParentPath;

        /// <summary>Sibling index at stash time so L/R order stays stable.</summary>
        public int SiblingIndex;
    }
}
