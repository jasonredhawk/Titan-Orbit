using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Bind-time local pose for one hardpoint. Hulls repeat the same child name on both
    /// sides (<c>AstroEagle_Weapon</c> at x = -4 and x = +4, <c>AstroEagle_Wing_2</c> flipped
    /// 180°). The name alone cannot tell those apart, so each live slot keeps its own pose.
    /// <para>
    /// [HYBRID] Presentation only. Stamped in <c>ShipComponentVisualSwapLogic</c> before
    /// attribute grow. Swaps copy this onto the replacement so scale does not compound and
    /// the opposite side keeps its own offset.
    /// </para>
    /// </summary>
    public sealed class ShipPartSlotPose : MonoBehaviour
    {
        public int Occurrence;
        public Vector3 AuthoredLocalPosition;
        public Quaternion AuthoredLocalRotation = Quaternion.identity;
        public Vector3 AuthoredLocalScale = Vector3.one;
        public bool Stamped;
    }
}
