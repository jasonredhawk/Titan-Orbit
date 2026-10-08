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

        /// <summary>
        /// Index among same-named hardpoints (two <c>AstroEagle_Wing_2</c> children are 0 and 1).
        /// The second side is a different position or a 180° flip, not a different name.
        /// </summary>
        public int Occurrence;

        /// <summary>
        /// True when this copy was taken at Bind, before attribute grow. A later stash of an
        /// already swapped mesh keeps the geometry but must not be used as the hardpoint pose.
        /// </summary>
        public bool PoseIsAuthored;
    }
}
