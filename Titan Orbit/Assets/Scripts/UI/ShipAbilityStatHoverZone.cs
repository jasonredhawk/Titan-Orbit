using UnityEngine;
using UnityEngine.EventSystems;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Invisible hit target on an ability quick-stat chip. Forwards pointer enter/exit to
    /// <see cref="ShipAttributeUpgradeHUD"/> so calculation cards appear while hovered.
    /// [UNITY] Requires an EventSystem + GraphicRaycaster on the HUD canvas.
    /// </summary>
    public class ShipAbilityStatHoverZone : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        /// <summary>Ability index 0–9 (matches bottom Ship Ability button order).</summary>
        public int AbilityIndex;

        /// <summary>Owner that owns the floating tip panel.</summary>
        public ShipAttributeUpgradeHUD Owner;

        /// <summary>[UNITY] Pointer entered this chip — show that ability's breakdown.</summary>
        public void OnPointerEnter(PointerEventData eventData)
        {
            if (Owner != null)
                Owner.ShowAbilityStatTooltip(AbilityIndex);
        }

        /// <summary>[UNITY] Pointer left this chip — hide if this ability is still the active tip.</summary>
        public void OnPointerExit(PointerEventData eventData)
        {
            if (Owner != null)
                Owner.HideAbilityStatTooltip(AbilityIndex);
        }
    }
}
